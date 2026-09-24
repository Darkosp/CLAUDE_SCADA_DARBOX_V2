using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// Creates MQTT pushing drivers from a device's connection settings:
/// <c>host</c> (required), <c>port</c> (default 1883), <c>topic</c> (required — a topic filter,
/// wildcards allowed), <c>clientId</c> (default derived from the device id) and
/// <c>stalenessSeconds</c> (default 60: how long silence may last before it reads as loss).
/// </summary>
/// <remarks>
/// Plain TCP for now. TLS with a certificate per edge (ADR-0017) arrives with the broker's own
/// configuration in the cloud topology's Compose step.
/// </remarks>
public sealed class MqttPushingDriverFactory : IPushingDeviceDriverFactory
{
    public const int DefaultPort = 1883;

    public static readonly TimeSpan DefaultStalenessLimit = TimeSpan.FromSeconds(60);

    private readonly ILoggerFactory _loggerFactory;

    public MqttPushingDriverFactory(ILoggerFactory? loggerFactory = null) =>
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public string DriverKey => "mqtt";

    public IPushingDeviceDriver Create(Device device) =>
        new MqttPushingDriver(MqttConnection.From(device), _loggerFactory.CreateLogger<MqttPushingDriver>());
}

/// <summary>An MQTT device's connection settings, read and checked once.</summary>
internal sealed record MqttConnection(string Host, int Port, string Topic, string ClientId, TimeSpan StalenessLimit)
{
    public static MqttConnection From(Device device)
    {
        var settings = device.ConnectionSettings;

        var host = settings.TryGetValue("host", out var h) && !string.IsNullOrWhiteSpace(h)
            ? h
            : throw new InvalidOperationException($"Device '{device.Name}' is configured for mqtt but has no 'host' connection setting.");

        var topic = settings.TryGetValue("topic", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : throw new InvalidOperationException($"Device '{device.Name}' is configured for mqtt but has no 'topic' connection setting.");

        var port = settings.TryGetValue("port", out var p) && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : MqttPushingDriverFactory.DefaultPort;

        var clientId = settings.TryGetValue("clientId", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : $"scada-darbox-{device.Id:N}";

        var staleness = MqttPushingDriverFactory.DefaultStalenessLimit;
        if (settings.TryGetValue("stalenessSeconds", out var s))
        {
            staleness = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : throw new InvalidOperationException(
                    $"Device '{device.Name}': 'stalenessSeconds' must be a positive number of seconds.");
        }

        return new MqttConnection(host, port, topic, clientId, staleness);
    }
}
