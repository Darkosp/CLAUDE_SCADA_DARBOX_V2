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
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Uplink;
using ScadaDarbox.Modules.Drivers.Mqtt;

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

    private UplinkService Uplink(SampleBuffer buffer) => new(
        Options.Create(new EdgeOptions
        {
            Id = "plant-7",
            Broker = new BrokerOptions { Host = "127.0.0.1", Port = _port },
            Buffer = new BufferOptions { Path = _path },
        }),
        buffer,
        NullLogger<UplinkService>.Instance);

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
            var read = SamplePayload.Read(message.ApplicationMessage.ConvertPayloadToString(), tags);
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
