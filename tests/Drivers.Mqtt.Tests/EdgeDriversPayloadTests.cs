using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The declaration format (ADR-0019 §8, ADR-0021): which driver keys the build at the plant has, and
/// which devices it has been assigned and cannot read. Said by the edge itself, because the cloud
/// cannot work either out — the Gateway's own drivers are a different list, and only the edge knows
/// what its build can open. A message carrying anything else is refused whole, because half a
/// declared list is a list the edge never stated.
/// </summary>
public sealed class EdgeDriversPayloadTests
{
    [Fact]
    public void What_is_written_reads_back_exactly()
    {
        var result = EdgeDriversPayload.Read(EdgeDriversPayload.Write(["opc-ua", "modbus-tcp"]));

        Assert.Null(result.Refusal);
        Assert.Empty(result.Problems);
        Assert.Equal(["modbus-tcp", "opc-ua"], result.Drivers);
        Assert.Empty(result.Unreadable);
        Assert.True(result.UnreadableReported);
    }

    [Fact]
    public void The_devices_an_edge_cannot_read_travel_with_its_drivers()
    {
        // ADR-0021. One message, because "which drivers this build has" and "what it currently
        // cannot open" are the same fact at the same moment, answered by the same party.
        var result = EdgeDriversPayload.Read(EdgeDriversPayload.Write(
            ["opc-ua"],
            [new EdgeUnreadableDevice("Pump Station PLC", "modbus-tcp")]));

        Assert.Null(result.Refusal);
        Assert.True(result.UnreadableReported);
        var device = Assert.Single(result.Unreadable);
        Assert.Equal("Pump Station PLC", device.Device);
        Assert.Equal("modbus-tcp", device.Driver);
    }

    [Fact]
    public void A_version_1_declaration_is_read_and_reports_nothing_about_unreadable_devices()
    {
        // Absent is not empty (ADR-0021 §2): a version 1 message says which drivers the edge has and
        // makes no claim about what it cannot read, which is a different statement from "it can read
        // everything". A reader that flattened the two would turn an older edge's silence into a
        // claim it never made.
        var result = EdgeDriversPayload.Read("""{"version":1,"drivers":["opc-ua"]}""");

        Assert.Null(result.Refusal);
        Assert.Equal(["opc-ua"], result.Drivers);
        Assert.Empty(result.Unreadable);
        Assert.False(result.UnreadableReported);
    }

    [Fact]
    public void A_version_2_declaration_with_an_empty_array_says_it_can_read_everything()
    {
        // The other half of the pair above, and the reason the flag exists: same empty list, and a
        // different statement.
        var result = EdgeDriversPayload.Read("""{"version":2,"drivers":["opc-ua"],"unreadable":[]}""");

        Assert.Null(result.Refusal);
        Assert.Empty(result.Unreadable);
        Assert.True(result.UnreadableReported);
    }

    [Fact]
    public void A_version_2_declaration_with_no_unreadable_array_is_refused()
    {
        // The field is required from version 2 on: a build that writes version 2 and omits it is
        // contradicting its own format, and guessing which it meant would be inventing a claim.
        var result = EdgeDriversPayload.Read("""{"version":2,"drivers":["opc-ua"]}""");

        Assert.NotNull(result.Refusal);
        Assert.Contains("no unreadable array", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":2,"drivers":["opc-ua"],"unreadable":["Pump"]}""")]
    [InlineData("""{"version":2,"drivers":["opc-ua"],"unreadable":[{"device":"Pump"}]}""")]
    [InlineData("""{"version":2,"drivers":["opc-ua"],"unreadable":[{"driver":"modbus-tcp"}]}""")]
    [InlineData("""{"version":2,"drivers":["opc-ua"],"unreadable":[{"device":"  ","driver":"modbus-tcp"}]}""")]
    public void An_unreadable_entry_that_is_not_a_device_and_a_driver_is_refused(string json)
    {
        var result = EdgeDriversPayload.Read(json);

        Assert.NotNull(result.Refusal);
        Assert.Contains("unreadable 0 is not a device and a driver", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_device_reported_twice_is_refused()
    {
        var result = EdgeDriversPayload.Read(
            """{"version":2,"drivers":["opc-ua"],"unreadable":[{"device":"Pump","driver":"modbus-tcp"},{"device":"pump","driver":"modbus-tcp"}]}""");

        Assert.NotNull(result.Refusal);
        Assert.Contains("'Pump' is reported unreadable more than once", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_devices_are_carried_in_one_order_however_they_were_given()
    {
        // For the same reason the keys are: this message is republished whenever the set changes,
        // and the same set in a different order must not read as a change.
        Assert.Equal(
            EdgeDriversPayload.Write(["opc-ua"], [new EdgeUnreadableDevice("Pump", "modbus-tcp"), new EdgeUnreadableDevice("Compressor", "opc-ua")]),
            EdgeDriversPayload.Write(["opc-ua"], [new EdgeUnreadableDevice("Compressor", "opc-ua"), new EdgeUnreadableDevice("Pump", "modbus-tcp")]));
    }

    [Fact]
    public void The_keys_are_carried_in_one_order_however_they_were_given()
    {
        // So that a declaration republished after every reconnect is the same message, and a broker
        // holding it retained is not made to see a change that is not one.
        Assert.Equal(
            EdgeDriversPayload.Write(["opc-ua", "modbus-tcp"]),
            EdgeDriversPayload.Write(["modbus-tcp", "opc-ua"]));
    }

    [Fact]
    public void An_edge_that_declares_no_drivers_is_a_declaration_and_not_a_refusal()
    {
        // Empty is a statement: "I have none". It is not the same answer as never having declared,
        // which is what a missing declaration is — and the message never carries that, because a
        // message that was sent is a message that was said.
        var result = EdgeDriversPayload.Read(EdgeDriversPayload.Write([]));

        Assert.Null(result.Refusal);
        Assert.Empty(result.Drivers);
    }

    [Theory]
    [InlineData("""{"drivers":["modbus-tcp"]}""", "no version")]
    [InlineData("""{"version":3,"drivers":["modbus-tcp"],"unreadable":[]}""", "version 3 is not understood")]
    [InlineData("""{"version":0,"drivers":["modbus-tcp"]}""", "version 0 is not understood")]
    [InlineData("""{"version":1}""", "no drivers array")]
    [InlineData("""{"version":1,"drivers":"modbus-tcp"}""", "no drivers array")]
    [InlineData("""{"version":1,"drivers":["modbus-tcp",7]}""", "driver 1 is not a key")]
    [InlineData("""{"version":1,"drivers":["modbus-tcp","  "]}""", "driver 1 is not a key")]
    [InlineData("""{"version":1,"drivers":["modbus-tcp","MODBUS-TCP"]}""", "appears more than once")]
    [InlineData("""["modbus-tcp"]""", "not a JSON object")]
    [InlineData("""not json""", "not JSON")]
    public void A_message_this_build_does_not_read_is_refused_whole(string json, string reason)
    {
        var result = EdgeDriversPayload.Read(json);

        Assert.NotNull(result.Refusal);
        Assert.Contains(reason, result.Refusal, StringComparison.Ordinal);
        Assert.Empty(result.Drivers);
        Assert.Empty(result.Unreadable);
        Assert.False(result.UnreadableReported);
    }

    [Fact]
    public void Two_spellings_of_one_key_are_one_key_and_are_refused()
    {
        // The factories are looked up ignoring case (ADR-0002), so a list carrying one key twice
        // under two spellings is not two drivers — it is the edge contradicting itself.
        var result = EdgeDriversPayload.Read("""{"version":1,"drivers":["modbus-tcp","Modbus-TCP"]}""");

        Assert.NotNull(result.Refusal);
        Assert.Contains("'modbus-tcp' appears more than once", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reason_is_reported_and_not_only_the_first()
    {
        var result = EdgeDriversPayload.Read("""{"version":1,"drivers":[1,true]}""");

        Assert.NotNull(result.Refusal);
        Assert.Equal(2, result.Problems.Count);
        Assert.Contains("driver 0 is not a key", result.Problems);
        Assert.Contains("driver 1 is not a key", result.Problems);
    }
}
