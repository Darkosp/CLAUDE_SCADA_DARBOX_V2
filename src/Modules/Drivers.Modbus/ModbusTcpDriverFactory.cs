using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Modbus;

/// <summary>
/// Creates <see cref="ModbusTcpDriver"/> instances for devices configured with the
/// <c>modbus-tcp</c> driver key. Registered against core's contract by the composition
/// root at compile time (ADR-0002).
/// </summary>
public sealed class ModbusTcpDriverFactory : IDeviceDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public ModbusTcpDriverFactory(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <summary>The default Modbus TCP port, used when a device does not specify one.</summary>
    public const int DefaultPort = 502;

    public string DriverKey => "modbus-tcp";

    public IDeviceDriver Create(Device device)
    {
        if (!device.ConnectionSettings.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                $"Device '{device.Name}' is configured for {DriverKey} but has no 'host' connection setting.");
        }

        var port = device.ConnectionSettings.TryGetValue("port", out var rawPort)
                   && int.TryParse(rawPort, out var parsedPort)
            ? parsedPort
            : DefaultPort;

        var unitId = device.ConnectionSettings.TryGetValue("unitId", out var rawUnitId)
                     && byte.TryParse(rawUnitId, out var parsedUnitId)
            ? parsedUnitId
            : (byte)1;

        return new ModbusTcpDriver(host, port, unitId, _timeProvider);
    }
}
