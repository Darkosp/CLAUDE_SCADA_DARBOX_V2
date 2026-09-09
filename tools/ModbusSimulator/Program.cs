using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

// A minimal Modbus TCP device, so the Phase 1 read path can be exercised end-to-end
// without physical hardware. Not part of the product: it lives under tools/ and nothing
// in src/ references it.
//
//   holding register 0 — discharge pressure, in hundredths of a bar
//   coil 0            — pump running
//
// The register is scaled because a Modbus register is a raw 16-bit integer; the tag's
// source address (holding:0?scale=0.01) is what gives it engineering meaning.

var port = args.Length > 0 && int.TryParse(args[0], out var parsedPort) ? parsedPort : 5502;
const byte UnitId = 1;

var dataStore = new SlaveDataStore();
var factory = new ModbusFactory();

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();

var network = factory.CreateSlaveNetwork(listener);
network.AddSlave(factory.CreateSlave(UnitId, dataStore));

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine($"Modbus TCP simulator listening on 127.0.0.1:{port}, unit {UnitId}.");
Console.WriteLine("  holding:0  discharge pressure (hundredths of a bar)");
Console.WriteLine("  coil:0     pump running");
Console.WriteLine("Press Ctrl+C to stop.");

var simulation = SimulateAsync(dataStore, shutdown.Token);

try
{
    await network.ListenAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    // Expected on Ctrl+C.
}
finally
{
    shutdown.Cancel();
    await simulation;
    listener.Stop();
}

return 0;

static async Task SimulateAsync(SlaveDataStore dataStore, CancellationToken cancellationToken)
{
    // A slow sine around 4.2 bar, so a browser watching one tag sees it move continuously
    // rather than flipping between two values.
    const double MeanBar = 4.2;
    const double AmplitudeBar = 0.8;

    var startedAt = DateTimeOffset.UtcNow;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
            var pressureBar = MeanBar + (AmplitudeBar * Math.Sin(elapsedSeconds / 8.0));
            var running = pressureBar > MeanBar;

            dataStore.HoldingRegisters.WritePoints(0, [(ushort)Math.Round(pressureBar * 100.0)]);
            dataStore.CoilDiscretes.WritePoints(0, [running]);

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Expected on shutdown.
    }
}
