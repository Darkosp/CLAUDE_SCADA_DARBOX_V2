using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The configuration format (ADR-0019): the devices an edge reads and, for each tag, the cloud's
/// own id — derived and published, never typed into an edge by hand. Its revision is the version
/// of the content alone, so an edge that already runs this configuration has nothing to do when
/// it arrives again; a message that does not describe its own revision is refused whole.
/// </summary>
public sealed class EdgeConfigurationPayloadTests
{
    private static readonly Guid Pressure = new("33333333-3333-4333-8333-333333333301");
    private static readonly Guid Running = new("33333333-3333-4333-8333-333333333302");

    private static readonly DateTimeOffset DerivedAt = new(2026, 9, 28, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void What_is_written_reads_back_exactly()
    {
        // Given in the one order a message carries devices in — by name — so that what reads back
        // is that same list and not merely the same devices.
        var devices = new[]
        {
            Plc(Tag(Pressure, "holding:0")),
            Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure"), Tag(Running, "ns=2;s=Pump1.Running", TagValueKind.Boolean)),
        };

        var result = EdgeConfigurationPayload.Read(EdgeConfigurationPayload.Write(devices, DerivedAt));

        Assert.Null(result.Refusal);
        Assert.Empty(result.Problems);

        // The message, not the objects: a device carries a dictionary and a list, which a record
        // compares by reference, so writing what was read is what "exactly" can mean here.
        Assert.Equal(
            EdgeConfigurationPayload.Write(devices, DerivedAt),
            EdgeConfigurationPayload.Write(result.Devices, DerivedAt));
        Assert.Equal(new[] { Pressure, Running }, result.Devices[1].Tags.Select(tag => tag.TagId));
        Assert.Equal(new[] { "holding:0" }, result.Devices[0].Tags.Select(tag => tag.Address));
        Assert.Equal(DerivedAt, result.GeneratedAtUtc);
        Assert.Equal(EdgeConfigurationPayload.RevisionOf(devices), result.Revision);
    }

    [Fact]
    public void An_edge_with_nothing_assigned_reads_as_a_configuration_of_no_devices()
    {
        // What the cloud publishes for an edge it no longer has: a later edge of the same name
        // must start with nothing rather than inherit the configuration of the one before it.
        var result = EdgeConfigurationPayload.Read(EdgeConfigurationPayload.Write([], DerivedAt));

        Assert.Null(result.Refusal);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void The_revision_is_the_same_however_the_devices_are_ordered()
    {
        var one = new[] { Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure")), Plc(Tag(Pressure, "holding:0")) };
        var other = new[] { Plc(Tag(Pressure, "holding:0")), Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure")) };

        Assert.Equal(EdgeConfigurationPayload.RevisionOf(one), EdgeConfigurationPayload.RevisionOf(other));
    }

    [Fact]
    public void The_revision_is_the_same_however_the_settings_are_ordered()
    {
        var one = new[] { Plc(Tag(Pressure, "holding:0")) with { Settings = new Dictionary<string, string> { ["host"] = "192.0.2.20", ["port"] = "502" } } };
        var other = new[] { Plc(Tag(Pressure, "holding:0")) with { Settings = new Dictionary<string, string> { ["port"] = "502", ["host"] = "192.0.2.20" } } };

        Assert.Equal(EdgeConfigurationPayload.RevisionOf(one), EdgeConfigurationPayload.RevisionOf(other));
    }

    [Fact]
    public void The_revision_changes_when_an_address_or_a_setting_changes()
    {
        // Otherwise a plant address could be corrected in the cloud and the edge would be told
        // nothing had changed.
        var before = new[] { Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure")) };
        var moved = new[] { Pump(Tag(Pressure, "ns=2;s=Pump1.Head")) };
        var reconfigured = new[] { Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure")) with { Settings = new Dictionary<string, string> { ["endpointUrl"] = "opc.tcp://192.0.2.11:4840/Server" } } };

        Assert.NotEqual(EdgeConfigurationPayload.RevisionOf(before), EdgeConfigurationPayload.RevisionOf(moved));
        Assert.NotEqual(EdgeConfigurationPayload.RevisionOf(before), EdgeConfigurationPayload.RevisionOf(reconfigured));
    }

    [Fact]
    public void A_message_whose_revision_does_not_describe_its_devices_is_refused()
    {
        var devices = new[] { Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure")) };
        var json = EdgeConfigurationPayload.Write(devices, DerivedAt)
            .Replace(EdgeConfigurationPayload.RevisionOf(devices), "sha256:0000000000000000000000000000000000000000000000000000000000000000", StringComparison.Ordinal);

        var result = EdgeConfigurationPayload.Read(json);

        Assert.Empty(result.Devices);
        Assert.Contains("the revision says", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_of_another_version_is_refused_whole()
    {
        var json = EdgeConfigurationPayload.Write([Pump(Tag(Pressure, "ns=2;s=Pump1.Pressure"))], DerivedAt)
            .Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);

        var result = EdgeConfigurationPayload.Read(json);

        Assert.Empty(result.Devices);
        Assert.Contains("version 2 is not understood", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_without_the_clouds_clock_is_refused_whole()
    {
        var json = $$"""{"version":1,"revision":"sha256:x","devices":[]}""";

        var result = EdgeConfigurationPayload.Read(json);

        Assert.Contains("generatedAtUtc", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[]}""")]
    [InlineData("""{"name":"Pump skid","scanIntervalMs":1000,"settings":{},"tags":[]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":0,"settings":{},"tags":[]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"tags":[]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[{"tagId":"00000000-0000-0000-0000-000000000000","address":"holding:0","kind":"Numeric"}]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[{"tagId":"33333333-3333-4333-8333-333333333301","kind":"Numeric"}]}""")]
    [InlineData("""{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[{"tagId":"33333333-3333-4333-8333-333333333301","address":"holding:0","kind":"numeric"}]}""")]
    public void A_device_that_cannot_be_read_is_refused_whole(string device)
    {
        // Half a configuration would leave an edge reading devices nobody asked it to read, so one
        // unreadable device refuses the message rather than being dropped from it.
        var json = $$"""{"version":1,"revision":"sha256:x","generatedAtUtc":"2026-09-28T18:00:00Z","devices":[{{device}}]}""";

        var result = EdgeConfigurationPayload.Read(json);

        Assert.Empty(result.Devices);
        Assert.NotNull(result.Refusal);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Two_devices_of_one_name_are_refused_whole()
    {
        // Two devices of one name would be two scan loops reading the same thing.
        var json = $$"""{"version":1,"revision":"sha256:x","generatedAtUtc":"2026-09-28T18:00:00Z","devices":[{{PumpBody()}},{{PumpBody()}}]}""";

        var result = EdgeConfigurationPayload.Read(json);

        Assert.Empty(result.Devices);
        Assert.Contains("appears more than once", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{}")]
    [InlineData("""{"version":1}""")]
    public void What_is_not_a_message_is_refused_rather_than_thrown(string json)
    {
        var result = EdgeConfigurationPayload.Read(json);

        Assert.Empty(result.Devices);
        Assert.NotNull(result.Refusal);
    }

    private static EdgeConfigurationDevice Pump(params EdgeConfigurationTag[] tags) =>
        new(
            "Pump skid",
            "opc-ua",
            1000,
            new Dictionary<string, string> { ["endpointUrl"] = "opc.tcp://192.0.2.10:4840/Server" },
            tags);

    private static EdgeConfigurationDevice Plc(params EdgeConfigurationTag[] tags) =>
        new(
            "Discharge PLC",
            "modbus-tcp",
            1000,
            new Dictionary<string, string> { ["host"] = "192.0.2.20", ["port"] = "502" },
            tags);

    private static EdgeConfigurationTag Tag(Guid tagId, string address, TagValueKind kind = TagValueKind.Numeric) =>
        new(tagId, address, kind);

    private static string PumpBody() =>
        """{"name":"Pump skid","driver":"opc-ua","scanIntervalMs":1000,"settings":{},"tags":[{"tagId":"33333333-3333-4333-8333-333333333301","address":"holding:0","kind":"Numeric"}]}""";
}
