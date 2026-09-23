using ScadaDarbox.Persistence.TimescaleDb;

// The one-shot migration step that runs before the Gateway starts (ADR-0012).
//
// Both inputs come from the environment and nowhere else. A connection string on the
// command line is visible to every process on the machine, and this one carries the
// privileged credential — the one the Gateway must never hold.

const string ConnectionVariable = "SCADA_MIGRATOR_CONNECTION";
const string AppPasswordVariable = "SCADA_APP_DB_PASSWORD";
const string LockTimeoutVariable = "SCADA_MIGRATOR_LOCK_TIMEOUT_SECONDS";

var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
var appPassword = Environment.GetEnvironmentVariable(AppPasswordVariable);

if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrEmpty(appPassword))
{
    Console.Error.WriteLine(
        $"""
        The migrator needs two environment variables:
          {ConnectionVariable}  connection string for a role allowed to change the schema
          {AppPasswordVariable}  password to give the application role '{ApplicationRole.Name}'
        """);
    return 2;
}

// How long to wait for another migrator run to finish (ADR-0014). Optional; a value that is
// there but unusable is refused rather than quietly replaced by the default.
var lockTimeout = DatabaseMigrator.DefaultLockTimeout;
var lockTimeoutSetting = Environment.GetEnvironmentVariable(LockTimeoutVariable);
if (!string.IsNullOrWhiteSpace(lockTimeoutSetting))
{
    if (!int.TryParse(lockTimeoutSetting, out var seconds) || seconds <= 0)
    {
        Console.Error.WriteLine($"{LockTimeoutVariable} must be a whole number of seconds greater than zero.");
        return 2;
    }

    lockTimeout = TimeSpan.FromSeconds(seconds);
}

try
{
    await DatabaseMigrator.RunAsync(connectionString, appPassword, lockTimeout, CancellationToken.None);
}
catch (Exception exception)
{
    // Only the message: an exception from a failed connection can quote its connection
    // string, and this one holds the privileged password.
    Console.Error.WriteLine($"Migration failed: {exception.Message}");
    if (exception.InnerException is { } inner)
    {
        Console.Error.WriteLine($"  {inner.Message}");
    }

    return 1;
}

Console.WriteLine(
    $"Schema is at {DatabaseMigrator.ScriptNames().Count} migrations; application role '{ApplicationRole.Name}' can log in.");
return 0;
