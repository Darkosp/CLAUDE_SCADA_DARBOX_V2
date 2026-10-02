using System.Text.Json;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Serialisation of the devices an edge has said it cannot read (ADR-0021), stored as a jsonb
/// column so the pair a device and its missing driver form survives a restart.
/// </summary>
/// <remarks>
/// Null and empty are kept apart in both directions, because they are different statements: null is
/// "no declaration has said anything about this" and empty is "the edge has said it can read
/// everything assigned to it". Flattening either into the other would turn an older edge's silence
/// into a claim it never made.
/// </remarks>
internal static class UnreadableDevicesJson
{
    internal static IReadOnlyList<EdgeUnreadableDevice>? Deserialize(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<List<EdgeUnreadableDevice>>(json) ?? [];

    internal static string? Serialize(IReadOnlyList<EdgeUnreadableDevice>? devices) =>
        devices is null ? null : JsonSerializer.Serialize(devices);
}
