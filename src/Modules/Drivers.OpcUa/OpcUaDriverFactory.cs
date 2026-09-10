using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.OpcUa;

/// <summary>
/// Creates <see cref="OpcUaDriver"/> instances for devices configured with the
/// <c>opc-ua</c> driver key. Registered by hand in the composition root (ADR-0002).
/// </summary>
public sealed class OpcUaDriverFactory : IDeviceDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public OpcUaDriverFactory(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public string DriverKey => "opc-ua";

    public IDeviceDriver Create(Device device)
    {
        if (!device.ConnectionSettings.TryGetValue("endpointUrl", out var endpointUrl)
            || string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new InvalidOperationException(
                $"Device '{device.Name}' is configured for {DriverKey} but has no 'endpointUrl' connection setting.");
        }

        // Defaults to false: trusting whatever answers on an address is a choice an
        // installation makes deliberately, not one it inherits.
        var acceptUntrusted =
            device.ConnectionSettings.TryGetValue("acceptUntrustedCertificates", out var raw)
            && bool.TryParse(raw, out var parsed)
            && parsed;

        return new OpcUaDriver(endpointUrl, acceptUntrusted, _timeProvider);
    }
}
