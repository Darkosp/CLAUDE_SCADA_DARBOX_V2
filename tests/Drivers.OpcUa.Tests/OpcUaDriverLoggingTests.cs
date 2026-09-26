using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Tools.OpcUaSimulator;
using Xunit;

namespace ScadaDarbox.Drivers.OpcUa.Tests;

/// <summary>
/// What the driver says when a tag has no value. The readings themselves are checked next
/// door; what is checked here is the part a Bad reading cannot carry — why — because the same
/// Bad reading used to be all there was to see of a mistyped address and an unplugged device.
/// </summary>
public sealed class OpcUaDriverLoggingTests : IAsyncLifetime
{
    private static readonly Guid PressureTagId = new("44444444-4444-4444-8444-444444444444");

    private static readonly DriverTag PressureTag =
        new(PressureTagId, "ns=2;s=Pump1.Pressure", TagValueKind.Numeric);

    private SimulatorServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FreePort();
        _server = await SimulatorServer.StartAsync(_port, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
    }

    [Fact]
    public async Task A_mistyped_address_reads_Bad_with_the_parsers_own_words_in_the_log()
    {
        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        var broken = new DriverTag(Guid.NewGuid(), "not a node id", TagValueKind.Numeric);
        var reading = Assert.Single(await driver.ReadAsync([broken], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);
        Assert.Null(reading.Value);

        var warning = Assert.Single(log.At(LogLevel.Warning));
        Assert.Contains(broken.TagId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains("not a node id", warning.Message, StringComparison.Ordinal);
        Assert.Contains("is not an OPC UA node id", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"opc.tcp://localhost:{_port}/ScadaDarboxSimulator", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_the_server_does_not_have_reads_Bad_with_the_servers_own_status_in_the_log()
    {
        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        var missing = new DriverTag(Guid.NewGuid(), "ns=2;s=Pump1.NoSuchTag", TagValueKind.Numeric);
        var reading = Assert.Single(await driver.ReadAsync([missing], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);

        var warning = Assert.Single(log.At(LogLevel.Warning));
        Assert.Contains("BadNodeIdUnknown", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ns=2;s=Pump1.NoSuchTag", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mistyped_address_and_an_unplugged_device_no_longer_read_the_same()
    {
        // The complaint this step answers: a mistyped address and an unplugged device both
        // read Bad, with no reason anywhere, so the two were indistinguishable on screen.
        var mistypedLog = new RecordingLogger<OpcUaDriver>();
        await using var connected = Driver(mistypedLog);
        await connected.ConnectAsync(CancellationToken.None);
        await connected.ReadAsync(
            [new DriverTag(Guid.NewGuid(), "ns=2;s=Pump1.NoSuchTag", TagValueKind.Numeric)],
            CancellationToken.None);

        var unpluggedLog = new RecordingLogger<OpcUaDriver>();
        await using var unplugged = new OpcUaDriver(
            $"opc.tcp://localhost:{FreePort()}/ScadaDarboxSimulator",
            acceptUntrustedCertificates: true,
            TimeProvider.System,
            unpluggedLog);
        await Assert.ThrowsAnyAsync<Exception>(() => unplugged.ConnectAsync(CancellationToken.None));
        var reading = Assert.Single(await unplugged.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);

        var saidOne = Assert.Single(mistypedLog.At(LogLevel.Warning)).Message;
        var saidTheOther = Assert.Single(unpluggedLog.At(LogLevel.Warning)).Message;

        Assert.Contains("BadNodeIdUnknown", saidOne, StringComparison.Ordinal);
        Assert.Contains("the session is not connected", saidTheOther, StringComparison.Ordinal);
        Assert.NotEqual(saidOne, saidTheOther);
    }

    [Fact]
    public async Task A_value_a_tag_cannot_hold_reads_Bad_and_the_log_names_the_shape_that_arrived()
    {
        // The server is fine and answers; the tag is misconfigured. Naming the type that came
        // back is what turns an unexplained Bad into a one-line fix.
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        var wrongKind = new DriverTag(Guid.NewGuid(), "ns=2;s=Pump1.Running", TagValueKind.Numeric);
        var reading = Assert.Single(await driver.ReadAsync([wrongKind], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);

        var warning = Assert.Single(log.At(LogLevel.Warning));
        Assert.Contains("Boolean", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Numeric", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task A_standing_fault_is_named_once_rather_than_once_per_scan()
    {
        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        var missing = new DriverTag(Guid.NewGuid(), "ns=2;s=Pump1.NoSuchTag", TagValueKind.Numeric);

        for (var scan = 0; scan < 3; scan++)
        {
            await driver.ReadAsync([missing], CancellationToken.None);
        }

        Assert.Single(log.At(LogLevel.Warning));
    }

    [Fact]
    public async Task A_tag_that_reads_again_says_so_and_a_fault_that_returns_is_named_again()
    {
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        // A tag's identity is its id (ADR-0001), so that — not the node id — is what the
        // recovery belongs to.
        var tagId = Guid.NewGuid();
        var absent = new DriverTag(tagId, "ns=2;s=Pump1.NoSuchTag", TagValueKind.Numeric);

        await driver.ReadAsync([absent], CancellationToken.None);
        var reading = Assert.Single(await driver.ReadAsync(
            [new DriverTag(tagId, "ns=2;s=Pump1.Pressure", TagValueKind.Numeric)], CancellationToken.None));
        Assert.Equal(Quality.Good, reading.Quality);

        await driver.ReadAsync([absent], CancellationToken.None);

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
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = Driver(log);
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Good, reading.Quality);
        Assert.Empty(log.Entries);
    }

    private OpcUaDriver Driver(ILogger<OpcUaDriver> logger) => new(
        $"opc.tcp://localhost:{_port}/ScadaDarboxSimulator",
        acceptUntrustedCertificates: true,
        TimeProvider.System,
        logger);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
