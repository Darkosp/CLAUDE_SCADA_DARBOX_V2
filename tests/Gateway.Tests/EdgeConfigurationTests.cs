using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Modules.Drivers.Mqtt;

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

        var names = Names(fixture);

        Assert.Equal(2, names.Count);
        Assert.Contains("Pump skid", names);
        Assert.Contains("Discharge PLC", names);
    }

    [Fact]
    public void A_device_with_no_tags_is_left_out_of_the_configuration_and_named()
    {
        // ADR-0020. The payload reader refuses a device whose tag array is empty, and refuses the
        // whole message with it — so deriving one would take every other device down with it. The
        // device stays assigned; it is reported instead, because a device that is assigned and
        // silently unread is the shape of finding ADR-0019 §8 closed on the other axis.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);
        fixture.Assign(fixture.Plc);
        fixture.RemoveTags(fixture.Plc);

        var devices = Derive(fixture, out var omitted);

        Assert.Equal(new[] { "Pump skid" }, devices.Select(device => device.Name));
        Assert.Equal(new[] { "Discharge PLC" }, omitted);
    }

    [Fact]
    public void A_device_whose_tags_all_go_leaves_nothing_to_refuse()
    {
        // The same rule from the other direction, and the case that would otherwise be a
        // configuration the cloud's own reader rejects: every assigned device omitted derives an
        // empty list, which is a configuration in its own right (ADR-0019 §4) and not a failure.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);
        fixture.RemoveTags(fixture.Pump);

        var devices = Derive(fixture, out var omitted);

        Assert.Empty(devices);
        Assert.Equal(new[] { "Pump skid" }, omitted);
    }

    [Fact]
    public void Nothing_derived_is_a_configuration_the_edges_reader_refuses()
    {
        // The invariant ADR-0020 exists for, stated over every fixture shape: whatever the builder
        // produces, the payload it becomes is one the edge accepts. The reader refuses an empty tag
        // array, so this fails the moment the omission is removed.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Pump);
        fixture.Assign(fixture.Plc);
        fixture.RemoveTags(fixture.Plc);

        var devices = Derive(fixture, out _);
        var payload = EdgeConfigurationPayload.Write(devices, DateTimeOffset.UnixEpoch);

        var result = EdgeConfigurationPayload.Read(payload);

        Assert.Null(result.Refusal);
        Assert.Equal(new[] { "Pump skid" }, result.Devices.Select(device => device.Name));
    }

    [Fact]
    public void An_edges_configuration_carries_no_device_with_an_empty_tag_array()
    {
        // Checked on the derived shape rather than through the reader, so this fails for the right
        // reason when the omission goes: a device with no tags is present again.
        var fixture = new ProvisioningFixture();
        fixture.Assign(fixture.Plc);
        fixture.RemoveTags(fixture.Plc);

        Assert.DoesNotContain(Derive(fixture, out _), device => device.Tags.Count == 0);
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

        var device = Assert.Single(Derive(fixture, out _));
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

        var device = Assert.Single(Derive(fixture, out _));

        Assert.Equal("opc-ua", device.Driver);
        Assert.Equal(1500, device.ScanIntervalMs);
        Assert.Equal("opc.tcp://192.0.2.10:4840/Server", device.Settings["endpointUrl"]);
    }

    [Fact]
    public void An_edge_with_nothing_assigned_reads_nothing()
    {
        var fixture = new ProvisioningFixture();

        var devices = Derive(fixture, out var omitted);

        Assert.Empty(devices);
        Assert.Empty(omitted);
    }

    private static IReadOnlyList<EdgeConfigurationDevice> Derive(
        ProvisioningFixture fixture,
        out IReadOnlyList<string> omitted) =>
        EdgeConfigurationBuilder.DevicesFor(fixture.Catalog, fixture.Edge, out omitted);

    private static IReadOnlyList<string> Names(ProvisioningFixture fixture) =>
        Derive(fixture, out _).Select(device => device.Name).ToList();

    private sealed class ProvisioningFixture
    {
        internal Tenant Tenant { get; } = new() { Id = Guid.NewGuid(), Name = "Darbo" };

        internal Site Site { get; }

        internal Device Pump { get; }

        internal Device Plc { get; }

        /// <summary>The Gateway's own device, carrying the link to <see cref="Edge"/> (ADR-0016).</summary>
        internal Device Link { get; }

        internal Tag Pressure { get; }

        internal Tag PlcLevel { get; }

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

            PlcLevel = new Tag
            {
                Id = Guid.NewGuid(),
                DeviceId = Plc.Id,
                Name = "Discharge Level",
                ValueKind = TagValueKind.Numeric,
                SourceAddress = "holding:3",
            };

            Devices = [Pump, Plc, Link];
            Tags = [Pressure, PlcLevel];

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

        /// <summary>
        /// Takes every tag off a device, as if none had been added yet — the state ADR-0020 is
        /// about. The catalogue is rebuilt, which is what an operator's own edit does.
        /// </summary>
        internal void RemoveTags(Device device)
        {
            Tags.RemoveAll(tag => tag.DeviceId == device.Id);
            Catalog = Rebuild();
        }

        private TagCatalog Rebuild() =>
            Catalog = new TagCatalog(Tenant, [Site], [], Devices, Tags, null, Edges);
    }
}
