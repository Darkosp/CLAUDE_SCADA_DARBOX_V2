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
        //
        // **It has decided nothing until now.** With the channel unsecured there was no server
        // certificate to validate, so a deployment that set this to `true` believing it was doing
        // something was mistaken. From ADR-0033 on it is load-bearing.
        var acceptUntrusted =
            device.ConnectionSettings.TryGetValue("acceptUntrustedCertificates", out var raw)
            && bool.TryParse(raw, out var parsed)
            && parsed;

        return new OpcUaDriver(
            endpointUrl,
            acceptUntrusted,
            SecurityFor(device),
            _timeProvider,
            _loggerFactory.CreateLogger<OpcUaDriver>());
    }

    /// <summary>
    /// Every connection setting this driver reads. Nothing else on a device reaches it.
    /// </summary>
    /// <remarks>
    /// **Public so a test can assert what is in it** (ADR-0033 decision 4). `ConnectionSettings` is a
    /// dictionary on the device row: the API echoes it back, and a device edit writes it into
    /// `audit_log.detail`, which ADR-0032 §6 makes **opaque JSON that is never parsed** — so nothing in
    /// the reader could redact a field even in principle, and a password put here would be printed in
    /// full on the screen built for an Admin to read.
    /// <para>
    /// The rule is therefore that **a setting may name a credential and may not carry one**, and this
    /// list is where that rule is checkable. The cheap wrong answer — *it is just another setting* — is
    /// the one a later session will reach for, which is why it is pinned rather than trusted to a
    /// comment.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> ConnectionSettingNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "endpointUrl",
            "acceptUntrustedCertificates",
            "security",
        };

    /// <summary>What the device asks of the channel, defaulting to <see cref="OpcUaSecurity.Required"/>.</summary>
    /// <remarks>
    /// **An unrecognised value is refused rather than defaulted** — in either direction. Defaulting a
    /// typo to `Required` would stop a deployment that meant `none` and leave it hunting; defaulting it
    /// to `None` would silently unsecure a device because somebody wrote `requried`. A device that
    /// cannot be understood does not run.
    /// </remarks>
    private static OpcUaSecurity SecurityFor(Device device)
    {
        if (!device.ConnectionSettings.TryGetValue("security", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return OpcUaSecurity.Required;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "required" => OpcUaSecurity.Required,
            "none" => OpcUaSecurity.None,
            _ => throw new InvalidOperationException(
                $"Device '{device.Name}' has an unrecognised 'security' connection setting: '{value}'. "
                + "It must be 'required' (the default — the strongest endpoint the server offers) or "
                + "'none' (an unsecured session, chosen deliberately). See ADR-0033."),
        };
    }
}
