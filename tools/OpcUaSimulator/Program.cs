using ScadaDarbox.Tools.OpcUaSimulator;

// A simulated OPC UA pump, so the Phase 4 driver can be exercised end to end without
// hardware. Not part of the product: it lives under tools/ and nothing in src/ uses it.
//
//   ns=2;s=Pump1.Pressure   discharge pressure, in bar
//   ns=2;s=Pump1.Running    pump running
//
// Unlike the Modbus simulator, values here carry an explicit source timestamp — the
// thing worth exercising, since it is what the protocol supplies and Modbus cannot.

var port = args.Length > 0 && int.TryParse(args[0], out var parsedPort) ? parsedPort : 4840;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

SimulatorServer server;

try
{
    server = await SimulatorServer.StartAsync(port, shutdown.Token);
}
catch (Exception exception)
{
    // Almost always the port already in use, or a certificate store that cannot be
    // written. A stack trace says less than the address and the reason.
    Console.Error.WriteLine(
        $"Could not start the OPC UA simulator on port {port}: {exception.Message}");
    return 1;
}

Console.WriteLine($"OPC UA simulator listening on opc.tcp://localhost:{port}/ScadaDarboxSimulator");
Console.WriteLine("  ns=2;s=Pump1.Pressure   discharge pressure (bar)");
Console.WriteLine("  ns=2;s=Pump1.Running    pump running");
Console.WriteLine("Press Ctrl+C to stop.");

const double MeanBar = 4.2;
const double AmplitudeBar = 0.8;
var startedAt = DateTimeOffset.UtcNow;

try
{
    while (!shutdown.IsCancellationRequested)
    {
        var elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
        var pressureBar = MeanBar + (AmplitudeBar * Math.Sin(elapsedSeconds / 8.0));

        server.Pumps.Publish(pressureBar, pressureBar > MeanBar, DateTime.UtcNow);

        await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown.Token);
    }
}
catch (OperationCanceledException)
{
    // Expected on Ctrl+C.
}
finally
{
    await server.StopAsync(CancellationToken.None);
    server.Dispose();
}

return 0;
