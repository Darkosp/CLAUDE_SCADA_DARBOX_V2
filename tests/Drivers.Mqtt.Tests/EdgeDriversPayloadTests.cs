using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The declaration format (ADR-0019 §8): which driver keys the build at the plant has, said by the
/// edge itself because the cloud cannot work it out — the Gateway's own drivers are a different
/// list. A message carrying anything else is refused whole, because half a declared list is a list
/// the edge never stated.
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
    [InlineData("""{"version":2,"drivers":["modbus-tcp"]}""", "version 2 is not understood")]
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
