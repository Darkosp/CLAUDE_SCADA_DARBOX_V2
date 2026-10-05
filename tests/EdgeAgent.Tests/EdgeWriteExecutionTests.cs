using Microsoft.Extensions.Logging.Abstractions;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// The edge's half of the write conversation (ADR-0023): a request the cloud published is really
/// carried out against a device, through the same driver module the Gateway would have used.
/// </summary>
/// <remarks>
/// <para>
/// This is the half that had no test when ADR-0023 was written. The cloud's half and the wire
/// format were covered — a request goes out with an id and a result comes back with it — and the
/// edge's half was covered by compiling. A compile proves the types line up; it does not prove that
/// the tag id in the request finds the device in the configuration, that the address in the
/// configuration reaches the right register, or that the scale is applied the way the read path
/// applies it.
/// </para>
/// <para>
/// So the device here is a real Modbus slave on a real loopback port, and the assertion is on the
/// register it holds afterwards — not on "the driver was asked". A stub that recorded the call
/// would pass just as happily with the wrong register or a dropped scale, which is exactly the
/// class of mistake this test exists to catch.
/// </para>
/// </remarks>
public sealed class EdgeWriteExecutionTests
{
    private static readonly Guid PressureTag = new("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task A_write_the_cloud_asked_for_reaches_the_device()
    {
        await using var device = ModbusTestSlave.Start();
        device.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var executor = Executor(device.Port, ("holding:0?scale=0.01", PressureTag));

        var result = await executor.ExecuteAsync(
            Request(PressureTag, new TagValue.Numeric(5.5)),
            CancellationToken.None);

        Assert.True(result.Written);
        Assert.Null(result.Reason);

        // The device changed, through the configuration's address and the read path's own scale:
        // 5.5 bar is 550 hundredths of a bar on the wire (ADR-0005 — scaling is the driver's, and
        // a write that skipped it would land 5.5 and look like a device fault).
        Assert.Equal(550, device.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    [Fact]
    public async Task The_result_names_the_request_it_answers()
    {
        // The id is the whole mechanism of the cloud's half (ADR-0023 §2). An edge that answered
        // with a fresh id, or with the tag's id in its place, would leave the cloud waiting out its
        // deadline for a write that succeeded.
        await using var device = ModbusTestSlave.Start();

        var executor = Executor(device.Port, ("holding:0", PressureTag));
        var writeId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(
            new WriteRequestResult(writeId, PressureTag, new TagValue.Numeric(1), Refusal: null),
            CancellationToken.None);

        Assert.True(result.Written);
        Assert.Equal(writeId, result.WriteId);
        Assert.Equal(PressureTag, result.TagId);
    }

    [Fact]
    public async Task A_tag_this_edge_does_not_read_is_refused_rather_than_guessed_at()
    {
        // The edge writes only what its configuration says it reads (ADR-0019): the address is the
        // configuration's, not the message's, so a tag this edge does not hold has nowhere to go.
        await using var device = ModbusTestSlave.Start();
        device.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var executor = Executor(device.Port, ("holding:0?scale=0.01", PressureTag));

        var result = await executor.ExecuteAsync(
            Request(Guid.NewGuid(), new TagValue.Numeric(5.5)),
            CancellationToken.None);

        Assert.False(result.Written);
        Assert.NotNull(result.Reason);
        Assert.Contains("does not read", result.Reason, StringComparison.Ordinal);

        // And nothing was written to the device it does hold.
        Assert.Equal(420, device.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    [Fact]
    public async Task A_driver_this_edge_does_not_have_is_refused_with_that_reason()
    {
        // ADR-0021's fact, on the write path: an edge is sent what the cloud derived for it, and
        // what it can actually read is what it has a driver for. "The edge could not reach the
        // device" and "the edge has no driver for it" are told apart rather than collapsed
        // (ADR-0003).
        await using var device = ModbusTestSlave.Start();

        var configuration = new EdgeConfigurationSource();
        configuration.Replace(
            [
                new EdgeConfigurationDevice(
                    "Skid",
                    "opcua",
                    1000,
                    new Dictionary<string, string>(),
                    [new EdgeConfigurationTag(PressureTag, "holding:0", TagValueKind.Numeric)]),
            ],
            "rev-1");

        // Only the Modbus factory is registered, which is the situation the cloud must have
        // prevented and this edge must survive.
        var executor = new EdgeWriteExecutor(
            configuration,
            [new ModbusTcpDriverFactory(TimeProvider.System)],
            NullLogger<EdgeWriteExecutor>.Instance);

        var result = await executor.ExecuteAsync(Request(PressureTag, new TagValue.Numeric(1)), CancellationToken.None);

        Assert.False(result.Written);
        Assert.NotNull(result.Reason);
        Assert.Contains("no 'opcua' driver", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_is_not_listening_comes_back_as_failed_with_a_reason()
    {
        // A device that is down is a failure to report, not a write to leave unanswered: the cloud
        // is waiting, and "the edge tried and could not" is a different fact from "the edge never
        // answered" (ADR-0023 §4).
        var configuration = new EdgeConfigurationSource();
        configuration.Replace(
            [
                new EdgeConfigurationDevice(
                    "Skid",
                    ModbusKey,
                    1000,
                    // A port nothing is listening on.
                    new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = "1" },
                    [new EdgeConfigurationTag(PressureTag, "holding:0", TagValueKind.Numeric)]),
            ],
            "rev-1");

        var executor = new EdgeWriteExecutor(
            configuration,
            [new ModbusTcpDriverFactory(TimeProvider.System)],
            NullLogger<EdgeWriteExecutor>.Instance);

        var result = await executor.ExecuteAsync(Request(PressureTag, new TagValue.Numeric(1)), CancellationToken.None);

        Assert.False(result.Written);
        Assert.NotNull(result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task An_edge_with_no_configuration_yet_refuses_every_write()
    {
        // Nothing accepted means nothing assigned, which means nothing this edge may write. The
        // refusal is a reason and not silence, so the cloud does not wait out its deadline.
        var executor = new EdgeWriteExecutor(
            new EdgeConfigurationSource(),
            [new ModbusTcpDriverFactory(TimeProvider.System)],
            NullLogger<EdgeWriteExecutor>.Instance);

        var result = await executor.ExecuteAsync(Request(PressureTag, new TagValue.Numeric(1)), CancellationToken.None);

        Assert.False(result.Written);
        Assert.Contains("does not read", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reply_the_edge_would_send_reads_back_at_the_cloud_as_that_result()
    {
        // The two halves joined: what this executor produces is what the cloud's reader must read.
        // Either one changing without the other is the failure this asserts against.
        await using var device = ModbusTestSlave.Start();

        var executor = Executor(device.Port, ("holding:0?scale=0.01", PressureTag));
        var writeId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(
            new WriteRequestResult(writeId, PressureTag, new TagValue.Numeric(2.5), Refusal: null),
            CancellationToken.None);

        var wire = WritePayload.WriteResult(result.WriteId, result.TagId, result.Written, result.Reason);
        var readBack = WritePayload.ReadResult(wire);

        Assert.Null(readBack.Refusal);
        Assert.Equal(writeId, readBack.WriteId);
        Assert.True(readBack.Written);
        Assert.Equal(250, device.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    /// <summary>The driver key, from the factory rather than typed here, so a rename cannot rot.</summary>
    private static string ModbusKey => new ModbusTcpDriverFactory(TimeProvider.System).DriverKey;

    [Fact]
    public async Task A_device_that_accepts_the_connection_and_then_says_nothing_comes_back_as_a_failure()
    {
        // The path ADR-0023's entry recorded as unexecuted anywhere, and the shape the Phase 7 walk
        // found in the *read* path: a device that accepts a connection and then stops answering is
        // silence that looks like a working plant.
        //
        // What this asserts is deliberately weaker than "the executor's deadline fired", because
        // measuring it showed that it does not. ModbusTcpDriver sets a five-second socket read
        // timeout, which raises a transport error rather than honouring the cancellation token — and
        // the failure took about twenty seconds to arrive, so neither bound is what stopped it.
        // What matters for ADR-0023 is what the cloud is told: a failure, with a reason, rather than
        // silence or a pretence of success. The exact mechanism is recorded in the executor's own
        // remarks as a measured fact rather than a claim.
        //
        // `using` would be wrong here: disposing the listener closes the socket, which turns a
        // device that says nothing into one that hangs up, and a hang-up is a different failure.
        var blackhole = SilentListener.Start();

        try
        {
            var executor = Executor(blackhole.Port, ("holding:0?scale=0.01", PressureTag));

            var started = DateTime.UtcNow;
            var result = await executor.ExecuteAsync(Request(PressureTag, new TagValue.Numeric(5.5)), CancellationToken.None);
            var elapsed = DateTime.UtcNow - started;

            Assert.False(result.Written);
            Assert.NotNull(result.Reason);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));

            // Nothing hung: whatever bounded it, a bounded failure came back.
            //
            // This was a minute when the test was written, because the bound was not understood: a
            // dead device took about twenty seconds and nobody could say why. The why was NModbus's
            // transport retrying a failed request three times, so the driver's five-second timeout
            // was worth twenty. With the retry off (ADR-0023's finding) the arithmetic is one
            // attempt times five seconds, and a loose bound is no longer the honest one -- a
            // regression that put the retry back would pass a minute-wide assertion and fail this.
            Assert.True(
                elapsed < TimeSpan.FromSeconds(15),
                $"a dead device took {elapsed.TotalSeconds:0.0} s to report; the driver's own bound is 5 s and anything near this is a retry the driver did not ask for");
        }
        finally
        {
            await blackhole.DisposeAsync();
        }
    }

    private static WriteRequestResult Request(Guid tagId, TagValue value) =>
        new(Guid.NewGuid(), tagId, value, Refusal: null);

    /// <summary>
    /// An executor over a configuration that reads one Modbus device on the given port.
    /// </summary>
    private static EdgeWriteExecutor Executor(int port, params (string Address, Guid TagId)[] tags)
    {
        var configuration = new EdgeConfigurationSource();

        configuration.Replace(
            [
                new EdgeConfigurationDevice(
                    "Discharge PLC",
                    ModbusKey,
                    1000,
                    new Dictionary<string, string>
                    {
                        ["host"] = "127.0.0.1",
                        ["port"] = port.ToString(),
                        ["unitId"] = "1",
                    },
                    [.. tags.Select(tag => new EdgeConfigurationTag(tag.TagId, tag.Address, TagValueKind.Numeric))]),
            ],
            "rev-1");

        return new EdgeWriteExecutor(
            configuration,
            [new ModbusTcpDriverFactory(TimeProvider.System)],
            NullLogger<EdgeWriteExecutor>.Instance);
    }
}
