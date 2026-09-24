using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The MQTT module as the Gateway composes it: a device configured through the API with driver
/// <c>mqtt</c>, fed by a real broker, reaches the screen with the time the source measured, and
/// its silence reads as loss (ADR-0016, ADR-0017).
/// </summary>
public sealed class MqttDeviceTests : IClassFixture<GatewayTestHost>, IAsyncLifetime
{
    private readonly GatewayTestHost _host;
    private readonly int _port = FreePort();
    private MqttServer _broker = null!;

    public MqttDeviceTests(GatewayTestHost host) => _host = host;

    public async Task InitializeAsync()
    {
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(_port)
            .Build());
        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _broker.StopAsync();
        _broker.Dispose();
    }

    [RequiresDatabaseFact]
    public async Task An_mqtt_device_shows_what_its_source_measured_and_reads_Bad_when_it_falls_silent()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var topic = $"scada/edge/{Guid.NewGuid():N}/samples";

        using var created = await client.PostAsJsonAsync(
            $"/api/sites/{DemoConfigurationSeeder.SiteId}/devices",
            new SaveDeviceRequest(
                $"MQTT {Guid.NewGuid():N}"[..13],
                "mqtt",
                new Dictionary<string, string>
                {
                    ["host"] = "127.0.0.1",
                    ["port"] = _port.ToString(CultureInfo.InvariantCulture),
                    ["topic"] = topic,
                    ["stalenessSeconds"] = "2",
                },
                ScanIntervalMs: null,
                FolderId: null));
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<Guid>();

        using var tagResponse = await client.PostAsJsonAsync(
            $"/api/devices/{device}/tags",
            new SaveTagRequest("Pressure", "Numeric", Unit: null, "pressure", IsWritable: false));
        tagResponse.EnsureSuccessStatusCode();
        var tagId = await tagResponse.Content.ReadFromJsonAsync<Guid>();

        // Measured a minute ago, sent now — as from an edge buffer. Published until the Gateway,
        // which subscribes when it notices the new device, has taken it.
        var measuredAt = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-1).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var message = SamplePayload.Write([new TagReading(tagId, new TagValue.Numeric(6.25), measuredAt, Quality.Good)]);

        JsonElement live = default;
        await GatewayTestHost.WaitUntilAsync(
            async () =>
            {
                await PublishAsync(topic, message);
                live = await CurrentAsync(client, tagId);
                return live.ValueKind == JsonValueKind.Object && live.GetProperty("quality").GetString() == "Good";
            },
            "the MQTT sample to reach the tag");

        Assert.Equal(6.25, live.GetProperty("value").GetProperty("numeric").GetDouble());
        Assert.Equal(measuredAt, live.GetProperty("sourceTimestampUtc").GetDateTimeOffset());

        // Then the source stops sending.
        JsonElement lost = default;
        await GatewayTestHost.WaitUntilAsync(
            async () => (lost = await CurrentAsync(client, tagId)).GetProperty("quality").GetString() == "Bad",
            "the silent MQTT tag to read Bad",
            TimeSpan.FromSeconds(15));

        Assert.Equal(measuredAt, lost.GetProperty("sourceTimestampUtc").GetDateTimeOffset());
        Assert.Equal("none", lost.GetProperty("value").GetProperty("kind").GetString());
    }

    private async Task PublishAsync(string topic, string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await publisher.DisconnectAsync();
    }

    private static async Task<JsonElement> CurrentAsync(HttpClient client, Guid tagId)
    {
        using var response = await client.GetAsync($"/api/tags/{tagId}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
