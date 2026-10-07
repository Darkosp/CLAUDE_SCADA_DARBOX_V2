using ScadaDarbox.Gateway;
using ScadaDarbox.Gateway.Security;
using ScadaDarbox.Persistence.TimescaleDb;

WebApplication app;

try
{
    app = await GatewayApp.BuildAsync(args);
}
catch (Exception exception) when (exception is SchemaVersionMismatchException or UnsafeDatabaseRoleException or NoMigrationScriptsException or DatabaseLoginRefusedException or InsecureTransportException)
{
    // A refusal, not a crash (ADR-0012): the message says exactly what is wrong and what to
    // run, and a stack trace would only bury it.
    Console.Error.WriteLine(exception.Message);
    return 1;
}

await app.RunAsync();
return 0;
