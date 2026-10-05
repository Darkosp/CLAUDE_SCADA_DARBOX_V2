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

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// The uplink receiving a write the cloud published and answering it (ADR-0023 §1–§2): a real
/// message off a real broker, into the running service, out to a real device, and the result back
/// on the topic the cloud is listening to.
/// </summary>
/// <remarks>
/// <para>
/// This is the last joint of the write path that no test crossed. Everything else was covered —
/// the payload, the cloud's matching, the executor against a real device, the ACL and the retain
/// rule against a real broker — and this is the piece in the middle that takes a message off the
/// wire and does something with it. It was covered by compiling, which is what the defect in
/// <see cref="EdgeWriteExecutor"/> was hiding behind.
/// </para>
/// <para>
/// The distinction this asserts is the one the executor's defect turned on: a request that was
/// **understood and not carried out** gets an answer, because an edge that stays silent leaves the
/// cloud to report "not confirmed" about something the edge knew the answer to.
/// </para>
/// </remarks>
public sealed class EdgeWriteUplinkTests : IAsyncLifetime
{
    private static readonly Guid PressureTag = new("44444444-4444-4444-8444-444444444444");

    private readonly int _port = FreePort();
    private readonly string _bufferPath =
        Path.Combine(Path.GetTempPath(), $"scada-write-uplink-{Guid.NewGuid():N}.db");

    private MqttServer? _broker;
    private IMqttClient? _cloud;

    public async Task InitializeAsync()
    {
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(_port)
            .Build());

        await _broker.StartAsync();

        _cloud = new MqttClientFactory().CreateMqttClient();
        await _cloud.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
    }

    public async Task DisposeAsync()
    {
        if (_cloud is not null)
        {
            if (_cloud.IsConnected)
            {
                await _cloud.DisconnectAsync();
            }

            _cloud.Dispose();
        }

        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }

        foreach (var leftover in new[] { _bufferPath, _bufferPath + "-wal", _bufferPath + "-shm" })
        {
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }
    }

    [Fact]
    public async Task A_write_published_on_the_link_reaches_the_device_and_the_answer_comes_back()
    {
        await using var device = ModbusTestSlave.Start();
        device.DataStore.HoldingRegisters.WritePoints(0, [420]);

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 1_000);
        var configuration = new EdgeConfigurationSource();
        using var uplink = Uplink(buffer, configuration, device.Port);

        await uplink.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => configuration.Revision is not null, "the edge to accept the configuration");

        var results = await ListenForResultsAsync();
        var writeId = Guid.NewGuid();

        await PublishWriteUntilAnsweredAsync(
            WritePayload.WriteRequest(writeId, PressureTag, new TagValue.Numeric(5.5)),
            results,
            "the edge to answer the write");
        await uplink.StopAsync(CancellationToken.None);

        var result = WritePayload.ReadResult(results.Single());
        Assert.Null(result.Refusal);
        Assert.Equal(writeId, result.WriteId);
        Assert.Equal(PressureTag, result.TagId);
        Assert.True(result.Written);

        // And the plant changed, which is the only reason any of the rest of it matters.
        Assert.Equal(550, device.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    [Fact]
    public async Task A_write_for_a_tag_this_edge_does_not_read_is_answered_with_why()
    {
        // The case the executor's defect broke: a request that is perfectly readable and names a tag
        // this edge does not hold. It must be answered — and with a reason — because the alternative
        // is the cloud waiting out its deadline and telling an operator "not confirmed" about a
        // failure the edge knew about immediately.
        await using var device = ModbusTestSlave.Start();

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 1_000);
        var configuration = new EdgeConfigurationSource();
        using var uplink = Uplink(buffer, configuration, device.Port);

        await uplink.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => configuration.Revision is not null, "the edge to accept the configuration");

        var results = await ListenForResultsAsync();
        var writeId = Guid.NewGuid();
        var unknown = Guid.NewGuid();

        await PublishWriteUntilAnsweredAsync(
            WritePayload.WriteRequest(writeId, unknown, new TagValue.Numeric(1)),
            results,
            "the edge to answer the write it cannot perform");
        await uplink.StopAsync(CancellationToken.None);

        var result = WritePayload.ReadResult(results.Single());
        Assert.Null(result.Refusal);
        Assert.Equal(writeId, result.WriteId);
        Assert.False(result.Written);
        Assert.NotNull(result.Reason);
        Assert.Contains("does not read", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_message_that_cannot_be_read_is_left_unanswered()
    {
        // The one case with no answer, and it is deliberate: there is no id to answer with, so a
        // reply is impossible rather than withheld. The cloud's deadline is then the only true thing
        // to report, which is why this is not a silence to be fixed.
        await using var device = ModbusTestSlave.Start();

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 1_000);
        var configuration = new EdgeConfigurationSource();
        using var uplink = Uplink(buffer, configuration, device.Port);

        await uplink.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => configuration.Revision is not null, "the edge to accept the configuration");

        var results = await ListenForResultsAsync();

        await _cloud!.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/plant-7/writes")
            .WithPayload("""{"version":9,"writeId":"44444444-4444-4444-8444-444444444444"}""")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        // A live control proves the edge is up and the topic is subscribed: the same service answers
        // a readable request. Without it, "nothing arrived" would also be what a stopped uplink
        // looks like.
        //
        // The control goes through the retry-publishing helper rather than a single publish, and
        // that is the same race the helper exists for: a write is not retained (ADR-0023 §3), so one
        // sent in the moment between the uplink connecting and subscribing is genuinely gone.
        var controlId = Guid.NewGuid();

        await PublishWriteUntilAnsweredAsync(
            WritePayload.WriteRequest(controlId, PressureTag, new TagValue.Numeric(2.5)),
            results,
            "the control write to be answered");

        // A moment longer, so a duplicate answer to the control -- which the retry helper can
        // produce, and which the edge answers honestly, once per request it received -- has arrived
        // before the assertion below runs. The assertion is about which request was answered, and
        // without this it was also accidentally an assertion about how many times the test managed
        // to ask, which is what made it flake under load.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await uplink.StopAsync(CancellationToken.None);

        // Every answer is to the control, so the version 9 message beside it was not answered.
        // Asserted on the ids rather than on the count: the count depends on whether a duplicate
        // publication happened to get through, and the ids are the thing that actually matters --
        // a reply that did not name a request in flight is the case the format refuses.
        var answers = results.Select(WritePayload.ReadResult).ToList();

        Assert.NotEmpty(answers);
        Assert.All(answers, answer => Assert.Null(answer.Refusal));
        Assert.All(answers, answer => Assert.True(answer.Written));
        Assert.All(answers, answer => Assert.Equal(controlId, answer.WriteId));

        // And the write really happened, which is what makes the control a control.
        Assert.Equal(250, device.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    private UplinkService Uplink(SampleBuffer buffer, EdgeConfigurationSource configuration, int devicePort)
    {
        var consumer = new EdgeConfigurationConsumer(configuration, buffer, NullLogger<EdgeConfigurationConsumer>.Instance);

        // The configuration the cloud would have derived: one Modbus device, one tag on it, and the
        // port of the slave the test started. Everything after this is the service's own work.
        configuration.Replace(
            [
                new EdgeConfigurationDevice(
                    "Discharge PLC",
                    new ModbusTcpDriverFactory(TimeProvider.System).DriverKey,
                    1000,
                    new Dictionary<string, string>
                    {
                        ["host"] = "127.0.0.1",
                        ["port"] = devicePort.ToString(),
                        ["unitId"] = "1",
                    },
                    [new EdgeConfigurationTag(PressureTag, "holding:0?scale=0.01", TagValueKind.Numeric)]),
            ],
            "rev-write");

        return new UplinkService(
            Options.Create(new EdgeOptions
            {
                Id = "plant-7",
                Broker = new BrokerOptions { Host = "127.0.0.1", Port = _port },
                Buffer = new BufferOptions { Path = _bufferPath },
            }),
            buffer,
            consumer,
            new EdgeUnreadableDevices(),
            new EdgeWriteExecutor(configuration, Drivers, NullLogger<EdgeWriteExecutor>.Instance),
            Drivers,
            NullLogger<UplinkService>.Instance);
    }

    /// <summary>Subscribes the cloud's side to this edge's results (ADR-0023 §2).</summary>
    private async Task<ConcurrentQueue<string>> ListenForResultsAsync()
    {
        var results = new ConcurrentQueue<string>();
        _cloud!.ApplicationMessageReceivedAsync += message =>
        {
            results.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };

        await _cloud.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/+/write-results", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        return results;
    }

    /// <summary>
    /// Publishes a write until the edge answers it.
    /// </summary>
    /// <remarks>
    /// A write is not retained (ADR-0023 §3), which is the point of the decision — and it means a
    /// message published in the moment between the uplink connecting and subscribing is gone, not
    /// queued. That is the product behaving correctly, so a test that published once would be
    /// asserting on a race. Republishing is safe here: the request is idempotent, it carries one
    /// write id, and the id is what the answer is matched by.
    /// </remarks>
    private async Task PublishWriteUntilAnsweredAsync(
        string payload,
        ConcurrentQueue<string> results,
        string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (results.IsEmpty)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await _cloud!.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("scada/edge/plant-7/writes")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

            await Task.Delay(200);
        }
    }

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

    private static IDeviceDriverFactory[] Drivers =>
    [
        new ModbusTcpDriverFactory(TimeProvider.System),
        new OpcUaDriverFactory(TimeProvider.System),
    ];

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
