using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The Gateway publishing each edge's configuration (ADR-0019 §4): retained on that edge's own
/// topic, again when the content changes and not when anything else does, and emptied when the
/// edge is deleted.
/// </summary>
public sealed class EdgeConfigurationPublishingTests : IAsyncLifetime
{
    private static readonly Guid EdgeId = new("11111111-1111-4111-8111-111111111101");
    private static readonly Guid PumpId = new("22222222-2222-4222-8222-222222222201");
    private static readonly Guid PressureId = new("33333333-3333-4333-8333-333333333301");

    private TestBroker? _broker;

    /// <summary>
    /// What the publisher logged during this test.
    /// </summary>
    /// <remarks>
    /// Attached and kept, because this test's failure mode is a silence: the first configuration
    /// never arrives and the assertion says only that. It has already earnt its place — the first
    /// captured failure showed the publisher connecting and publishing the configuration
    /// successfully, which ruled the publisher out and moved the question to delivery.
    /// </remarks>
    private readonly CapturingLoggerProvider _logs = new();

    public async Task InitializeAsync() => _broker = await TestBroker.StartAsync();

    public async Task DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_change_is_published_retained_to_that_edges_topic()
    {
        var source = new TagCatalogSource(Catalogue(assigned: false));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        // The publish is waited for on the broker, not on a subscriber: what this test owns is that
        // the cloud published and the broker retained it, and delivery to a client that connected
        // afterwards is MQTTnet's in-process server, which is not reliable at it. See
        // TestBroker.RetainsAsync for the measurement and the upstream issue.
        await WaitUntilRetainedAsync("the first configuration to be published");

        // The edge's device is assigned: the cloud's configuration of it changes.
        source.Set(Catalogue(assigned: true));
        await WaitUntilRetainedAsync("the changed configuration to be published", devices: 1);

        // And it is retained: an edge that subscribes later — one that was off while this
        // happened — is given it the moment it asks (ADR-0019 §4). This is the one place delivery to
        // a late subscriber is asserted, on a client connected to this broker for the first time.
        var late = await SubscribeAsync("scada/edge/edge-a/config", window: TimeSpan.FromSeconds(2));
        Assert.True(late.Retain, "the configuration an edge is given on subscribing must be retained");
        var read = EdgeConfigurationPayload.Read(late.Payload!);
        Assert.Null(read.Refusal);
        Assert.Equal(new[] { "Pump skid" }, read.Devices.Select(device => device.Name));

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_configuration_that_has_not_changed_is_not_published_again()
    {
        // Otherwise every unrelated edit — a folder renamed, another device added — would restart
        // the acquisition of every edge (ADR-0019 §5).
        var source = new TagCatalogSource(Catalogue(assigned: true));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        await WaitUntilRetainedAsync("the first configuration to be published");

        // Counted on the broker rather than on one subscriber. A subscriber here would report what it
        // happened to be sent, and a publish that was made and not delivered would read as a publish
        // that was not made — the wrong conclusion in both directions.
        var publishes = await PublishCountAsync();

        source.Set(Catalogue(assigned: true));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal(publishes, await PublishCountAsync());

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_edge_that_is_deleted_has_its_configuration_emptied()
    {
        // The topic keeps the name, so a later edge under that name must start reading nothing
        // rather than inherit the devices of the edge that used to answer to it.
        var source = new TagCatalogSource(Catalogue(assigned: true));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        await WaitUntilRetainedAsync("the first configuration to be published");

        source.Set(Catalogue(assigned: true, withEdge: false));
        await WaitUntilRetainedAsync("the edge's configuration to be emptied", devices: 0);

        await publisher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Waits until the broker holds this topic's retained configuration, and optionally until it is of
    /// a given size.
    /// </summary>
    /// <remarks>
    /// The device count is read back out of the payload, so waiting for "the emptied configuration"
    /// and waiting for "the changed configuration" are the same wait with a different expectation —
    /// and neither is a guess about how long a publish takes.
    /// </remarks>
    private async Task WaitUntilRetainedAsync(string what, int? devices = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var read = await RetainedConfigurationAsync();
            if (read is not null && (devices is null || read.Devices.Count == devices))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                await FailWithEvidenceAsync($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>The payload the broker holds for this edge's topic, or null when it holds none.</summary>
    private async Task<EdgeConfigurationPayloadResult?> RetainedConfigurationAsync()
    {
        var messages = await _broker!.RetainedMessagesAsync();
        var message = messages.FirstOrDefault(candidate => candidate.Topic == "scada/edge/edge-a/config");
        return message is null ? null : EdgeConfigurationPayload.Read(message.ConvertPayloadToString());
    }

    /// <summary>How many retained messages the broker is holding, which is how many publishes landed.</summary>
    private async Task<int> PublishCountAsync() => (await _broker!.RetainedMessagesAsync()).Count;

    /// <summary>
    /// Fails, saying what the publisher logged, what the broker holds and what it thinks of its
    /// clients — because this class's failure mode is a silence and a silence cannot be diagnosed.
    /// </summary>
    private async Task FailWithEvidenceAsync(string message)
    {
        var retained = string.Join(", ", await _broker!.RetainedTopicsAsync());
        var clients = string.Join(" | ", await _broker.ClientSubscriptionsAsync());

        Assert.Fail(
            $"{message}{Environment.NewLine}"
            + $"The publisher logged:{Environment.NewLine}{string.Join(Environment.NewLine, _logs.Entries)}{Environment.NewLine}"
            + $"The broker retains: [{retained}]{Environment.NewLine}"
            + $"The broker's sessions: [{clients}]");
    }

    private EdgeConfigurationPublisher Publisher(TagCatalogSource source)
    {
        var options = Options.Create(new EdgeProvisioningOptions
        {
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UsesTls = false,
        });

        // One client, both directions (ADR-0019 §8): the declaration handler is built over the same
        // catalogue the publisher reads, so what this test starts is the service the Gateway starts.
        var catalogue = new FakeCatalogue();

        return new EdgeConfigurationPublisher(
            source,
            new EdgeDriverDeclarations(
                source,
                catalogue,
                new ConfigurationReloader(catalogue, source),
                new RecordingAuditLog(),
                options,
                NullLogger<EdgeDriverDeclarations>.Instance),
            // The write conversation shares this connection (ADR-0023). Nothing here writes, so the
            // router is only present because the publisher is what hands it the way to publish.
            new EdgeWriteRouter(options, NullLogger<EdgeWriteRouter>.Instance),
            options,
            _logs.CreateLogger<EdgeConfigurationPublisher>());
    }

    /// <summary>A catalogue with this edge, and one device either assigned to it or not.</summary>
    private static TagCatalog Catalogue(bool assigned, bool withEdge = true)
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
            EdgeId = assigned ? EdgeId : null,
        };
        var pressure = new Tag
        {
            Id = PressureId,
            DeviceId = pump.Id,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            SourceAddress = "ns=2;s=Pump1.Pressure",
        };

        return new TagCatalog(tenant, [site], [], [pump], [pressure], null, withEdge ? [edge] : []);
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


    

    /// <summary>An edge's side of the topic: everything it was sent, in order.</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly IMqttClient _client;
        private readonly ConcurrentQueue<MqttApplicationMessage> _messages;

        internal Subscription(IMqttClient client, ConcurrentQueue<MqttApplicationMessage> messages)
        {
            _client = client;
            _messages = messages;
        }


        internal MqttApplicationMessage Last => _messages.Last();

        /// <summary>Whether the last message arrived because it was retained, not because it was published.</summary>
        internal bool Retain => _messages.Last().Retain;

        internal string? Payload => _messages.Last().ConvertPayloadToString();

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
