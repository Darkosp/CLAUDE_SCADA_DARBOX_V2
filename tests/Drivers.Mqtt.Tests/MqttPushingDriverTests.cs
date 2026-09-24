using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The MQTT pushing driver against a real, in-process broker: what arrives is handed over with the
/// source's own time; what cannot be read does not stop it; a lost broker is reconnected to.
/// </summary>
public sealed class MqttPushingDriverTests : IAsyncLifetime
{
    private const string Topic = "scada/edge/test/samples";

    private static readonly Guid Pressure = new("44444444-4444-4444-8444-444444444401");
    private static readonly DriverTag[] Tags = [new(Pressure, "pressure", TagValueKind.Numeric)];

    private readonly int _port = FreePort();
    private MqttServer _broker = null!;
    private TaskCompletionSource _subscribed = NewSignal();

    public async Task InitializeAsync() => _broker = await StartBrokerAsync();

    public async Task DisposeAsync()
    {
        await _broker.StopAsync();
        _broker.Dispose();
    }

    [Fact]
    public async Task A_sample_is_handed_over_with_the_time_the_source_measured_it()
    {
        // Minutes ago, as anything that crossed a link from a buffer was: the time handed on is the
        // one in the message, not the time it arrived.
        var measuredAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var sink = new RecordingSink();

        await using var run = await StartAsync(sink);
        await PublishAsync(SamplePayload.Write([new TagReading(Pressure, new TagValue.Numeric(4.2), measuredAt, Quality.Good)]));

        var sample = Assert.Single(await sink.WaitForAsync(1));
        Assert.Equal(new TagValue.Numeric(4.2), sample.Value);
        Assert.Equal(measuredAt, sample.SourceTimestampUtc);
        Assert.Equal(Quality.Good, sample.Quality);
    }

    [Fact]
    public async Task A_batch_is_handed_over_in_one_call()
    {
        var sink = new RecordingSink();
        var start = DateTimeOffset.UtcNow.AddMinutes(-3);

        await using var run = await StartAsync(sink);
        await PublishAsync(SamplePayload.Write(Enumerable.Range(0, 5)
            .Select(i => new TagReading(Pressure, new TagValue.Numeric(i), start.AddSeconds(i), Quality.Good))));

        Assert.Equal(5, (await sink.WaitForAsync(5)).Count);
        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task A_message_it_cannot_read_does_not_stop_it()
    {
        var sink = new RecordingSink();

        await using var run = await StartAsync(sink);
        await PublishAsync("this is not a message");
        await PublishAsync("""{ "version": 99, "samples": [] }""");
        await PublishAsync(SamplePayload.Write([new TagReading(Pressure, new TagValue.Numeric(1.5), DateTimeOffset.UtcNow, Quality.Good)]));

        var sample = Assert.Single(await sink.WaitForAsync(1));
        Assert.Equal(new TagValue.Numeric(1.5), sample.Value);
    }

    [Fact]
    public async Task It_reconnects_to_a_broker_that_went_away_and_came_back()
    {
        var sink = new RecordingSink();

        await using var run = await StartAsync(sink);

        // The broker goes away for long enough that at least one reconnection attempt fails, then
        // comes back on the same port.
        await _broker.StopAsync();
        _broker.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(3));
        _subscribed = NewSignal();
        _broker = await StartBrokerAsync();

        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await PublishAsync(SamplePayload.Write([new TagReading(Pressure, new TagValue.Numeric(2.5), DateTimeOffset.UtcNow, Quality.Good)]));

        Assert.Equal(new TagValue.Numeric(2.5), Assert.Single(await sink.WaitForAsync(1)).Value);
    }

    [Fact]
    public async Task Run_again_after_stopping_it_hands_each_message_over_once()
    {
        // The scanner runs a pushing driver again after a failure. A message handler left behind by
        // the last run would hand every message over twice.
        var sink = new RecordingSink();
        await using var driver = Create();

        using (var first = new CancellationTokenSource())
        {
            var firstRun = driver.RunAsync(Tags, sink, first.Token);
            await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await first.CancelAsync();
            await firstRun;
        }

        using var second = new CancellationTokenSource();
        var secondRun = driver.RunAsync(Tags, sink, second.Token);

        await PublishAsync(SamplePayload.Write([new TagReading(Pressure, new TagValue.Numeric(3.5), DateTimeOffset.UtcNow, Quality.Good)]));
        await sink.WaitForAsync(1);

        // Long enough for a second delivery to have arrived, if there were going to be one.
        await Task.Delay(500);
        Assert.Single(sink.Samples);

        await second.CancelAsync();
        await secondRun;
    }

    private IPushingDeviceDriver Create() => new MqttPushingDriverFactory().Create(new Device
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Name = "Edge",
        DriverKey = "mqtt",
        ConnectionSettings = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["topic"] = Topic,
        },
    });

    /// <summary>Runs a driver until disposed, having waited for its subscription to reach the broker.</summary>
    private async Task<RunningDriver> StartAsync(RecordingSink sink)
    {
        var driver = Create();
        var stop = new CancellationTokenSource();
        var run = driver.RunAsync(Tags, sink, stop.Token);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return new RunningDriver(driver, stop, run);
    }

    private async Task<MqttServer> StartBrokerAsync()
    {
        var factory = new MqttServerFactory();
        var options = factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(_port)
            .Build();

        var broker = factory.CreateMqttServer(options);
        broker.ClientSubscribedTopicAsync += _ =>
        {
            _subscribed.TrySetResult();
            return Task.CompletedTask;
        };

        await broker.StartAsync();
        return broker;
    }

    private async Task PublishAsync(string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(Topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await publisher.DisconnectAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class RecordingSink : IPushedSampleSink
    {
        public ConcurrentQueue<IReadOnlyList<TagReading>> Calls { get; } = new();

        public List<TagReading> Samples => Calls.SelectMany(call => call).ToList();

        public Task AcceptAsync(IReadOnlyList<TagReading> samples, CancellationToken cancellationToken)
        {
            Calls.Enqueue(samples);
            return Task.CompletedTask;
        }

        public async Task<List<TagReading>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Samples.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected {count} sample(s), got {Samples.Count}.");
                }

                await Task.Delay(50);
            }

            return Samples;
        }
    }

    private sealed class RunningDriver(IPushingDeviceDriver driver, CancellationTokenSource stop, Task run) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await run;
            await driver.DisposeAsync();
            stop.Dispose();
        }
    }
}
