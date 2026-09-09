using System.Text.Json;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Serialisation of a device's driver-specific connection settings, which core treats
/// as an opaque bag of strings (ADR-0002).
/// </summary>
internal static class ConnectionSettingsJson
{
    internal static IReadOnlyDictionary<string, string> Deserialize(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    internal static string Serialize(IReadOnlyDictionary<string, string> settings) =>
        JsonSerializer.Serialize(settings);
}
