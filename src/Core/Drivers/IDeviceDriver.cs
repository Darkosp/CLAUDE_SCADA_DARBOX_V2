using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Drivers;

/// <summary>
/// One tag as the driver sees it: an identity plus the driver-specific address that
/// locates it on the device. Core never interprets <see cref="SourceAddress"/>.
/// </summary>
public sealed record DriverTag(Guid TagId, string SourceAddress, TagValueKind ValueKind);

/// <summary>
/// A live connection to one device. Implemented by driver modules, never by core:
/// this contract is protocol-agnostic and core carries no knowledge of any specific
/// protocol (ADR-0002).
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>Establishes the connection. May be called again to reconnect.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the given tags once.
    /// </summary>
    /// <remarks>
    /// An unreachable device is a normal operating condition, not an exception: an
    /// implementation returns readings carrying <see cref="Quality.Bad"/> rather than
    /// throwing, and never substitutes a fabricated value with <see cref="Quality.Good"/>
    /// (ADR-0003). Each reading carries the timestamp at which the device captured the
    /// value, not the time the read completed.
    /// </remarks>
    Task<IReadOnlyList<TagReading>> ReadAsync(
        IReadOnlyList<DriverTag> tags,
        CancellationToken cancellationToken);

    /// <summary>Writes a value back to the device, for a tag marked writable.</summary>
    Task WriteAsync(DriverTag tag, TagValue value, CancellationToken cancellationToken);
}

/// <summary>
/// Creates drivers for the devices of one protocol. Driver modules register an
/// implementation against core's contracts at compile time; nothing is discovered
/// by reflection or loaded dynamically (ADR-0002).
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>
    /// The value of <see cref="Device.DriverKey"/> this factory serves, e.g. <c>modbus-tcp</c>.
    /// Opaque to core, which only matches it for equality.
    /// </summary>
    string DriverKey { get; }

    /// <summary>Creates a driver for one device from that device's connection settings.</summary>
    IDeviceDriver Create(Device device);
}
