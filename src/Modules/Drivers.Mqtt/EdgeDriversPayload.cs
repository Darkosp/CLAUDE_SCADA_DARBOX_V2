using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// The message format that carries an edge's own driver keys to the cloud (ADR-0019 §8): which
/// drivers the build at the plant can read a device with.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// { "version": 1, "drivers": [ "modbus-tcp", "opc-ua" ] }
///
/// { "version": 2,
///   "drivers": [ "opc-ua" ],
///   "unreadable": [ { "device": "Pump Station PLC", "driver": "modbus-tcp" } ] }
/// </code>
/// </para>
/// <para>
/// Published by the edge, because this is the one fact the cloud cannot work out for itself. The
/// cloud's own driver list is a different list: the Gateway runs a driver no edge reads with, and
/// a build that gave an edge a driver the cloud lacks would be the same mistake mirrored.
/// </para>
/// <para>
/// <b>Version 2 adds <c>unreadable</c> (ADR-0021)</b>: the devices this edge has been assigned and
/// cannot open, because its build has no driver for them. An edge that loses a driver after the
/// assignment is the case ADR-0019 §8 could not see — nothing re-examines an assignment when the
/// edge's build changes — so the edge says it on the declaration it already makes, and the cloud
/// records and reports it without touching the assignment.
/// </para>
/// <para>
/// <b>Version 1 is still read, and reads as "nothing reported".</b> A version 1 message says which
/// drivers the edge has and nothing about what it cannot read, which is not the same statement as
/// "it can read everything". The field is absent rather than empty, and <see
/// cref="EdgeDriversPayloadResult.UnreadableReported"/> keeps the two apart — the same distinction
/// §8 makes between an edge that has declared nothing and one that has declared it has nothing.
/// </para>
/// <para>
/// Deliberately no timestamp. A declaration is a fact about a build, not a measurement: it has no
/// source time to carry and it does not go stale, so the only time worth keeping is when the cloud
/// read it — and that one the cloud owns (ADR-0017: an edge's clock is neither trusted nor
/// overwritten). A field that existed and was ignored would be a lie told to whoever read the
/// message.
/// </para>
/// <para>
/// Deliberately no message expiry, for the reason the configuration payload gives: a declaration
/// is not stale after an hour, it is the current one, and it has to outlive a broker that is only
/// ever restarted. It is published retained, so an edge that declared itself while the cloud was
/// away is still understood when the cloud returns.
/// </para>
/// <para>
/// This is a compatibility surface we own across versions (ADR-0017). An unknown version, or a
/// message carrying something other than a list of keys, is refused whole rather than guessed at:
/// half a declared driver list would be a list the edge never stated, and the cloud would refuse
/// devices over a difference that is not there.
/// </para>
/// </remarks>
public static class EdgeDriversPayload
{
    /// <summary>The newest format version this build writes.</summary>
    public const int Version = 2;

    /// <summary>The oldest this build still reads: a declaration that reports no unreadable device.</summary>
    public const int OldestReadableVersion = 1;

    /// <summary>Writes one edge's driver keys as one message, reporting nothing unreadable.</summary>
    /// <remarks>Kept for the version 1 shape, which a caller with no configuration to check still writes.</remarks>
    public static string Write(IEnumerable<string> driverKeys) => Write(driverKeys, []);

    /// <summary>
    /// Writes one edge's driver keys, and the devices it has been assigned and cannot read, as one
    /// message (ADR-0021).
    /// </summary>
    public static string Write(
        IEnumerable<string> driverKeys,
        IEnumerable<EdgeUnreadableDevice> unreadable)
    {
        var ordered = Ordered(driverKeys);
        var missing = unreadable
            .OrderBy(device => device.Device, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Device, StringComparer.Ordinal)
            .ToList();

        return new JsonObject
        {
            ["version"] = Version,
            ["drivers"] = new JsonArray(ordered.Select(key => (JsonNode)key).ToArray()),
            ["unreadable"] = new JsonArray(missing
                .Select(device => (JsonNode)new JsonObject
                {
                    ["device"] = device.Device,
                    ["driver"] = device.Driver,
                })
                .ToArray()),
        }.ToJsonString();
    }

    /// <summary>Reads one declaration message, or why it cannot be read.</summary>
    public static EdgeDriversPayloadResult Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            return EdgeDriversPayloadResult.Refused($"not JSON: {exception.Message}");
        }

        if (root is not JsonObject message)
        {
            return EdgeDriversPayloadResult.Refused("not a JSON object");
        }

        if (message["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version))
        {
            return EdgeDriversPayloadResult.Refused("no version");
        }

        if (version < OldestReadableVersion || version > Version)
        {
            return EdgeDriversPayloadResult.Refused(
                $"version {version} is not understood (this build reads versions {OldestReadableVersion} to {Version})");
        }

        if (message["drivers"] is not JsonArray array)
        {
            return EdgeDriversPayloadResult.Refused("no drivers array");
        }

        var drivers = new List<string>(array.Count);
        var problems = new List<string>();

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index]?.GetValueKind() != JsonValueKind.String
                || string.IsNullOrWhiteSpace(array[index]!.GetValue<string>()))
            {
                problems.Add($"driver {index} is not a key");
                continue;
            }

            drivers.Add(array[index]!.GetValue<string>());
        }

        // A key names a factory, and the factories are looked up ignoring case (ADR-0002), so a
        // list carrying one key twice under two spellings would not be two drivers — it would be
        // the edge contradicting itself about the build it runs.
        foreach (var repeated in drivers
                     .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            problems.Add($"driver '{repeated.Key}' appears more than once");
        }

        // Absent in version 1, and absent means "not reported" rather than "none" (ADR-0021). The
        // older message is not an older claim about unreadable devices; it makes no claim at all.
        var reported = version >= 2;
        var unreadable = new List<EdgeUnreadableDevice>();

        if (reported)
        {
            if (message["unreadable"] is not JsonArray unreadableArray)
            {
                problems.Add("no unreadable array");
            }
            else
            {
                for (var index = 0; index < unreadableArray.Count; index++)
                {
                    if (ReadUnreadable(unreadableArray[index], index) is { } entry)
                    {
                        unreadable.Add(entry);
                    }
                    else
                    {
                        problems.Add($"unreadable {index} is not a device and a driver");
                    }
                }

                foreach (var repeated in unreadable
                             .GroupBy(device => device.Device, StringComparer.OrdinalIgnoreCase)
                             .Where(group => group.Count() > 1))
                {
                    problems.Add($"device '{repeated.Key}' is reported unreadable more than once");
                }
            }
        }

        if (problems.Count > 0)
        {
            return new EdgeDriversPayloadResult([], [], reported, problems, $"the declaration was refused: {problems[0]}");
        }

        return new EdgeDriversPayloadResult(
            Ordered(drivers),
            unreadable
                .OrderBy(device => device.Device, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.Device, StringComparer.Ordinal)
                .ToList(),
            reported,
            [],
            Refusal: null);
    }

    /// <summary>One entry of the unreadable array, or why it is not one (ADR-0021).</summary>
    private static EdgeUnreadableDevice? ReadUnreadable(JsonNode? node, int index)
    {
        if (node is not JsonObject entry)
        {
            return null;
        }

        if (entry["device"] is not JsonValue deviceNode
            || deviceNode.GetValueKind() != JsonValueKind.String
            || string.IsNullOrWhiteSpace(deviceNode.GetValue<string>()))
        {
            return null;
        }

        if (entry["driver"] is not JsonValue driverNode
            || driverNode.GetValueKind() != JsonValueKind.String
            || string.IsNullOrWhiteSpace(driverNode.GetValue<string>()))
        {
            return null;
        }

        return new EdgeUnreadableDevice(deviceNode.GetValue<string>(), driverNode.GetValue<string>());
    }

    /// <summary>
    /// The keys in one order: ignoring case, then by their exact spelling where two keys differ
    /// only in case (which the duplicate check above has already refused, so this only keeps the
    /// order total).
    /// </summary>
    private static IReadOnlyList<string> Ordered(IEnumerable<string> driverKeys) =>
        driverKeys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// A device an edge has been assigned and cannot open, because its build has no driver for it
/// (ADR-0021). Named, not identified: the operator reading the log, the audit row or the screen is
/// reading names, and the cloud resolves the name to the device it assigned.
/// </summary>
public sealed record EdgeUnreadableDevice(string Device, string Driver);

/// <summary>What one declaration message yielded.</summary>
/// <param name="Drivers">The keys the edge declared; empty whenever the message was refused.</param>
/// <param name="Unreadable">The devices the edge said it cannot read; empty on a version 1 message.</param>
/// <param name="UnreadableReported">
/// Whether the message said anything about unreadable devices at all. False on version 1, where
/// absence is not emptiness (ADR-0021), and false on a refused message.
/// </param>
/// <param name="Problems">Every reason the message could not be read, for the log.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record EdgeDriversPayloadResult(
    IReadOnlyList<string> Drivers,
    IReadOnlyList<EdgeUnreadableDevice> Unreadable,
    bool UnreadableReported,
    IReadOnlyList<string> Problems,
    string? Refusal)
{
    public static EdgeDriversPayloadResult Refused(string reason) => new([], [], false, [reason], reason);
}
