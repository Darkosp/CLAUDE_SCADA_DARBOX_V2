using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Modbus;

namespace ScadaDarbox.Drivers.Modbus.Tests;

/// <summary>
/// Exercises the driver against a real Modbus TCP slave running in-process — the same
/// shape as the simulator the Phase 1 test gate uses, without needing a database.
/// </summary>
public class ModbusTcpDriverTests
{
    private static readonly Guid PressureTagId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid RunningTagId = new("33333333-3333-4333-8333-333333333333");

    private static readonly DriverTag PressureTag =
        new(PressureTagId, "holding:0?scale=0.01", TagValueKind.Numeric);

    private static readonly DriverTag RunningTag =
        new(RunningTagId, "coil:0", TagValueKind.Boolean);

    [Fact]
    public async Task Reads_a_scaled_register_and_a_coil_from_a_live_slave()
    {
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);
        slave.DataStore.CoilDiscretes.WritePoints(0, [true]);

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System);
        await driver.ConnectAsync(CancellationToken.None);

        var readings = await driver.ReadAsync([PressureTag, RunningTag], CancellationToken.None);

        var pressure = Assert.Single(readings, r => r.TagId == PressureTagId);
        Assert.Equal(Quality.Good, pressure.Quality);
        Assert.Equal(4.2, Assert.IsType<TagValue.Numeric>(pressure.Value).Value, precision: 9);

        var running = Assert.Single(readings, r => r.TagId == RunningTagId);
        Assert.Equal(Quality.Good, running.Quality);
        Assert.True(Assert.IsType<TagValue.Boolean>(running.Value).Value);
    }

    [Fact]
    public async Task Reports_bad_quality_when_the_device_is_unreachable()
    {
        // The distinction ADR-0003 exists for: an offline device must not look like a
        // device reporting zero.
        await using var driver = new ModbusTcpDriver("127.0.0.1", FreePort(), 1, TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(
            () => driver.ConnectAsync(CancellationToken.None));

        var readings = await driver.ReadAsync([PressureTag], CancellationToken.None);

        Assert.Equal(Quality.Bad, Assert.Single(readings).Quality);
    }

    [Fact]
    public async Task Reports_bad_quality_for_a_malformed_address_without_failing_the_whole_scan()
    {
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System);
        await driver.ConnectAsync(CancellationToken.None);

        var broken = new DriverTag(Guid.NewGuid(), "not-an-address", TagValueKind.Numeric);
        var readings = await driver.ReadAsync([broken, PressureTag], CancellationToken.None);

        Assert.Equal(Quality.Bad, Assert.Single(readings, r => r.TagId == broken.TagId).Quality);
        Assert.Equal(Quality.Good, Assert.Single(readings, r => r.TagId == PressureTagId).Quality);
    }

    [Fact]
    public async Task Writes_a_scaled_value_back_to_the_device()
    {
        await using var slave = ModbusTestSlave.Start();

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, TimeProvider.System);
        await driver.ConnectAsync(CancellationToken.None);

        await driver.WriteAsync(PressureTag, new TagValue.Numeric(5.5), CancellationToken.None);

        Assert.Equal(550, slave.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);
    }

    [Fact]
    public async Task Stamps_a_source_timestamp_from_the_driver_clock()
    {
        // Modbus carries no device timestamp, so the driver supplies the read time — but it
        // must supply one, not leave it to whatever writes the value later (ADR-0003).
        await using var slave = ModbusTestSlave.Start();
        slave.DataStore.HoldingRegisters.WritePoints(0, [420]);

        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 9, 10, 30, 0, TimeSpan.Zero));
        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port, 1, clock);
        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(clock.GetUtcNow(), reading.SourceTimestampUtc);
    }

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

    /// <summary>A Modbus TCP slave listening on a free loopback port for the life of one test.</summary>
    private sealed class ModbusTestSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _listening;

        private ModbusTestSlave(TcpListener listener, IModbusSlaveNetwork network, SlaveDataStore dataStore)
        {
            _listener = listener;
            DataStore = dataStore;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listening = network.ListenAsync(_shutdown.Token);
        }

        internal SlaveDataStore DataStore { get; }

        internal int Port { get; }

        internal static ModbusTestSlave Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var dataStore = new SlaveDataStore();
            var factory = new ModbusFactory();
            var network = factory.CreateSlaveNetwork(listener);
            network.AddSlave(factory.CreateSlave(1, dataStore));

            return new ModbusTestSlave(listener, network, dataStore);
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();

            try
            {
                await _listening;
            }
            catch (Exception)
            {
                // The listen loop faults when its listener is torn down; that is the shutdown.
            }

            _shutdown.Dispose();
        }
    }
}
