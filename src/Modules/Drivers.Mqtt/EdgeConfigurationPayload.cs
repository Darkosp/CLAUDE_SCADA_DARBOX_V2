using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// The message format that carries an edge's configuration from the cloud (ADR-0019 §4): the
/// devices the edge reads, each with the driver that reads it and each tag's plant address and
/// kind — the same shape the hand-written <c>edge.json</c> had, derived instead of typed.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "version": 1,
///   "revision": "sha256:…",
///   "generatedAtUtc": "2026-09-28T18:00:00.0000000+00:00",
///   "devices": [
///     {
///       "name": "Pump skid",
///       "driver": "opc-ua",
///       "scanIntervalMs": 1000,
///       "settings": { "endpointUrl": "opc.tcp://192.0.2.10:4840/Server" },
///       "tags": [ { "tagId": "…", "address": "ns=2;s=Pump1.Pressure", "kind": "Numeric" } ]
///     }
///   ]
/// }
/// </code>
/// <para>
/// A tag is named by its own stable id (ADR-0001), taken from the cloud's catalogue — the id is
/// never typed into an edge by hand (ADR-0019 §7), which is the whole point of the message.
/// </para>
/// <para>
/// <c>revision</c> is the version of the *content*: a hash of the devices alone, so an edge that
/// already holds this configuration does nothing when it is published again after an unrelated
/// change, rather than restarting acquisition for a configuration identical to the one it runs
/// (ADR-0019 §5). <c>generatedAtUtc</c> is when the cloud derived it — for a person reading a
/// log, deliberately not part of the hash.
/// </para>
/// <para>
/// Unlike a sample, a configuration carries no message expiry: an old configuration is not a
/// stale one, it is the current one, and it has to outlive a broker that is only ever restarted.
/// </para>
/// <para>
/// This is a compatibility surface we own across versions, exactly as the sample payload is
/// (ADR-0017). A message of another version, or one whose revision does not match the devices it
/// carries, is refused whole rather than guessed at — half an applied configuration would leave
/// an edge reading devices nobody asked it to read.
/// </para>
/// </remarks>
public static class EdgeConfigurationPayload
{
    /// <summary>The format version this build writes and reads. Version 1 is the first.</summary>
    public const int Version = 1;

    /// <summary>
    /// The version of this configuration's content: a hash of the devices, and of nothing else.
    /// Two configurations with the same devices have the same revision, whenever they were built.
    /// </summary>
    public static string RevisionOf(IEnumerable<EdgeConfigurationDevice> devices) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(devices)))).ToLowerInvariant();

    /// <summary>Writes a configuration, derived at <paramref name="generatedAtUtc"/>, as one message.</summary>
    public static string Write(IEnumerable<EdgeConfigurationDevice> devices, DateTimeOffset generatedAtUtc)
    {
        var ordered = Ordered(devices);

        return new JsonObject
        {
            ["version"] = Version,
            ["revision"] = RevisionOf(ordered),
            ["generatedAtUtc"] = Time(generatedAtUtc),
            ["devices"] = new JsonArray(ordered.Select(Device).ToArray()),
        }.ToJsonString();
    }

    /// <summary>Reads one configuration message, or why it cannot be read.</summary>
    public static EdgeConfigurationPayloadResult Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            return EdgeConfigurationPayloadResult.Refused($"not JSON: {exception.Message}");
        }

        if (root is not JsonObject message)
        {
            return EdgeConfigurationPayloadResult.Refused("not a JSON object");
        }

        if (message["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version))
        {
            return EdgeConfigurationPayloadResult.Refused("no version");
        }

        if (version != Version)
        {
            return EdgeConfigurationPayloadResult.Refused($"version {version} is not understood (this build reads version {Version})");
        }

        if (message["revision"]?.GetValueKind() != JsonValueKind.String)
        {
            return EdgeConfigurationPayloadResult.Refused("no revision");
        }

        var revision = message["revision"]!.GetValue<string>();

        if (ReadTime(message["generatedAtUtc"], out var generatedAt) is { } clockProblem)
        {
            return EdgeConfigurationPayloadResult.Refused($"generatedAtUtc: {Describe(clockProblem)}");
        }

        if (message["devices"] is not JsonArray array)
        {
            return EdgeConfigurationPayloadResult.Refused("no devices array");
        }

        var devices = new List<EdgeConfigurationDevice>(array.Count);
        var problems = new List<string>();

        for (var index = 0; index < array.Count; index++)
        {
            if (ReadDevice(array[index], index, out var device) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                devices.Add(device!);
            }
        }

        // Two devices of one name, or two addresses for one tag, cannot come from the cloud's own
        // catalogue (ADR-0015, ADR-0001) — a message that carries them is not one we wrote.
        foreach (var repeated in devices.GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            problems.Add($"device '{repeated.Key}' appears more than once");
        }

        foreach (var device in devices)
        {
            foreach (var tag in device.Tags.GroupBy(tag => tag.TagId).Where(group => group.Count() > 1))
            {
                problems.Add($"device '{device.Name}': tag {tag.Key} appears more than once");
            }
        }

        // The revision is what the edge compares to decide whether anything changed, so a message
        // whose revision does not describe its own devices would either be applied needlessly or
        // ignored when it mattered. Refused, rather than trusted.
        if (problems.Count == 0 && RevisionOf(devices) != revision)
        {
            problems.Add($"the revision says '{revision}', but the devices it carries hash to '{RevisionOf(devices)}'");
        }

        if (problems.Count > 0)
        {
            return new EdgeConfigurationPayloadResult([], problems, $"the configuration was refused: {problems[0]}");
        }

        return new EdgeConfigurationPayloadResult(devices, [], Refusal: null)
        {
            GeneratedAtUtc = generatedAt.ToUniversalTime(),
            Revision = revision,
        };
    }

    /// <summary>The devices in the one order a hash may be taken in: by name, then each device's tags by address.</summary>
    private static IReadOnlyList<EdgeConfigurationDevice> Ordered(IEnumerable<EdgeConfigurationDevice> devices) =>
        devices
            .OrderBy(device => device.Name, StringComparer.Ordinal)
            .Select(device => device with
            {
                Tags = device.Tags.OrderBy(tag => tag.Address, StringComparer.Ordinal).ThenBy(tag => tag.TagId).ToList(),
            })
            .ToList();

    /// <summary>The canonical form the revision is taken over: the devices, with their settings keys in one order.</summary>
    private static string Canonical(IEnumerable<EdgeConfigurationDevice> devices) =>
        new JsonArray(Ordered(devices).Select(Device).ToArray()).ToJsonString();

    private static JsonObject Device(EdgeConfigurationDevice device)
    {
        var settings = new JsonObject();
        foreach (var setting in device.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            settings[setting.Key] = setting.Value;
        }

        return new JsonObject
        {
            ["name"] = device.Name,
            ["driver"] = device.Driver,
            ["scanIntervalMs"] = device.ScanIntervalMs,
            ["settings"] = settings,
            ["tags"] = new JsonArray(device.Tags.Select(tag => (JsonNode)new JsonObject
            {
                ["tagId"] = tag.TagId.ToString(),
                ["address"] = tag.Address,
                ["kind"] = tag.Kind.ToString(),
            }).ToArray()),
        };
    }

    /// <summary>One device, or why it cannot be read.</summary>
    private static string? ReadDevice(JsonNode? node, int index, out EdgeConfigurationDevice? device)
    {
        device = null;

        if (node is not JsonObject body)
        {
            return $"device {index}: not an object";
        }

        if (body["name"]?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(body["name"]!.GetValue<string>()))
        {
            return $"device {index}: no name";
        }

        var name = body["name"]!.GetValue<string>();

        if (body["driver"]?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(body["driver"]!.GetValue<string>()))
        {
            return $"device '{name}': no driver";
        }

        if (body["scanIntervalMs"] is not JsonValue intervalNode
            || !intervalNode.TryGetValue<int>(out var scanIntervalMs)
            || scanIntervalMs < 1)
        {
            return $"device '{name}': ScanIntervalMs must be positive";
        }

        // Required, even empty: a device whose settings were dropped and one that needs none must
        // not look alike (the same reasoning as the sample payload's loss list).
        if (body["settings"] is not JsonObject settingsBody)
        {
            return $"device '{name}': no settings object";
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in settingsBody)
        {
            if (setting.Value?.GetValueKind() != JsonValueKind.String)
            {
                return $"device '{name}': setting '{setting.Key}' is not text";
            }

            settings[setting.Key] = setting.Value.GetValue<string>();
        }

        if (body["tags"] is not JsonArray tagsBody)
        {
            return $"device '{name}': no tags array";
        }

        if (tagsBody.Count == 0)
        {
            return $"device '{name}' has no tags";
        }

        var tags = new List<EdgeConfigurationTag>(tagsBody.Count);
        for (var tagIndex = 0; tagIndex < tagsBody.Count; tagIndex++)
        {
            if (ReadTag(tagsBody[tagIndex], name, tagIndex, out var tag) is { } problem)
            {
                return problem;
            }

            tags.Add(tag!);
        }

        device = new EdgeConfigurationDevice(name, body["driver"]!.GetValue<string>(), scanIntervalMs, settings, tags);
        return null;
    }

    /// <summary>One tag, or why it cannot be read.</summary>
    private static string? ReadTag(JsonNode? node, string device, int index, out EdgeConfigurationTag? tag)
    {
        tag = null;

        if (node is not JsonObject body)
        {
            return $"device '{device}', tag {index}: not an object";
        }

        if (!Guid.TryParse(body["tagId"]?.GetValueKind() == JsonValueKind.String ? body["tagId"]!.GetValue<string>() : null, out var tagId)
            || tagId == Guid.Empty)
        {
            return $"device '{device}', tag {index}: every tag needs the Gateway's tag id";
        }

        if (body["address"]?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(body["address"]!.GetValue<string>()))
        {
            return $"device '{device}', tag {tagId}: no address";
        }

        if (body["kind"]?.GetValueKind() != JsonValueKind.String
            || !Enum.TryParse<TagValueKind>(body["kind"]!.GetValue<string>(), ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind))
        {
            return $"device '{device}', tag {tagId}: no known kind";
        }

        tag = new EdgeConfigurationTag(tagId, body["address"]!.GetValue<string>(), kind);
        return null;
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A time with an explicit offset or Z, or why not.</summary>
    private static ClockProblem? ReadTime(JsonNode? node, out DateTimeOffset time)
    {
        time = default;

        if (node?.GetValueKind() != JsonValueKind.String)
        {
            return ClockProblem.NoTimestamp;
        }

        // An explicit offset or Z only. A time without one would be read in whatever zone this
        // server is in — a time made up by the reader, not given by the sender.
        var text = node.GetValue<string>();
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            return ClockProblem.NoTimestamp;
        }

        return HasOffset(text) ? null : ClockProblem.TimestampWithoutOffset;
    }

    private static bool HasOffset(string text)
    {
        var separator = text.IndexOf('T', StringComparison.Ordinal);
        var clock = separator >= 0 ? text[(separator + 1)..] : text;
        return clock.EndsWith('Z') || clock.EndsWith('z') || clock.Contains('+') || clock.Contains('-');
    }

    private static string Describe(ClockProblem problem) => problem switch
    {
        ClockProblem.NoTimestamp => "no readable timestamp",
        ClockProblem.TimestampWithoutOffset => "timestamp has no offset or Z",
        _ => problem.ToString(),
    };

    private enum ClockProblem
    {
        NoTimestamp,
        TimestampWithoutOffset,
    }
}

/// <summary>One device an edge reads, as it travels to the edge.</summary>
/// <param name="Name">The device's name in the cloud's browse tree.</param>
/// <param name="Driver">The driver key that reads it — the same key and module as the Gateway's (ADR-0002).</param>
/// <param name="ScanIntervalMs">How often the edge reads it.</param>
/// <param name="Settings">The driver's own settings, exactly as the cloud holds them.</param>
/// <param name="Tags">The tags to read, each naming the cloud's own tag id.</param>
public sealed record EdgeConfigurationDevice(
    string Name,
    string Driver,
    int ScanIntervalMs,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<EdgeConfigurationTag> Tags);

/// <summary>One tag to read, and where its value lives on the plant.</summary>
/// <param name="TagId">The cloud Gateway's stable id for this tag (ADR-0001).</param>
/// <param name="Address">The address the driver reads.</param>
/// <param name="Kind">The value kind the cloud expects, so a wrong-kind reading is caught at the edge.</param>
public sealed record EdgeConfigurationTag(Guid TagId, string Address, TagValueKind Kind);

/// <summary>What one configuration message yielded.</summary>
/// <param name="Devices">The devices to read; empty whenever the message was refused.</param>
/// <param name="Problems">Every reason the message could not be read, for the log.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record EdgeConfigurationPayloadResult(
    IReadOnlyList<EdgeConfigurationDevice> Devices,
    IReadOnlyList<string> Problems,
    string? Refusal)
{
    /// <summary>When the cloud derived this configuration; null only on a refused message.</summary>
    public DateTimeOffset? GeneratedAtUtc { get; init; }

    /// <summary>The content version the message declared; null only on a refused message.</summary>
    public string? Revision { get; init; }

    public static EdgeConfigurationPayloadResult Refused(string reason) => new([], [reason], reason);
}
