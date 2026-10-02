using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The link device the Gateway writes for an edge, so that nothing about an edge is typed by hand
/// (ADR-0022): one device per edge, its topic the edge's own name, everything else a constant of the
/// deployment, and an edge that already has a link left exactly as it is.
/// </summary>
public sealed class LinkDeviceProvisionerTests
{
    [Fact]
    public async Task An_edge_with_no_link_is_given_one_whose_topic_is_its_own_name()
    {
        var plant = new Plant();
        var edge = plant.AddEdge("plant-b");
        var edgeId = edge.Id;

        var wrote = await plant.ProvisionAsync();

        Assert.Equal(1, wrote);

        var link = Assert.Single(plant.Devices.Devices);
        Assert.Equal("mqtt", link.DriverKey);
        // The edge now names it. Recorded before the pass, because the service replaces the edge
        // rather than editing it in place — the same thing the repository does.
        Assert.Equal(link.Id, plant.Catalogue.Edges.Single().LinkDeviceId);
        Assert.Equal(edgeId, plant.Catalogue.Edges.Single().Id);

        // The edge's own name is the whole address (ADR-0017), which is why nothing here can be
        // typed wrong: the name in the certificate and the name in the topic are one value.
        Assert.Equal("scada/edge/plant-b/samples", link.ConnectionSettings["topic"]);

        // And everything else is the deployment's, not an operator's.
        Assert.Equal("broker", link.ConnectionSettings["host"]);
        Assert.Equal("8884", link.ConnectionSettings["port"]);
        Assert.Equal("true", link.ConnectionSettings["tls"]);
        Assert.Equal("/app/mqtt/scada-gateway.crt", link.ConnectionSettings["certFile"]);
    }

    [Fact]
    public async Task An_edge_that_already_has_a_link_keeps_it()
    {
        // ADR-0022 §4, and the reason rollout is additive rather than a rename: the broker keys a
        // session by the client id, the MQTT driver derives that id from the device id, so a link
        // replaced rather than reused arrives under a new key and the queue held for the old one is
        // never delivered.
        var plant = new Plant();
        var existing = plant.AddExistingDevice("Edge plant-b", "mqtt");
        var edge = plant.AddEdge("plant-b");
        edge.LinkDeviceId = existing.Id;
        plant.Rebuild();

        var wrote = await plant.ProvisionAsync();

        Assert.Equal(0, wrote);
        Assert.Single(plant.Devices.Devices);
        Assert.Equal(existing.Id, plant.Catalogue.Edges.Single().LinkDeviceId);
    }

    [Fact]
    public async Task Every_edge_gets_its_own_link_so_one_silent_plant_cannot_mark_another_bad()
    {
        // ADR-0022 §2, which is the whole reason deriving the device does not collapse the links
        // into one shared subscription: one limit for every edge would let one silent plant mark
        // every other plant's tags Bad (ADR-0019 §5, ADR-0016).
        var plant = new Plant();
        plant.AddEdge("plant-b");
        plant.AddEdge("plant-c");

        Assert.Equal(2, await plant.ProvisionAsync());

        var links = plant.Devices.Devices.OrderBy(device => device.Name).ToList();
        Assert.Equal(2, links.Count);
        Assert.NotEqual(links[0].Id, links[1].Id);
        Assert.Equal("scada/edge/plant-b/samples", links[0].ConnectionSettings["topic"]);
        Assert.Equal("scada/edge/plant-c/samples", links[1].ConnectionSettings["topic"]);

        // Two devices means two derived client ids, so two independent broker sessions — and a
        // session is what the broker keys the queue to.
        Assert.NotEqual(links[0].Id, links[1].Id);
    }

    [Fact]
    public async Task A_second_pass_writes_nothing()
    {
        // It watches rather than creating once, so an edge added to a running Gateway is picked up
        // — and that makes idempotence the thing to prove.
        var plant = new Plant();
        plant.AddEdge("plant-b");

        Assert.Equal(1, await plant.ProvisionAsync());
        Assert.Equal(0, await plant.ProvisionAsync());

        Assert.Single(plant.Devices.Devices);
    }

    [Fact]
    public async Task The_edges_own_limits_are_what_the_link_device_carries()
    {
        // ADR-0022 §3: the limit belongs to the link, and the link is the edge. The device is
        // derived from it and is not overridable, so this is the only place it is chosen.
        var plant = new Plant();
        var edge = plant.AddEdge("plant-b");
        edge.LinkStaleness = TimeSpan.FromSeconds(15);
        edge.LinkSessionExpiry = TimeSpan.FromHours(48);

        await plant.ProvisionAsync();

        var link = Assert.Single(plant.Devices.Devices);
        Assert.Equal("15", link.ConnectionSettings["stalenessSeconds"]);
        Assert.Equal("48", link.ConnectionSettings["sessionExpiryHours"]);
    }

    [Fact]
    public async Task Nothing_is_derived_while_provisioning_is_off()
    {
        // With provisioning off there is no broker to subscribe to and no certificate to present,
        // so a derived link would be a device that cannot connect.
        var plant = new Plant(enabled: false);
        plant.AddEdge("plant-b");

        Assert.Equal(0, await plant.ProvisionAsync());
        Assert.Empty(plant.Devices.Devices);
    }

    [Fact]
    public async Task The_link_sits_in_the_site_of_the_devices_that_edge_reads()
    {
        // A device is Site-scoped (ADR-0004) and an edge is not — it hangs off the tenant, because
        // its name is unique across the deployment. So the link takes its placement from the plant
        // it carries, which is also where an operator will look for it.
        var plant = new Plant();
        var edge = plant.AddEdge("plant-b");
        var site = plant.Catalogue.Sites.Single();
        var device = plant.AddVisibleDevice("Pump skid", "modbus-tcp", site.Id);
        device.EdgeId = edge.Id;
        plant.Rebuild();

        await plant.ProvisionAsync();

        Assert.Equal(site.Id, Assert.Single(plant.Devices.Devices).SiteId);
    }

    /// <summary>One deployment in memory: a catalogue, a device store, and the settings.</summary>
    private sealed class Plant
    {
        private readonly TagCatalogSource _source;
        private readonly FakeDeviceRepository _devices;
        private readonly LinkDeviceProvisioner _provisioner;

        internal FakeCatalogue Catalogue { get; }

        internal FakeDeviceRepository Devices => _devices;

        internal Plant(bool enabled = true, bool addSite = true)
        {
            Catalogue = new FakeCatalogue();
            _devices = new FakeDeviceRepository();

            if (addSite)
            {
                Catalogue.Sites.Add(new Site
                {
                    Id = Guid.NewGuid(),
                    TenantId = Catalogue.Tenant.Id,
                    Name = "Skopje",
                });
            }

            _source = new TagCatalogSource(Empty());
            _provisioner = new LinkDeviceProvisioner(
                _source,
                _devices,
                Catalogue,
                new ConfigurationReloader(Catalogue, _source),
                Options.Create(new EdgeProvisioningOptions { Enabled = enabled, UsesTls = true }),
                NullLogger<LinkDeviceProvisioner>.Instance);
        }

        internal Edge AddEdge(string name)
        {
            var edge = new Edge { Id = Guid.NewGuid(), TenantId = Catalogue.Tenant.Id, Name = name };
            Catalogue.Edges.Add(edge);
            Rebuild();
            return edge;
        }

        internal Device AddExistingDevice(string name, string driverKey, Guid? siteId = null)
        {
            var device = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = siteId ?? Catalogue.Sites.FirstOrDefault()?.Id ?? Guid.Empty,
                Name = name,
                DriverKey = driverKey,
            };

            _devices.Devices.Add(device);
            return device;
        }

        /// <summary>
        /// A device the catalogue can see, which is what the provisioner reads — so this is how a
        /// device assigned to an edge reaches <see cref="LinkDeviceProvisioner"/>'s choice of Site.
        /// </summary>
        internal Device AddVisibleDevice(string name, string driverKey, Guid siteId)
        {
            var device = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                Name = name,
                DriverKey = driverKey,
            };

            Catalogue.Devices.Add(device);
            Rebuild();
            return device;
        }

        internal Task<int> ProvisionAsync() => CountAsync();

        /// <summary>
        /// The catalogue the provisioner reads. Rebuilt after every edit, because it asks the
        /// catalogue rather than the repository — which is what makes a link appear for an edge the
        /// operator has just made.
        /// </summary>
        internal void Rebuild() => _source.Set(Empty());

        private async Task<int> CountAsync()
        {
            var before = _devices.Devices.Count;
            await _provisioner.ProvisionAsync(CancellationToken.None);
            return _devices.Devices.Count - before;
        }

        private TagCatalog Empty() => new(
            Catalogue.Tenant,
            Catalogue.Sites,
            [],
            Catalogue.Devices,
            [],
            null,
            Catalogue.Edges);
    }
}
