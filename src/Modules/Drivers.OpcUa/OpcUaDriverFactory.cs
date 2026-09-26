using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILoggerFactory _loggerFactory;

    /// <remarks>
    /// The logger factory is optional, as it is for the MQTT module: a composition that keeps
    /// no log passes nothing rather than being given one it did not ask for.
    /// </remarks>
    public OpcUaDriverFactory(TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
    {
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

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

        return new OpcUaDriver(
            endpointUrl,
            acceptUntrusted,
            _timeProvider,
            _loggerFactory.CreateLogger<OpcUaDriver>());
    }
}
