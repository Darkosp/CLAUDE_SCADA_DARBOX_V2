using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Modbus;

namespace ScadaDarbox.Drivers.Modbus.Tests;

/// <summary>
/// What the driver says when a tag has no value. The readings themselves are checked next
/// door; what is checked here is the part a Bad reading cannot carry — why — and the fact that
/// a mistyped address and an unreachable device stop looking identical from the outside.
/// </summary>
public sealed class ModbusTcpDriverLoggingTests
{
    [Fact]
    public async Task A_mistyped_address_reads_Bad_and_the_log_says_what_is_wrong_with_it()
    {
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var log = new RecordingLogger<ModbusTcpDriver>();
        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System, log);
        await driver.ConnectAsync(CancellationToken.None);

        var broken = new DriverTag(Guid.NewGuid(), "not-an-address", TagValueKind.Numeric);
        var reading = Assert.Single(await driver.ReadAsync([broken], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);

        var warning = Assert.Single(log.At(LogLevel.Warning));
        Assert.Contains(broken.TagId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains("not-an-address", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not in the form 'area:offset'", warning.Message, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{slave.Port}", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mistyped_address_and_an_unreachable_device_no_longer_read_the_same()
    {
        // This is the distinction the step exists for: the same Bad reading, two faults that
        // are not the same, and nothing in the reading itself to tell a reader which is which.
        await using var slave = ModbusTestSlave.Start();

        var tagId = Guid.NewGuid();
        var mistypedLog = new RecordingLogger<ModbusTcpDriver>();
        await using var reachable =
            new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System, mistypedLog);
        await reachable.ConnectAsync(CancellationToken.None);
        var mistyped = await reachable.ReadAsync(
            [new DriverTag(tagId, "not-an-address", TagValueKind.Numeric)], CancellationToken.None);

        var unreachableLog = new RecordingLogger<ModbusTcpDriver>();
        await using var unreachable =
            new ModbusTcpDriver("127.0.0.1", FreePort(), 1, TimeProvider.System, unreachableLog);
        await Assert.ThrowsAnyAsync<Exception>(() => unreachable.ConnectAsync(CancellationToken.None));
        var unconnected = await unreachable.ReadAsync(
            [new DriverTag(tagId, "holding:0", TagValueKind.Numeric)], CancellationToken.None);

        Assert.Equal(Quality.Bad, Assert.Single(mistyped).Quality);
        Assert.Equal(Quality.Bad, Assert.Single(unconnected).Quality);

        var saidOne = Assert.Single(mistypedLog.At(LogLevel.Warning)).Message;
        var saidTheOther = Assert.Single(unreachableLog.At(LogLevel.Warning)).Message;

        Assert.Contains("is not in the form 'area:offset'", saidOne, StringComparison.Ordinal);
        Assert.Contains("the connection has not succeeded yet", saidTheOther, StringComparison.Ordinal);
        Assert.NotEqual(saidOne, saidTheOther);
    }

    [Fact]
    public async Task A_device_that_accepts_the_connection_and_then_says_nothing_is_read_Bad_with_the_runtimes_own_words()
    {
        // A live socket that never answers: the read gives up, and what the runtime said has
        // to reach the log — a tag that is simply unreadable is not a diagnosis.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = silent.AcceptTcpClientAsync();

        var log = new RecordingLogger<ModbusTcpDriver>();
        await using var driver = new ModbusTcpDriver(
            "127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port, 1, TimeProvider.System, log);
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync(
            [new DriverTag(Guid.NewGuid(), "holding:0", TagValueKind.Numeric)], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);

        var warning = Assert.Single(log.At(LogLevel.Warning));
        var failure = Assert.IsAssignableFrom<Exception>(warning.Exception);
        Assert.Contains(failure.Message, warning.Message, StringComparison.Ordinal);

        (await accepted).Dispose();
    }

    [Fact]
    public async Task A_standing_fault_is_named_once_rather_than_once_per_scan()
    {
        await using var slave = ModbusTestSlave.Start();

        var log = new RecordingLogger<ModbusTcpDriver>();
        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System, log);
        await driver.ConnectAsync(CancellationToken.None);

        var broken = new DriverTag(Guid.NewGuid(), "not-an-address", TagValueKind.Numeric);

        // Three scans of the same broken tag: one condition, said once. Repeating the reason
        // every scan would bury it in the lines that say the same thing.
        for (var scan = 0; scan < 3; scan++)
        {
            await driver.ReadAsync([broken], CancellationToken.None);
        }

        Assert.Single(log.At(LogLevel.Warning));
    }

    [Fact]
    public async Task A_tag_that_reads_again_says_so_and_a_fault_that_returns_is_named_again()
    {
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var log = new RecordingLogger<ModbusTcpDriver>();
        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System, log);
        await driver.ConnectAsync(CancellationToken.None);

        // A tag's identity is its id (ADR-0001), so that — not the address — is what the
        // recovery belongs to.
        var tagId = Guid.NewGuid();
        var broken = new DriverTag(tagId, "not-an-address", TagValueKind.Numeric);
        await driver.ReadAsync([broken], CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync(
            [new DriverTag(tagId, "holding:0?scale=0.01", TagValueKind.Numeric)], CancellationToken.None));
        Assert.Equal(Quality.Good, reading.Quality);

        // The memory of the fault is gone rather than permanent silence: the same fault coming
        // back is a new fault, and is named again.
        await driver.ReadAsync([broken], CancellationToken.None);

        var recovery = Assert.Single(log.At(LogLevel.Information));
        Assert.Contains(tagId.ToString(), recovery.Message, StringComparison.Ordinal);
        Assert.Contains("reads Good again", recovery.Message, StringComparison.Ordinal);
        Assert.Equal(2, log.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task A_healthy_scan_writes_nothing()
    {
        // The control for naming a fault: a device that reads fine must not fill the log, or
        // the reasons this step adds would be lost among lines that say nothing happened.
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var log = new RecordingLogger<ModbusTcpDriver>();
        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System, log);
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync(
            [new DriverTag(Guid.NewGuid(), "holding:0?scale=0.01", TagValueKind.Numeric)], CancellationToken.None));

        Assert.Equal(Quality.Good, reading.Quality);
        Assert.Empty(log.Entries);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
