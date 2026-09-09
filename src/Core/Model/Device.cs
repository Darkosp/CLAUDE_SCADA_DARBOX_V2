namespace ScadaDarbox.Core.Model;

/// <summary>
/// A source of tags with its own connection configuration (ADR-0001). Driver and
/// connection settings live here — never on a folder, and never inferred from a
/// tag's position in the browse tree.
/// </summary>
public sealed class Device
{
    public required Guid Id { get; init; }

    /// <summary>Owning site, and through it the tenant (ADR-0001, ADR-0004).</summary>
    public required Guid SiteId { get; init; }

    /// <summary>Display name. Mutable and identity-neutral.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Which driver module serves this device, e.g. <c>modbus-tcp</c>. An opaque key to
    /// core: the tag engine never interprets it, and core carries no knowledge of any
    /// specific protocol (ADR-0002).
    /// </summary>
    public required string DriverKey { get; init; }

    /// <summary>
    /// Driver-specific connection settings, opaque to core. Each driver module defines
    /// and validates its own keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConnectionSettings { get; init; } =
        new Dictionary<string, string>();

    /// <summary>How often the driver polls this device's tags.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(1);
}
