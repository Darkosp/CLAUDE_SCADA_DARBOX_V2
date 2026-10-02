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
/// </code>
/// </para>
/// <para>
/// Published by the edge, because this is the one fact the cloud cannot work out for itself. The
/// cloud's own driver list is a different list: the Gateway runs a driver no edge reads with, and
/// a build that gave an edge a driver the cloud lacks would be the same mistake mirrored.
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
/// This is a compatibility surface we own across versions (ADR-0017). A message of another
/// version, or one carrying something other than a list of keys, is refused whole rather than
/// guessed at: half a declared driver list would be a list the edge never stated, and the cloud
/// would refuse devices over a difference that is not there.
/// </para>
/// </remarks>
public static class EdgeDriversPayload
{
    /// <summary>The format version this build writes and reads. Version 1 is the first.</summary>
    public const int Version = 1;

    /// <summary>Writes one edge's driver keys as one message.</summary>
    public static string Write(IEnumerable<string> driverKeys)
    {
        var ordered = Ordered(driverKeys);

        return new JsonObject
        {
            ["version"] = Version,
            ["drivers"] = new JsonArray(ordered.Select(key => (JsonNode)key).ToArray()),
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

        if (version != Version)
        {
            return EdgeDriversPayloadResult.Refused($"version {version} is not understood (this build reads version {Version})");
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

        if (problems.Count > 0)
        {
            return new EdgeDriversPayloadResult([], problems, $"the declaration was refused: {problems[0]}");
        }

        return new EdgeDriversPayloadResult(Ordered(drivers), [], Refusal: null);
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

/// <summary>What one declaration message yielded.</summary>
/// <param name="Drivers">The keys the edge declared; empty whenever the message was refused.</param>
/// <param name="Problems">Every reason the message could not be read, for the log.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record EdgeDriversPayloadResult(
    IReadOnlyList<string> Drivers,
    IReadOnlyList<string> Problems,
    string? Refusal)
{
    public static EdgeDriversPayloadResult Refused(string reason) => new([], [reason], reason);
}
