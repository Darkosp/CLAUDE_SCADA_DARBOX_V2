using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// What an edge says about its own build, arriving over the link (ADR-0019 §8): the cloud records
/// the declaration, and names any device already assigned to that edge which the declaration says
/// it cannot read — the state that used to be loud at the plant and silent in the cloud.
/// </summary>
public sealed class EdgeDriverDeclarationsTests : IAsyncLifetime
{
    private static readonly Guid EdgeId = new("11111111-1111-4111-8111-111111111102");
    private static readonly Guid ReadableId = new("22222222-2222-4222-8222-222222222202");
    private static readonly Guid UnreadableId = new("33333333-3333-4333-8333-333333333302");

    private readonly int _port = FreePort();
    private MqttServer? _broker;

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
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }
    }

    [Fact]
    public async Task A_declaration_is_recorded_and_the_device_the_edge_cannot_read_is_named()
    {
        var plant = Provisioning();
        using var publisher = plant.Publisher;
        await StartPublisherAsync(plant);

        await PublishAsync("scada/edge/edge-a/drivers", EdgeDriversPayload.Write(["modbus-tcp"]));
        // Both, in order: the declaration is recorded and the catalogue reloaded *before* the
        // audit row is appended, so waiting only for the first leaves the assertion below racing
        // the write it checks. Measured 2026-10-02 — this failed in four of eight whole-solution
        // runs and never once in isolation.
        await WaitUntilAsync(() => plant.Catalogue.Declarations.Count == 1, "the declaration to be recorded");
        await WaitUntilAsync(() => plant.Audit.Entries.Count == 1, "the declaration to be audited");
        // The catalogue now holds what the edge said, and the cloud's own list is not involved.
        Assert.Equal(["modbus-tcp"], plant.Catalogue.Declarations[0].Drivers);
        Assert.Equal(["modbus-tcp"], plant.Source.Current.Edges.Single().DeclaredDriverKeys);
        Assert.NotNull(plant.Source.Current.Edges.Single().DriversDeclaredAt);

        // Recorded in the audit trail (ADR-0011), by no user: an edge reports with its certificate,
        // not with an account.
        var entry = Assert.Single(plant.Audit.Entries);
        Assert.Equal("edge.drivers_declared", entry.Action);
        Assert.Equal("edge", entry.EntityType);
        Assert.Equal(EdgeId, entry.EntityId);
        Assert.Null(entry.ActorUserId);

        // And the device this declaration says the edge cannot read is named — not left to be
        // discovered on a screen at the plant.
        Assert.Equal([UnreadableId], Assert.IsType<List<Guid>>(entry.Detail!["unreadableDeviceIds"]));

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_declaration_under_a_name_with_no_edge_is_ignored()
    {
        var plant = Provisioning();
        using var publisher = plant.Publisher;
        await StartPublisherAsync(plant);

        // A name the catalogue does not know: an edge that has been deleted, or a certificate older
        // than the catalogue. Nothing is invented for it.
        await PublishAsync("scada/edge/ghost/drivers", EdgeDriversPayload.Write(["modbus-tcp"]));

        // A declaration that must be recorded, so the silence above is the pipeline working rather
        // than nothing having arrived yet.
        await PublishAsync("scada/edge/edge-a/drivers", EdgeDriversPayload.Write(["modbus-tcp"]));
        await WaitUntilAsync(() => plant.Catalogue.Declarations.Count == 1, "the known edge's declaration");
        // The audit row is appended after the reload, so it gets its own wait (see the test above).
        await WaitUntilAsync(() => plant.Audit.Entries.Count == 1, "the known edge's declaration to be audited");

        Assert.Equal(EdgeId, plant.Catalogue.Declarations[0].EdgeId);
        Assert.Single(plant.Audit.Entries);

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_declaration_that_cannot_be_read_changes_nothing()
    {
        var plant = Provisioning();
        using var publisher = plant.Publisher;
        await StartPublisherAsync(plant);

        // A version this build does not read, refused whole rather than guessed at (the rule the
        // configuration payload already follows).
        await PublishAsync("scada/edge/edge-a/drivers", """{"version":2,"drivers":["modbus-tcp"]}""");

        await PublishAsync("scada/edge/edge-a/drivers", EdgeDriversPayload.Write(["modbus-tcp"]));
        await WaitUntilAsync(() => plant.Catalogue.Declarations.Count == 1, "the readable declaration");
        // The audit row is appended after the reload, so it gets its own wait (see the first test).
        await WaitUntilAsync(() => plant.Audit.Entries.Count == 1, "the readable declaration to be audited");

        Assert.Equal(["modbus-tcp"], plant.Catalogue.Declarations[0].Drivers);
        Assert.Equal(["modbus-tcp"], plant.Source.Current.Edges.Single().DeclaredDriverKeys);
        Assert.Single(plant.Audit.Entries);

        await publisher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The Gateway's own provisioning service over a catalogue in memory, with one edge holding two
    /// devices: one whose driver it will declare it has, and one whose driver it will not.
    /// </summary>
    private Provisioned Provisioning()
    {
        var catalogue = new FakeCatalogue();
        var site = new Site { Id = Guid.NewGuid(), TenantId = catalogue.Tenant.Id, Name = "Skopje" };
        catalogue.Sites.Add(site);
        catalogue.Edges.Add(new Edge { Id = EdgeId, TenantId = catalogue.Tenant.Id, Name = "edge-a" });
        catalogue.Devices.Add(Device(ReadableId, site.Id, "Pump skid", "modbus-tcp"));
        catalogue.Devices.Add(Device(UnreadableId, site.Id, "Compressor", "opc-ua"));

        var source = new TagCatalogSource(new TagCatalog(
            catalogue.Tenant,
            catalogue.Sites,
            [],
            catalogue.Devices,
            [],
            null,
            catalogue.Edges));

        var audit = new RecordingAuditLog();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var options = Options.Create(new EdgeProvisioningOptions
        {
            Host = "127.0.0.1",
            Port = _port,
            UsesTls = false,
        });

        var declarations = new EdgeDriverDeclarations(
            source,
            catalogue,
            new ConfigurationReloader(catalogue, source),
            audit,
            options,
            factory.CreateLogger<EdgeDriverDeclarations>());

        return new Provisioned(
            catalogue,
            source,
            audit,
            logs,
            new EdgeConfigurationPublisher(source, declarations, options, factory.CreateLogger<EdgeConfigurationPublisher>()));
    }

    private static Device Device(Guid id, Guid siteId, string name, string driverKey) => new()
    {
        Id = id,
        SiteId = siteId,
        Name = name,
        DriverKey = driverKey,
        EdgeId = EdgeId,
    };

    /// <summary>The edge's own publish: retained, on the topic its certificate confines it to.</summary>
    private async Task PublishAsync(string topic, string payload)
    {
        using var edge = new MqttClientFactory().CreateMqttClient();
        await edge.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await edge.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build());
        await edge.DisconnectAsync();
    }

    /// <summary>
    /// Starts the Gateway's provisioning service and waits until it is on the broker, because the
    /// subscription is made as it connects: a test that publishes before that is asking what the
    /// Gateway does with a message it was never handed, not what it does with a declaration.
    /// </summary>
    private static async Task StartPublisherAsync(Provisioned plant)
    {
        await plant.Publisher.StartAsync(CancellationToken.None);

        await WaitUntilAsync(
            () => plant.Logs.Entries.Any(entry => entry.Contains("Connected to the broker", StringComparison.Ordinal)),
            "the Gateway to connect to the broker");
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

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed record Provisioned(
        FakeCatalogue Catalogue,
        TagCatalogSource Source,
        RecordingAuditLog Audit,
        CapturingLoggerProvider Logs,
        EdgeConfigurationPublisher Publisher);
}
