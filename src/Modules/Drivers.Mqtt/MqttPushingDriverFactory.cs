using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// Creates MQTT pushing drivers from a device's connection settings:
/// <list type="bullet">
/// <item><c>host</c> (required), <c>port</c> (default 1883, or 8883 with TLS);</item>
/// <item><c>topic</c> (required — a topic filter, wildcards allowed);</item>
/// <item><c>clientId</c> (default derived from the device id — fixed, because the broker keys the
/// persistent session to it);</item>
/// <item><c>stalenessSeconds</c> (default 60: how long silence may last before it reads as loss);</item>
/// <item><c>sessionExpiryHours</c> (default 720: how long the broker keeps queueing for this
/// Gateway while it is away — past it, what was queued is gone);</item>
/// <item><c>tls</c> (<c>true</c> for mutual TLS, ADR-0017) with <c>caFile</c>, <c>certFile</c> and
/// <c>keyFile</c>: PEM paths as the Gateway sees them.</item>
/// </list>
/// </summary>
public sealed class MqttPushingDriverFactory : IPushingDeviceDriverFactory
{
    public const int DefaultPort = 1883;
    public const int DefaultTlsPort = 8883;

    public static readonly TimeSpan DefaultStalenessLimit = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultSessionExpiry = TimeSpan.FromDays(30);

    private readonly ILoggerFactory _loggerFactory;

    public MqttPushingDriverFactory(ILoggerFactory? loggerFactory = null) =>
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public string DriverKey => "mqtt";

    public IPushingDeviceDriver Create(Device device) =>
        new MqttPushingDriver(MqttConnection.From(device), _loggerFactory.CreateLogger<MqttPushingDriver>());
}

/// <summary>An MQTT device's connection settings, read and checked once.</summary>
internal sealed record MqttConnection(
    string Host,
    int Port,
    string Topic,
    string ClientId,
    TimeSpan StalenessLimit,
    uint SessionExpirySeconds,
    MqttTlsFiles? Tls)
{
    public static MqttConnection From(Device device)
    {
        var settings = device.ConnectionSettings;
        string? Setting(string key) => settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        var host = Setting("host")
            ?? throw new InvalidOperationException($"Device '{device.Name}' is configured for mqtt but has no 'host' connection setting.");

        var topic = Setting("topic")
            ?? throw new InvalidOperationException($"Device '{device.Name}' is configured for mqtt but has no 'topic' connection setting.");

        MqttTlsFiles? tls = null;
        if (Setting("tls") is { } tlsSetting)
        {
            if (!bool.TryParse(tlsSetting, out var useTls))
            {
                throw new InvalidOperationException($"Device '{device.Name}': 'tls' must be true or false.");
            }

            if (useTls)
            {
                tls = new MqttTlsFiles(Setting("caFile") ?? "", Setting("certFile") ?? "", Setting("keyFile") ?? "");
                if (tls.Problems() is { Count: > 0 } problems)
                {
                    throw new InvalidOperationException($"Device '{device.Name}': {string.Join("; ", problems)}.");
                }
            }
        }

        var port = Setting("port") is { } p && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : tls is null ? MqttPushingDriverFactory.DefaultPort : MqttPushingDriverFactory.DefaultTlsPort;

        var clientId = Setting("clientId") ?? $"scada-darbox-{device.Id:N}";

        var staleness = MqttPushingDriverFactory.DefaultStalenessLimit;
        if (Setting("stalenessSeconds") is { } s)
        {
            staleness = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : throw new InvalidOperationException(
                    $"Device '{device.Name}': 'stalenessSeconds' must be a positive number of seconds.");
        }

        var sessionExpiry = MqttPushingDriverFactory.DefaultSessionExpiry;
        if (Setting("sessionExpiryHours") is { } h)
        {
            sessionExpiry = double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0 && hours <= 24 * 365 * 10
                ? TimeSpan.FromHours(hours)
                : throw new InvalidOperationException(
                    $"Device '{device.Name}': 'sessionExpiryHours' must be a positive number of hours, at most ten years.");
        }

        return new MqttConnection(host, port, topic, clientId, staleness, (uint)sessionExpiry.TotalSeconds, tls);
    }
}
