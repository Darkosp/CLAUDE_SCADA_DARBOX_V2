using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// The message format on the wire (ADR-0017): our own, carrying exactly what ADR-0003 defines
/// for a value, and a version from the first message on.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "version": 1,
///   "samples": [
///     { "tagId": "…", "sourceTimestampUtc": "2026-09-24T12:00:00Z", "quality": "Good",
///       "value": { "kind": "numeric", "numeric": 4.2 } }
///   ]
/// }
/// </code>
/// <para>
/// A tag is named by its stable id (ADR-0001), never its display path. The value kinds are the
/// four of <see cref="TagValue"/>, plus <c>"none"</c>, allowed only when the quality is not Good:
/// a Good sample without a value is a contradiction and is refused, never read as zero.
/// </para>
/// <para>
/// This is a compatibility surface we own across versions. A message of any other version is
/// refused whole rather than guessed at.
/// </para>
/// </remarks>
public static class SamplePayload
{
    public const int Version = 1;

    /// <summary>Reads one message for the given tags.</summary>
    /// <param name="tags">The tags the receiving device has, by id; samples for any other tag are not accepted.</param>
    public static SamplePayloadResult Read(string json, IReadOnlyDictionary<Guid, DriverTag> tags)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            return SamplePayloadResult.Refused($"not JSON: {exception.Message}");
        }

        if (root is not JsonObject message)
        {
            return SamplePayloadResult.Refused("not a JSON object");
        }

        if (message["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version))
        {
            return SamplePayloadResult.Refused("no version");
        }

        if (version != Version)
        {
            return SamplePayloadResult.Refused($"version {version} is not understood (this build reads version {Version})");
        }

        if (message["samples"] is not JsonArray samples)
        {
            return SamplePayloadResult.Refused("no samples array");
        }

        var accepted = new List<TagReading>(samples.Count);
        var rejected = new List<string>();
        var unknown = 0;

        for (var index = 0; index < samples.Count; index++)
        {
            switch (ReadSample(samples[index], tags, out var reading))
            {
                case null when reading is not null:
                    accepted.Add(reading);
                    break;
                case SampleProblem.UnknownTag:
                    unknown++;
                    break;
                case { } problem:
                    rejected.Add($"sample {index}: {Describe(problem)}");
                    break;
            }
        }

        return new SamplePayloadResult(accepted, rejected, unknown, Refusal: null);
    }

    /// <summary>Writes samples as one message.</summary>
    public static string Write(IEnumerable<TagReading> samples)
    {
        var array = new JsonArray();
        foreach (var sample in samples)
        {
            array.Add(new JsonObject
            {
                ["tagId"] = sample.TagId.ToString(),
                ["sourceTimestampUtc"] = sample.SourceTimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["quality"] = sample.Quality.ToString(),
                ["value"] = WriteValue(sample.Value),
            });
        }

        return new JsonObject { ["version"] = Version, ["samples"] = array }.ToJsonString();
    }

    private static JsonObject WriteValue(TagValue? value) => value switch
    {
        TagValue.Numeric numeric => new JsonObject { ["kind"] = "numeric", ["numeric"] = numeric.Value },
        TagValue.Boolean boolean => new JsonObject { ["kind"] = "boolean", ["boolean"] = boolean.Value },
        TagValue.Text text => new JsonObject { ["kind"] = "text", ["text"] = text.Value },
        TagValue.Discrete discrete => new JsonObject { ["kind"] = "discrete", ["code"] = discrete.Code, ["label"] = discrete.Label },
        _ => new JsonObject { ["kind"] = "none" },
    };

    private enum SampleProblem
    {
        NotAnObject,
        NoTagId,
        UnknownTag,
        NoTimestamp,
        TimestampWithoutOffset,
        UnknownQuality,
        BadValue,
        GoodWithoutValue,
        WrongKind,
    }

    private static SampleProblem? ReadSample(JsonNode? node, IReadOnlyDictionary<Guid, DriverTag> tags, out TagReading? reading)
    {
        reading = null;

        if (node is not JsonObject sample)
        {
            return SampleProblem.NotAnObject;
        }

        if (!Guid.TryParse(sample["tagId"]?.GetValueKind() == JsonValueKind.String ? sample["tagId"]!.GetValue<string>() : null, out var tagId))
        {
            return SampleProblem.NoTagId;
        }

        if (!tags.TryGetValue(tagId, out var tag))
        {
            return SampleProblem.UnknownTag;
        }

        if (sample["sourceTimestampUtc"]?.GetValueKind() != JsonValueKind.String)
        {
            return SampleProblem.NoTimestamp;
        }

        // An explicit offset or Z only. A time without one would be read in whatever zone this
        // server is in — a measurement time made up by the reader, not given by the source.
        var text = sample["sourceTimestampUtc"]!.GetValue<string>();
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var measuredAt)
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out measuredAt))
        {
            return SampleProblem.NoTimestamp;
        }

        if (!HasOffset(text))
        {
            return SampleProblem.TimestampWithoutOffset;
        }

        if (sample["quality"]?.GetValueKind() != JsonValueKind.String
            || !Enum.TryParse<Quality>(sample["quality"]!.GetValue<string>(), ignoreCase: false, out var quality)
            || !Enum.IsDefined(quality))
        {
            return SampleProblem.UnknownQuality;
        }

        if (!TryReadValue(sample["value"], out var value))
        {
            return SampleProblem.BadValue;
        }

        if (value is null && quality == Quality.Good)
        {
            return SampleProblem.GoodWithoutValue;
        }

        if (value is not null && value.Kind != tag.ValueKind)
        {
            return SampleProblem.WrongKind;
        }

        reading = new TagReading(tagId, value, measuredAt.ToUniversalTime(), quality);
        return null;
    }

    private static bool HasOffset(string text)
    {
        var time = text.IndexOf('T', StringComparison.Ordinal);
        if (time < 0)
        {
            return false;
        }

        var clock = text[(time + 1)..];
        return clock.EndsWith('Z') || clock.EndsWith('z') || clock.Contains('+') || clock.Contains('-');
    }

    private static bool TryReadValue(JsonNode? node, out TagValue? value)
    {
        value = null;

        if (node is not JsonObject body || body["kind"]?.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        switch (body["kind"]!.GetValue<string>())
        {
            case "none":
                return true;
            case "numeric" when body["numeric"]?.GetValueKind() == JsonValueKind.Number:
                var number = body["numeric"]!.GetValue<double>();
                if (!double.IsFinite(number))
                {
                    return false;
                }

                value = new TagValue.Numeric(number);
                return true;
            case "boolean" when body["boolean"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                value = new TagValue.Boolean(body["boolean"]!.GetValue<bool>());
                return true;
            case "text" when body["text"]?.GetValueKind() == JsonValueKind.String:
                value = new TagValue.Text(body["text"]!.GetValue<string>());
                return true;
            case "discrete" when body["code"] is JsonValue codeNode && codeNode.TryGetValue<int>(out var code):
                var label = body["label"]?.GetValueKind() == JsonValueKind.String ? body["label"]!.GetValue<string>() : null;
                value = new TagValue.Discrete(code, label);
                return true;
            default:
                return false;
        }
    }

    private static string Describe(SampleProblem problem) => problem switch
    {
        SampleProblem.NotAnObject => "not an object",
        SampleProblem.NoTagId => "no tag id",
        SampleProblem.NoTimestamp => "no readable source timestamp",
        SampleProblem.TimestampWithoutOffset => "source timestamp has no offset or Z",
        SampleProblem.UnknownQuality => "no known quality",
        SampleProblem.BadValue => "no readable value",
        SampleProblem.GoodWithoutValue => "Good quality with no value",
        SampleProblem.WrongKind => "value of the wrong kind for its tag",
        _ => problem.ToString(),
    };
}

/// <summary>What one message yielded.</summary>
/// <param name="Accepted">Samples to hand over, each for a tag the device has.</param>
/// <param name="Rejected">Why each malformed sample was not accepted.</param>
/// <param name="UnknownTags">How many samples named a tag the device does not have.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record SamplePayloadResult(
    IReadOnlyList<TagReading> Accepted,
    IReadOnlyList<string> Rejected,
    int UnknownTags,
    string? Refusal)
{
    public static SamplePayloadResult Refused(string reason) => new([], [], 0, reason);
}
