using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The Gateway publishing a write request (ADR-0023 §3): on the edge's own topic, at QoS 1, and
/// **never retained**.
/// </summary>
/// <remarks>
/// <para>
/// A retained write is one an edge receives the moment it reconnects, having missed the moment. A
/// late sample is still true of its own moment; a late command is a request to change a plant after
/// the reason for it has passed. That is why the flag's absence is a decision rather than a detail,
/// and why it is asserted here rather than left to inspection: the publisher that sends a write is
/// a different one from the publisher that sends a configuration, and the two differ in exactly
/// this one flag.
/// </para>
/// <para>
/// The in-process broker is a real MQTT server, so a retained message really is delivered to a
/// later subscriber. That is what makes the second subscription in each test meaningful: it finds
/// nothing when nothing was retained, and it would find something if the flag were set.
/// </para>
/// </remarks>
public sealed class EdgeWritePublishingTests : IAsyncLifetime
{
    private static readonly Guid EdgeId = new("11111111-1111-4111-8111-111111111102");
    private static readonly Guid PumpId = new("22222222-2222-4222-8222-222222222202");
    private static readonly Guid PressureId = new("33333333-3333-4333-8333-333333333302");
    private TestBroker? _broker;

    public async Task InitializeAsync() => _broker = await TestBroker.StartAsync();

    public async Task DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_write_the_Gateway_publishes_is_not_retained()
    {
        var options = Settings();
        var router = new EdgeWriteRouter(Options.Create(options), NullLogger<EdgeWriteRouter>.Instance);
        var source = new TagCatalogSource(Catalogue());

        // The real publisher, because it is what gives the router the connection to publish on —
        // the same wiring the Gateway starts (ADR-0023 §1).
        using var publisher = Publisher(source, router, options);
        await publisher.StartAsync(CancellationToken.None);

        // Somebody is listening when the write is published, which is the only way a live write is
        // delivered at all.
        using var listening = await SubscribeAsync("scada/edge/edge-a/writes");

        // The publisher connects on its own loop, and the router is told about it there. Asking
        // whether it can publish yet is the honest wait: a delay would be a guess at how long a
        // loopback connection takes.
        await WaitUntilAsync(() => router.CanPublish, "the publisher to give the router its connection");

        // The deadline is the router's own five seconds, and nobody answers here: what this test is
        // about is what was published, not what came back.
        var outcome = await router.WriteAsync("edge-a", PressureId, new TagValue.Numeric(4.5), CancellationToken.None);
        Assert.False(outcome.Confirmed);

        await WaitUntilAsync(() => listening.Count >= 1, "the write to be published");

        var request = WritePayload.ReadRequest(listening.Payload!);
        Assert.Null(request.Refusal);
        Assert.Equal(PressureId, request.TagId);
        Assert.Equal(new TagValue.Numeric(4.5), request.Value);

        // Nothing was retained, so a connection made after the fact finds nothing. This is the
        // whole guarantee, and it is the second subscription that tests it: the first one above
        // proves the topic is live and the payload readable.
        using var late = await SubscribeAsync("scada/edge/edge-a/writes", window: TimeSpan.FromSeconds(2));
        Assert.Equal(0, late.Count);

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_configuration_beside_it_is_retained_so_the_difference_is_the_write_alone()
    {
        // The control, and without it the first test would pass just as happily on a deployment
        // that had broken retaining altogether. The same publisher, the same connection, the same
        // broker: a configuration is retained and a write is not.
        var options = Settings();
        var router = new EdgeWriteRouter(Options.Create(options), NullLogger<EdgeWriteRouter>.Instance);
        var source = new TagCatalogSource(Catalogue());

        using var publisher = Publisher(source, router, options);
        await publisher.StartAsync(CancellationToken.None);

        // Somebody is already listening while the configuration is published, so the wait is for the
        // configuration itself rather than for the connection — and so the subscriber below is
        // unambiguously a *later* one, which is the only case the retain flag is about. A live
        // message arrives with the flag clear; a retained one arrives with it set, and conflating
        // the two is exactly the mistake this test exists to catch.
        using var early = await SubscribeAsync("scada/edge/edge-a/config");
        await WaitUntilAsync(() => early.Count >= 1, "the configuration to be published");

        using var late = await SubscribeAsync("scada/edge/edge-a/config", window: TimeSpan.FromSeconds(3));
        Assert.True(late.Count >= 1, "the configuration must reach a connection made after it was published");
        Assert.True(late.Retain, "the configuration an edge is given on subscribing must be retained");

        await publisher.StopAsync(CancellationToken.None);
    }

    private EdgeProvisioningOptions Settings() => new()
    {
        Enabled = true,
        Host = "127.0.0.1",
        Port = _broker!.Port,
        UsesTls = false,
    };

    private static EdgeConfigurationPublisher Publisher(
        TagCatalogSource source,
        EdgeWriteRouter router,
        EdgeProvisioningOptions options)
    {
        var wrapped = Options.Create(options);
        var catalogue = new FakeCatalogue();

        return new EdgeConfigurationPublisher(
            source,
            new EdgeDriverDeclarations(
                source,
                catalogue,
                new ConfigurationReloader(catalogue, source),
                new RecordingAuditLog(),
                wrapped,
                NullLogger<EdgeDriverDeclarations>.Instance),
            router,
            wrapped,
            NullLogger<EdgeConfigurationPublisher>.Instance);
    }

    /// <summary>One edge, with one device assigned to it and one tag on that device.</summary>
    private static TagCatalog Catalogue()
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Darbo" };
        var site = new Site { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Skopje" };
        var edge = new Edge { Id = EdgeId, TenantId = tenant.Id, Name = "edge-a" };
        var pump = new Device
        {
            Id = PumpId,
            SiteId = site.Id,
            Name = "Pump skid",
            DriverKey = "opc-ua",
            EdgeId = EdgeId,
        };
        var pressure = new Tag
        {
            Id = PressureId,
            DeviceId = pump.Id,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            SourceAddress = "ns=2;s=Pump1.Pressure",
        };

        return new TagCatalog(tenant, [site], [], [pump], [pressure], null, [edge]);
    }

    private async Task<Subscription> SubscribeAsync(string topic, TimeSpan? window = null)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var messages = new ConcurrentQueue<MqttApplicationMessage>();
        client.ApplicationMessageReceivedAsync += message =>
        {
            messages.Enqueue(message.ApplicationMessage);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build());
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        if (window is { } delay)
        {
            await Task.Delay(delay);
        }

        return new Subscription(client, messages);
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

            await Task.Delay(50);
        }
    }


    private sealed class Subscription(IMqttClient client, ConcurrentQueue<MqttApplicationMessage> messages)
        : IDisposable
    {
        internal int Count => messages.Count;

        internal bool Retain => messages.Last().Retain;

        internal string? Payload => messages.Last().ConvertPayloadToString();

        public void Dispose() => client.Dispose();
    }
}
