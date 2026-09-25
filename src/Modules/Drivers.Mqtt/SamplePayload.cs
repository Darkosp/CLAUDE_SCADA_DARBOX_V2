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
///   "version": 2,
///   "sentAtUtc": "2026-09-24T12:00:05Z",
///   "samples": [
///     { "tagId": "…", "sourceTimestampUtc": "2026-09-24T12:00:00Z", "quality": "Good",
///       "value": { "kind": "numeric", "numeric": 4.2 } }
///   ],
///   "lost": [
///     { "lossId": "…", "count": 1200,
///       "fromSourceUtc": "2026-09-24T03:00:00Z", "toSourceUtc": "2026-09-24T03:20:00Z" }
///   ]
/// }
/// </code>
/// <para>
/// A tag is named by its stable id (ADR-0001), never its display path. The value kinds are the
/// four of <see cref="TagValue"/>, plus <c>"none"</c>, allowed only when the quality is not Good:
/// a Good sample without a value is a contradiction and is refused, never read as zero.
/// </para>
/// <para>
/// <c>sentAtUtc</c> is the sender's clock when it sent the message — not when anything was
/// measured — so the receiver can compare clocks without guessing from sample times, which may be
/// hours old in a backlog. <c>lost</c> lists samples the sender measured and then dropped
/// (ADR-0017); a report is repeated until the message carrying it is acknowledged, so each has an
/// id and the receiver records it once.
/// </para>
/// <para>
/// This is a compatibility surface we own across versions. A message of any other version is
/// refused whole rather than guessed at. Version 1 had neither field: a version 1 reader would
/// have read a version 2 message and silently ignored its losses, which is why these fields
/// needed a new version rather than an optional addition.
/// </para>
/// </remarks>
public static class SamplePayload
{
    public const int Version = 2;

    /// <summary>
    /// The MQTT 5 message expiry every sender sets: ten years, so in practice nothing expires.
    /// It is set for what the broker does with it: an MQTT 5 broker forwards a message with its
    /// expiry reduced by the time it held it. The difference is how long the message waited —
    /// measured by the broker's clock alone, so neither the sender's nor the receiver's is trusted
    /// for it.
    /// </summary>
    public const uint MessageExpirySeconds = 315_360_000;

    /// <summary>
    /// How long a message waited in the broker, from the expiry it arrived with; zero when it
    /// carries none, or one this format did not set.
    /// </summary>
    public static TimeSpan WaitedInBroker(uint? remainingExpirySeconds) =>
        remainingExpirySeconds is { } remaining and > 0 and <= MessageExpirySeconds
            ? TimeSpan.FromSeconds(MessageExpirySeconds - remaining)
            : TimeSpan.Zero;

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

        if (ReadTime(message["sentAtUtc"], out var sentAt) is { } clockProblem)
        {
            return SamplePayloadResult.Refused($"sentAtUtc: {Describe(clockProblem)}");
        }

        if (message["samples"] is not JsonArray samples)
        {
            return SamplePayloadResult.Refused("no samples array");
        }

        if (message["lost"] is not JsonArray lostArray)
        {
            // Required, even empty: an absent list and "nothing was lost" must not look alike.
            return SamplePayloadResult.Refused("no lost array");
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

        var lost = new List<SourceLoss>(lostArray.Count);
        for (var index = 0; index < lostArray.Count; index++)
        {
            if (ReadLoss(lostArray[index]) is { } loss)
            {
                lost.Add(loss);
            }
            else
            {
                rejected.Add($"lost {index}: not a loss report with an id, a count of at least 1 and an ordered window of source times");
            }
        }

        return new SamplePayloadResult(accepted, rejected, unknown, Refusal: null)
        {
            SentAtUtc = sentAt.ToUniversalTime(),
            Lost = lost,
        };
    }

    /// <summary>Writes samples, and any losses to report, as one message sent at <paramref name="sentAtUtc"/>.</summary>
    public static string Write(IEnumerable<TagReading> samples, IEnumerable<SourceLoss> lost, DateTimeOffset sentAtUtc)
    {
        var array = new JsonArray();
        foreach (var sample in samples)
        {
            array.Add(new JsonObject
            {
                ["tagId"] = sample.TagId.ToString(),
                ["sourceTimestampUtc"] = Time(sample.SourceTimestampUtc),
                ["quality"] = sample.Quality.ToString(),
                ["value"] = WriteValue(sample.Value),
            });
        }

        var losses = new JsonArray();
        foreach (var loss in lost)
        {
            losses.Add(new JsonObject
            {
                ["lossId"] = loss.LossId.ToString(),
                ["count"] = loss.Count,
                ["fromSourceUtc"] = Time(loss.FromSourceUtc),
                ["toSourceUtc"] = Time(loss.ToSourceUtc),
            });
        }

        return new JsonObject
        {
            ["version"] = Version,
            ["sentAtUtc"] = Time(sentAtUtc),
            ["samples"] = array,
            ["lost"] = losses,
        }.ToJsonString();
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static SourceLoss? ReadLoss(JsonNode? node)
    {
        if (node is not JsonObject loss
            || loss["lossId"]?.GetValueKind() != JsonValueKind.String
            || !Guid.TryParse(loss["lossId"]!.GetValue<string>(), out var lossId)
            || loss["count"] is not JsonValue countNode
            || !countNode.TryGetValue<long>(out var count)
            || count < 1
            || ReadTime(loss["fromSourceUtc"], out var from) is not null
            || ReadTime(loss["toSourceUtc"], out var to) is not null
            || from > to)
        {
            return null;
        }

        return new SourceLoss(lossId, count, from.ToUniversalTime(), to.ToUniversalTime());
    }

    /// <summary>A time with an explicit offset or Z, or why not.</summary>
    private static SampleProblem? ReadTime(JsonNode? node, out DateTimeOffset time)
    {
        time = default;

        if (node?.GetValueKind() != JsonValueKind.String)
        {
            return SampleProblem.NoTimestamp;
        }

        // An explicit offset or Z only. A time without one would be read in whatever zone this
        // server is in — a time made up by the reader, not given by the sender.
        var text = node.GetValue<string>();
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            return SampleProblem.NoTimestamp;
        }

        return HasOffset(text) ? null : SampleProblem.TimestampWithoutOffset;
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

        if (ReadTime(sample["sourceTimestampUtc"], out var measuredAt) is { } timeProblem)
        {
            return timeProblem;
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
        SampleProblem.NoTimestamp => "no readable timestamp",
        SampleProblem.TimestampWithoutOffset => "timestamp has no offset or Z",
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
    /// <summary>The sender's clock when it sent the message; null only on a refused message.</summary>
    public DateTimeOffset? SentAtUtc { get; init; }

    /// <summary>Losses the sender reported, each well formed.</summary>
    public IReadOnlyList<SourceLoss> Lost { get; init; } = [];

    public static SamplePayloadResult Refused(string reason) => new([], [], 0, reason);
}
