using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Provisioning;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// What the cloud derives for an edge (ADR-0019 §4): exactly the devices assigned to it, carrying
/// the driver, settings and addresses the cloud holds — and, for every tag, the Gateway's own tag
/// id, which is what lets an edge be configured without anyone typing an id into it.
/// </summary>
public sealed class EdgeConfigurationTests
{
    [Fact]
    public void An_edges_configuration_is_the_devices_assigned_to_it_and_no_others()
    {
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);
        fixture.Assign(fixture.Plc);

        var names = EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge)
            .Select(device => device.Name)
            .ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("Pump skid", names);
        Assert.Contains("Discharge PLC", names);
    }

    [Fact]
    public void A_device_the_gateway_polls_itself_is_not_in_the_configuration()
    {
        // ADR-0019 §2: assigning a device to an edge is what moves it, and nothing else does.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);

        Assert.Equal(new[] { "Pump skid" }, Names(fixture));
    }

    [Fact]
    public void A_device_assigned_to_another_edge_is_not_in_this_ones_configuration()
    {
        var fixture = new ProvisioningFixture();
        var other = fixture.AddEdge("south-01");
        fixture.Assign(fixture.Plc, other);
        fixture.Assign(fixture.Pump);

        Assert.Equal(new[] { "Pump skid" }, Names(fixture));
    }

    [Fact]
    public void The_edges_link_device_is_not_in_its_configuration()
    {
        // The link is the Gateway's own device — it subscribes to what the edge publishes — so the
        // edge neither reads it nor is told it exists.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);

        Assert.Equal(new[] { "Pump skid" }, Names(fixture));
    }

    [Fact]
    public void Every_tag_carries_the_gateways_own_id_and_the_clouds_address()
    {
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);

        var device = Assert.Single(EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge));
        var tag = Assert.Single(device.Tags);

        Assert.Equal(fixture.Pressure.Id, tag.TagId);
        Assert.Equal("ns=2;s=Pump1.Pressure", tag.Address);
        Assert.Equal(TagValueKind.Numeric, tag.Kind);
    }

    [Fact]
    public void A_devices_driver_settings_and_interval_travel_as_the_cloud_holds_them()
    {
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);

        var device = Assert.Single(EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge));

        Assert.Equal("opc-ua", device.Driver);
        Assert.Equal(1500, device.ScanIntervalMs);
        Assert.Equal("opc.tcp://192.0.2.10:4840/Server", device.Settings["endpointUrl"]);
    }

    [Fact]
    public void An_edge_with_nothing_assigned_reads_nothing()
    {
        var fixture = new ProvisioningFixture();

        Assert.Empty(EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge));
    }

    private static IReadOnlyList<string> Names(ProvisioningFixture fixture) =>
        EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge)
            .Select(device => device.Name)
            .ToList();

    private sealed class ProvisioningFixture
    {
        internal Tenant Tenant { get; } = new() { Id = Guid.NewGuid(), Name = "Darbo" };

        internal Site Site { get; }

        internal Device Pump { get; }

        internal Device Plc { get; }

        /// <summary>The Gateway's own device, carrying the link to <see cref="Edge"/> (ADR-0016).</summary>
        internal Device Link { get; }

        internal Tag Pressure { get; }

        internal Edge Edge { get; }

        internal List<Device> Devices { get; }

        internal List<Tag> Tags { get; }

        internal List<Edge> Edges { get; } = [];

        internal TagCatalog Catalog { get; private set; }

        internal ProvisioningFixture()
        {
            Site = new Site { Id = Guid.NewGuid(), TenantId = Tenant.Id, Name = "Skopje" };

            Pump = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = Site.Id,
                Name = "Pump skid",
                DriverKey = "opc-ua",
                ConnectionSettings = new Dictionary<string, string>
                {
                    ["endpointUrl"] = "opc.tcp://192.0.2.10:4840/Server",
                },
                ScanInterval = TimeSpan.FromMilliseconds(1500),
            };

            Plc = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = Site.Id,
                Name = "Discharge PLC",
                DriverKey = "modbus-tcp",
            };

            Link = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = Site.Id,
                Name = "Edge link",
                DriverKey = "mqtt",
            };

            Pressure = new Tag
            {
                Id = Guid.NewGuid(),
                DeviceId = Pump.Id,
                Name = "Discharge Pressure",
                ValueKind = TagValueKind.Numeric,
                SourceAddress = "ns=2;s=Pump1.Pressure",
            };

            Devices = [Pump, Plc, Link];
            Tags = [Pressure];

            Edge = AddEdge("north-01");
            Edge.LinkDeviceId = Link.Id;

            Catalog = Rebuild();
        }

        /// <summary>Adds an edge, for the next rebuild.</summary>
        internal Edge AddEdge(string name)
        {
            var edge = new Edge { Id = Guid.NewGuid(), TenantId = Tenant.Id, Name = name };
            Edges.Add(edge);
            return edge;
        }

        /// <summary>Assigns a device to an edge — the one way a device moves (ADR-0019 §2).</summary>
        internal void Assign(Device device, Edge? edge = null)
        {
            device.EdgeId = (edge ?? Edge).Id;
            Catalog = Rebuild();
        }

        private TagCatalog Rebuild() =>
            Catalog = new TagCatalog(Tenant, [Site], [], Devices, Tags, null, Edges);
    }
}
