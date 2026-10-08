using System.Net;
using System.Net.Sockets;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Tools.OpcUaSimulator;
using Xunit;

namespace ScadaDarbox.Drivers.OpcUa.Tests;

/// <summary>
/// Exercises the driver against a real OPC UA server running in-process — the same shape
/// as the Modbus tests, and the only way to know the protocol handling actually works.
/// </summary>
public sealed class OpcUaDriverTests : IAsyncLifetime
{
    private static readonly Guid PressureTagId = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid RunningTagId = new("55555555-5555-4555-8555-555555555555");

    private static readonly DriverTag PressureTag =
        new(PressureTagId, "ns=2;s=Pump1.Pressure", TagValueKind.Numeric);

    private static readonly DriverTag RunningTag =
        new(RunningTagId, "ns=2;s=Pump1.Running", TagValueKind.Boolean);

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
    public async Task Reads_a_numeric_and_a_boolean_from_a_live_server()
    {
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);

        await using var driver = Driver();
        await driver.ConnectAsync(CancellationToken.None);

        var readings = await driver.ReadAsync([PressureTag, RunningTag], CancellationToken.None);

        var pressure = Assert.Single(readings, r => r.TagId == PressureTagId);
        Assert.Equal(Quality.Good, pressure.Quality);
        Assert.Equal(4.25, Assert.IsType<TagValue.Numeric>(pressure.Value).Value, precision: 9);

        var running = Assert.Single(readings, r => r.TagId == RunningTagId);
        Assert.Equal(Quality.Good, running.Quality);
        Assert.True(Assert.IsType<TagValue.Boolean>(running.Value).Value);
    }

    [Fact]
    public async Task Carries_the_servers_own_source_timestamp_rather_than_the_read_time()
    {
        // The reason this protocol was worth adding: OPC UA states when the value was
        // captured, so ADR-0003's source timestamp is real here instead of standing in
        // for one, as it must in Modbus.
        var capturedAt = DateTime.UtcNow.AddMinutes(-7);
        _server.Pumps.Publish(pressureBar: 3.75, running: false, capturedAt);

        // A clock deliberately far from the truth: if the driver substituted its own
        // time, the assertion below would catch it rather than pass by coincidence.
        var wrongClock = new FixedTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using var driver = Driver(wrongClock);
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Good, reading.Quality);
        Assert.Equal(capturedAt, reading.SourceTimestampUtc.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.NotEqual(wrongClock.GetUtcNow(), reading.SourceTimestampUtc);
    }

    [Fact]
    public async Task A_bad_status_from_the_server_yields_bad_quality_and_no_value()
    {
        // The server answers, but says the value is not trustworthy. Reporting the last
        // number instead would be indistinguishable from a real reading.
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);
        _server.Pumps.PublishBadPressure();

        await using var driver = Driver();
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);
        Assert.Null(reading.Value);
    }

    [Fact]
    public async Task A_malformed_address_is_bad_without_stopping_the_rest_of_the_scan()
    {
        _server.Pumps.Publish(pressureBar: 4.25, running: true, DateTime.UtcNow);

        await using var driver = Driver();
        await driver.ConnectAsync(CancellationToken.None);

        var broken = new DriverTag(Guid.NewGuid(), "not a node id", TagValueKind.Numeric);
        var readings = await driver.ReadAsync([broken, PressureTag], CancellationToken.None);

        Assert.Equal(Quality.Bad, Assert.Single(readings, r => r.TagId == broken.TagId).Quality);
        Assert.Equal(Quality.Good, Assert.Single(readings, r => r.TagId == PressureTagId).Quality);
    }

    [Fact]
    public async Task An_unreachable_server_yields_bad_quality_with_no_value()
    {
        await using var driver = new OpcUaDriver(
            $"opc.tcp://localhost:{FreePort()}/Nothing",
            acceptUntrustedCertificates: true,
            OpcUaSecurity.Required,
            TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync(CancellationToken.None));

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);
        Assert.Null(reading.Value);
    }

    [Fact]
    public async Task Writes_a_value_back_to_the_server()
    {
        _server.Pumps.Publish(pressureBar: 1.0, running: false, DateTime.UtcNow);

        await using var driver = Driver();
        await driver.ConnectAsync(CancellationToken.None);

        await driver.WriteAsync(PressureTag, new TagValue.Numeric(6.5), CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));
        Assert.Equal(6.5, Assert.IsType<TagValue.Numeric>(reading.Value).Value, precision: 9);
    }

    private OpcUaDriver Driver(TimeProvider? clock = null) => new(
        $"opc.tcp://localhost:{_port}/ScadaDarboxSimulator",
        acceptUntrustedCertificates: true,
        OpcUaSecurity.Required,
        clock ?? TimeProvider.System);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
