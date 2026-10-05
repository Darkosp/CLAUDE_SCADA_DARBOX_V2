using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.EdgeAgent.Uplink;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Modules.Drivers.OpcUa;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0019 §4 and §5 over a real broker: the cloud publishes a configuration on the edge's own
/// topic and the edge applies it — including one published while the edge was away, which is what
/// publishing it retained is for. The message the cloud writes is the one the Gateway's own builder
/// produces, read by the Gateway's own reader.
/// </summary>
public sealed class ConfigurationLinkTests : IAsyncLifetime
{
    private static readonly Guid Pressure = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaa01");
    private static readonly DateTimeOffset Derived = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");
    private TestBroker? _broker;
    private IMqttClient? _cloud;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_cloud is not null)
        {
            await _cloud.DisconnectAsync();
            _cloud.Dispose();
        }

        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_configuration_published_while_the_edge_was_away_is_applied_when_it_connects()
    {
        await StartBrokerAsync();

        // The cloud derives it and publishes it while no edge is connected; retained, it waits there.
        await PublishAsync(EdgeConfigurationPayload.Write(Devices(Pressure), Derived));

        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        var configuration = new EdgeConfigurationSource();
        var consumer = new EdgeConfigurationConsumer(configuration, buffer, NullLogger<EdgeConfigurationConsumer>.Instance);
        using var uplink = new UplinkService(Options.Create(UplinkOptions()), buffer, consumer, new EdgeUnreadableDevices(), new EdgeWriteExecutor(configuration, Drivers, NullLogger<EdgeWriteExecutor>.Instance), Drivers, NullLogger<UplinkService>.Instance);

        Assert.Null(configuration.Revision);

        await uplink.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => configuration.Revision is not null, "the edge to accept the retained configuration");
        await uplink.StopAsync(CancellationToken.None);

        var revision = EdgeConfigurationPayload.RevisionOf(Devices(Pressure));
        Assert.Equal(revision, configuration.Revision);

        // The cloud's own tag ids, which is the point of the message (ADR-0019 §7).
        Assert.Equal([Pressure], configuration.Devices.SelectMany(device => device.Tags).Select(tag => tag.TagId));

        // And it is kept, so the next start reads it with the broker still away (ADR-0019 §5).
        Assert.Equal(revision, buffer.AcceptedConfiguration()!.Revision);
    }

    [Fact]
    public async Task A_configuration_the_edge_cannot_read_is_refused_whole_and_changes_nothing()
    {
        await StartBrokerAsync();

        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        var configuration = new EdgeConfigurationSource();
        var consumer = new EdgeConfigurationConsumer(configuration, buffer, NullLogger<EdgeConfigurationConsumer>.Instance);
        using var uplink = new UplinkService(Options.Create(UplinkOptions()), buffer, consumer, new EdgeUnreadableDevices(), new EdgeWriteExecutor(configuration, Drivers, NullLogger<EdgeWriteExecutor>.Instance), Drivers, NullLogger<UplinkService>.Instance);

        await uplink.StartAsync(CancellationToken.None);
        await PublishAsync(EdgeConfigurationPayload.Write(Devices(Pressure), Derived));
        await WaitUntilAsync(() => configuration.Revision is not null, "the edge to accept the configuration");

        var accepted = configuration.Revision;

        // A device with no tags cannot come from the cloud's own catalogue, so the message is not one
        // the cloud wrote. Nothing about the edge changes, and the configuration in force stands.
        await PublishAsync(
            """
            {"version":1,"revision":"sha256:0","generatedAtUtc":"2026-09-28T20:00:00.0000000+00:00",
             "devices":[{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[]}]}
            """);

        await Task.Delay(TimeSpan.FromSeconds(2));
        await uplink.StopAsync(CancellationToken.None);

        Assert.Equal(accepted, configuration.Revision);
        Assert.Equal([Pressure], configuration.Devices.SelectMany(device => device.Tags).Select(tag => tag.TagId));
        Assert.Equal(accepted, buffer.AcceptedConfiguration()!.Revision);
    }

    private EdgeOptions UplinkOptions() => new()
    {
        Id = "plant-7",
        Broker = new BrokerOptions { Host = "127.0.0.1", Port = _broker!.Port },
        Buffer = new BufferOptions { Path = _path },
    };

    private static List<EdgeConfigurationDevice> Devices(params Guid[] tagIds) =>
    [
        new(
            "Pump skid",
            "opc-ua",
            1000,
            new Dictionary<string, string> { ["endpointUrl"] = "opc.tcp://192.0.2.10:4840/Server" },
            tagIds.Select(id => new EdgeConfigurationTag(id, $"ns=2;s=Pump1.{id:N}", TagValueKind.Numeric)).ToList()),
    ];

    private async Task StartBrokerAsync()
    {
        _broker = await TestBroker.StartAsync();

        _cloud = new MqttClientFactory().CreateMqttClient();
        await _cloud.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _broker.Port).Build());
    }

    /// <summary>The cloud's own publish: the edge's topic, retained, at least once (ADR-0019 §4).</summary>
    private Task PublishAsync(string payload) =>
        _cloud!.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic("scada/edge/plant-7/config")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build(),
            CancellationToken.None);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The drivers this build has, as the composition root registers them (ADR-0002). The edge
    /// declares these to the cloud the moment it connects (ADR-0019 §8), so a test that starts the
    /// real service says what a plant's edge says.
    /// </summary>
    private static IDeviceDriverFactory[] Drivers =>
    [
        new ModbusTcpDriverFactory(TimeProvider.System),
        new OpcUaDriverFactory(TimeProvider.System),
    ];

}
