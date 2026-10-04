using System.Collections.Concurrent;
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
// The domain's own pair and the wire's pair are deliberately separate types (ADR-0002: Core cannot
// reference a module). This file is about the wire.
using EdgeUnreadableDevice = ScadaDarbox.Modules.Drivers.Mqtt.EdgeUnreadableDevice;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0017's link from the edge: the buffer drains to the broker when it can, a batch leaves the
/// buffer only once the broker has acknowledged it, and a lost broker is reconnected to.
/// </summary>
public sealed class UplinkTests : IAsyncLifetime
{
    private static readonly Guid Tag = new("77777777-7777-4777-8777-777777777701");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 5, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");
    private readonly int _port = FreePort();
    private readonly ConcurrentQueue<TagReading> _received = new();
    private readonly ConcurrentQueue<string> _declared = new();
    private MqttServer? _broker;
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
            await _broker.StopAsync();
            _broker.Dispose();
        }

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task While_the_broker_is_away_nothing_leaves_the_buffer_and_when_it_is_back_everything_arrives()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 10_000);
        var samples = Samples(20);
        buffer.Append(samples);

        using var uplink = Uplink(buffer);
        await uplink.StartAsync(CancellationToken.None);

        // No broker: the samples wait, all of them.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal((Pending: 20L, Acknowledged: 0L), (buffer.Account().Pending, buffer.Account().Acknowledged));

        // The broker, and the cloud listening on it, come up.
        await StartBrokerAsync(refuse: false);
        await WaitUntilAsync(() => buffer.Account().Pending == 0, "the buffer to drain");
        await WaitUntilAsync(() => _received.Count == 20, "the cloud to receive every sample");

        Assert.Equal(samples, _received.OrderBy(sample => sample.SourceTimestampUtc));
        var account = buffer.Account();
        Assert.Equal(20L, account.Acknowledged);
        Assert.Empty(account.Problems());

        await uplink.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_batch_the_broker_refuses_stays_in_the_buffer()
    {
        // A broker that answers every publish with a refusal — as it would one outside this edge's
        // topic prefix (ADR-0017). Over MQTT 3.1.1 that refusal would still arrive as an
        // acknowledgement, and the samples would be deleted.
        await StartBrokerAsync(refuse: true);

        // Full, too: five dropped, so the refused message also carried a loss report.
        using var buffer = SampleBuffer.Open(_path, maxPending: 15);
        buffer.Append(Samples(20));

        using var uplink = Uplink(buffer);
        await uplink.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4));
        await uplink.StopAsync(CancellationToken.None);

        var account = buffer.Account();
        Assert.Equal((Pending: 15L, Acknowledged: 0L), (account.Pending, account.Acknowledged));
        Assert.Empty(account.Problems());

        // Nor is the loss taken as reported: the cloud has not heard of it.
        Assert.Single(buffer.UnsentLosses());
    }

    private UplinkService Uplink(SampleBuffer buffer, EdgeUnreadableDevices? unreadable = null) => new(
        Options.Create(new EdgeOptions
        {
            Id = "plant-7",
            Broker = new BrokerOptions { Host = "127.0.0.1", Port = _port },
            Buffer = new BufferOptions { Path = _path },
        }),
        buffer,
        // These tests are about what leaves the edge; what it reads is ConfigurationLinkTests' subject.
        new EdgeConfigurationConsumer(new EdgeConfigurationSource(), buffer, NullLogger<EdgeConfigurationConsumer>.Instance),
        // And nothing is unreadable until a configuration says so (ADR-0021).
        unreadable ?? new EdgeUnreadableDevices(),
        // Nothing is written unless the cloud asks (ADR-0023), and these tests never do.
        new EdgeWriteExecutor(new EdgeConfigurationSource(), Drivers, NullLogger<EdgeWriteExecutor>.Instance),
        Drivers,
        NullLogger<UplinkService>.Instance);

    /// <summary>The drivers this build has, as the composition root registers them (ADR-0002).</summary>
    private static IDeviceDriverFactory[] Drivers =>
    [
        new ModbusTcpDriverFactory(TimeProvider.System),
        new OpcUaDriverFactory(TimeProvider.System),
    ];

    [Fact]
    public async Task The_edge_declares_the_drivers_this_build_has_and_a_late_cloud_still_hears_it()
    {
        // ADR-0019 §8: the cloud cannot work out which drivers an edge has — its own list is a
        // different list — so the edge says so, on its own topic, and the declaration is retained
        // because the cloud may be the one that arrives second.
        await StartBrokerAsync(refuse: false);

        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        using var uplink = Uplink(buffer);
        await uplink.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => _declared.Count > 0, "the edge to declare its drivers");

        var declared = EdgeDriversPayload.Read(_declared.First());
        Assert.Null(declared.Refusal);
        Assert.Equal(["modbus-tcp", "opc-ua"], declared.Drivers);

        await uplink.StopAsync(CancellationToken.None);

        // The cloud that was not there when the edge spoke: it subscribes now, and the broker hands
        // it the same declaration. Not retained would mean an edge that connected first is an edge
        // the cloud never learns about.
        using var late = new MqttClientFactory().CreateMqttClient();
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.ApplicationMessageReceivedAsync += message =>
        {
            heard.TrySetResult(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };

        await late.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await late.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/plant-7/drivers", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        var retained = EdgeDriversPayload.Read(await heard.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Null(retained.Refusal);
        Assert.Equal(["modbus-tcp", "opc-ua"], retained.Drivers);

        await late.DisconnectAsync();
    }

    [Fact]
    public async Task A_device_the_edge_cannot_read_is_carried_on_the_declaration_it_already_sends()
    {
        // ADR-0021. Nothing new is published to say this: the declaration is already the edge's
        // account of itself, and "which drivers this build has" and "what it currently cannot open"
        // are the same fact at the same moment.
        await StartBrokerAsync(refuse: false);

        var unreadable = new EdgeUnreadableDevices();
        unreadable.Replace([new EdgeUnreadableDevice("Retired PLC", "no-such-driver")]);

        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        using var uplink = Uplink(buffer, unreadable);
        await uplink.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => _declared.Count > 0, "the edge to declare itself");

        var declared = EdgeDriversPayload.Read(_declared.First());
        Assert.Null(declared.Refusal);
        Assert.Equal(["modbus-tcp", "opc-ua"], declared.Drivers);
        Assert.True(declared.UnreadableReported);
        var reported = Assert.Single(declared.Unreadable);
        Assert.Equal("Retired PLC", reported.Device);
        Assert.Equal("no-such-driver", reported.Driver);

        await uplink.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_device_that_becomes_readable_is_declared_again_without_it()
    {
        // The republish is triggered by the set changing, not by a timer or a second message kind
        // (ADR-0021 §4). Sending only on a change is also what stops this topic being rewritten
        // every couple of hundred milliseconds.
        await StartBrokerAsync(refuse: false);

        var unreadable = new EdgeUnreadableDevices();
        unreadable.Replace([new EdgeUnreadableDevice("Retired PLC", "no-such-driver")]);

        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        using var uplink = Uplink(buffer, unreadable);
        await uplink.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => _declared.Count > 0, "the edge to declare the unreadable device");

        // The driver is back: the same device is now read by one this build has, so the set is
        // computed again from the accepted configuration and comes out empty.
        unreadable.Replace([]);

        await WaitUntilAsync(
            () => _declared.Any(payload => EdgeDriversPayload.Read(payload).Unreadable.Count == 0),
            "a declaration without the device");

        var latest = EdgeDriversPayload.Read(_declared.Last());
        Assert.Null(latest.Refusal);
        Assert.Empty(latest.Unreadable);
        Assert.True(latest.UnreadableReported);

        await uplink.StopAsync(CancellationToken.None);
    }

    private async Task StartBrokerAsync(bool refuse)
    {
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(_port)
            .Build());

        if (refuse)
        {
            _broker.InterceptingPublishAsync += args =>
            {
                args.ProcessPublish = false;
                args.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
                return Task.CompletedTask;
            };
        }

        await _broker.StartAsync();

        // The cloud side, reading the edge's topic with the Gateway's own format.
        _cloud = new MqttClientFactory().CreateMqttClient();
        var tags = new Dictionary<Guid, DriverTag> { [Tag] = new(Tag, "t", TagValueKind.Numeric) };
        _cloud.ApplicationMessageReceivedAsync += message =>
        {
            var payload = message.ApplicationMessage.ConvertPayloadToString();

            // The edge's declaration travels on its own topic, and the cloud reads it with the
            // Gateway's own reader (ADR-0019 §8).
            if (message.ApplicationMessage.Topic.EndsWith("/drivers", StringComparison.Ordinal))
            {
                _declared.Enqueue(payload);
                return Task.CompletedTask;
            }

            var read = SamplePayload.Read(payload, tags);
            Assert.Null(read.Refusal);
            foreach (var sample in read.Accepted)
            {
                _received.Enqueue(sample);
            }

            return Task.CompletedTask;
        };
        await _cloud.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await _cloud.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/plant-7/samples", MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter("scada/edge/plant-7/drivers", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
    }

    private static List<TagReading> Samples(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TagReading(Tag, new TagValue.Numeric(i), Start.AddSeconds(i), Quality.Good))
            .ToList();

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

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
