using ScadaDarbox.Tools.OpcUaSimulator;

// A simulated OPC UA pump, so the Phase 4 driver can be exercised end to end without
// hardware. Not part of the product: it lives under tools/ and nothing in src/ uses it.
//
//   ns=2;s=Pump1.Pressure   discharge pressure, in bar
//   ns=2;s=Pump1.Running    pump running
//
// Unlike the Modbus simulator, values here carry an explicit source timestamp — the
// thing worth exercising, since it is what the protocol supplies and Modbus cannot.

//
// Usage: [port] [--host <name>]. The host is the name in the server's endpoint URL, which is
// what a client is told to connect back to — so it has to be a name the client can resolve.
// "localhost" unless told otherwise; in a container, Compose passes the service's own name
// (Phase 6).

var host = "localhost";
var port = 4840;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--host" && i + 1 < args.Length && Uri.CheckHostName(args[i + 1]) != UriHostNameType.Unknown)
    {
        host = args[i + 1];
        i++;
    }
    else if (int.TryParse(args[i], out var parsedPort))
    {
        port = parsedPort;
    }
    else
    {
        Console.Error.WriteLine($"Unrecognised argument '{args[i]}'. Usage: [port] [--host <name>]");
        return 2;
    }
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

SimulatorServer server;

try
{
    server = await SimulatorServer.StartAsync(host, port, shutdown.Token);
}
catch (Exception exception)
{
    // Almost always the port already in use, or a certificate store that cannot be
    // written. A stack trace says less than the address and the reason.
    Console.Error.WriteLine(
        $"Could not start the OPC UA simulator on port {port}: {exception.Message}");
    return 1;
}

Console.WriteLine($"OPC UA simulator listening on opc.tcp://{host}:{port}/ScadaDarboxSimulator");
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
