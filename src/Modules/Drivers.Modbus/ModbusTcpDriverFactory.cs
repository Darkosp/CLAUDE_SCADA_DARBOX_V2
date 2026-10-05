using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILoggerFactory _loggerFactory;

    /// <remarks>
    /// The logger factory is optional, as it is for the MQTT module: a composition that keeps
    /// no log passes nothing rather than being given one it did not ask for.
    /// </remarks>
    public ModbusTcpDriverFactory(TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
    {
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>The default Modbus TCP port, used when a device does not specify one.</summary>
    public const int DefaultPort = 502;

    /// <summary>
    /// How long a request waits for the device before it is given up on, when a device does not say
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>open-work.md</c> carried this as an open decision: *the Modbus 5 s response bound — driver
    /// constant or per-device setting?* It is a **per-device setting**, with this default.
    /// </para>
    /// <para>
    /// The reason it is per-device rather than one number for a deployment is the same reason
    /// <c>host</c> and <c>scanIntervalMs</c> are: <b>how long a device takes to answer is a property
    /// of the device and the link to it, not of the Gateway.</b> A plant with one device on a slow
    /// radio link and twenty on a local switch has one device that needs a longer bound and nineteen
    /// that would be made slower to report a fault if they all shared it. A deployment-wide setting
    /// would force that choice on every device at once.
    /// </para>
    /// <para>
    /// <b>Five seconds is the default and not a recommendation.</b> It is what the driver had when
    /// this was a constant, it is generous for a local network — where a Modbus answer arrives in
    /// milliseconds or never — and it is the number every recorded measurement in this repository
    /// refers to, so leaving it as the default keeps those measurements comparable.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The connection setting a device uses to override <see cref="DefaultResponseTimeout"/>.</summary>
    public const string ResponseTimeoutSetting = "responseTimeoutSeconds";

    /// <summary>
    /// The widest a per-device bound may be set to, and the narrowest.
    /// </summary>
    /// <remarks>
    /// Bounded rather than merely positive, because both ends are a way to build a driver that does
    /// not work. Under a second, a healthy device on a busy network reads Bad for reasons that are
    /// the Gateway's fault. Over two minutes and a scan loop effectively stops reporting faults at
    /// all, which is the silence ADR-0003 refuses — the very failure the bound exists to prevent.
    /// </remarks>
    public static readonly TimeSpan MinResponseTimeout = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxResponseTimeout = TimeSpan.FromMinutes(2);

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

        return new ModbusTcpDriver(
            host,
            port,
            unitId,
            _timeProvider,
            _loggerFactory.CreateLogger<ModbusTcpDriver>(),
            ResponseTimeoutFor(device));
    }

    /// <summary>
    /// How long this device's requests wait, from its own settings or the default.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The setting is present and unusable. <b>Refused rather than ignored</b>, and the difference
    /// matters: a device configured for a thirty-second bound that silently got five would read Bad
    /// on a link that is merely slow, and nothing anywhere would say why. The message names the
    /// setting, the value and the allowed range, because the person reading it is looking at a device
    /// that is not working.
    /// </exception>
    private static TimeSpan ResponseTimeoutFor(Device device)
    {
        if (!device.ConnectionSettings.TryGetValue(ResponseTimeoutSetting, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return DefaultResponseTimeout;
        }

        if (!double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds))
        {
            throw new InvalidOperationException(
                $"Device '{device.Name}' has '{ResponseTimeoutSetting}' set to '{raw}', which is not a number of seconds.");
        }

        var timeout = TimeSpan.FromSeconds(seconds);

        if (timeout < MinResponseTimeout || timeout > MaxResponseTimeout)
        {
            throw new InvalidOperationException(
                $"Device '{device.Name}' has '{ResponseTimeoutSetting}' set to {seconds} s, which is outside "
                + $"the allowed {MinResponseTimeout.TotalSeconds:0} s to {MaxResponseTimeout.TotalSeconds:0} s.");
        }

        return timeout;
    }
}
