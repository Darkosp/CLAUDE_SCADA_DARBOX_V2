using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// A Modbus TCP slave listening on a free loopback port for the life of one test (ADR-0023).
/// </summary>
/// <remarks>
/// A real one rather than a stub, for the reason the Modbus driver's own tests give: what a write
/// does to a device that is actually listening is the only thing worth asserting, and a stub that
/// records "WriteAsync was called" would pass just as happily if the address were wrong, the scale
/// were dropped, or the value were never encoded.
/// </remarks>
internal sealed class ModbusTestSlave : IAsyncDisposable
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
