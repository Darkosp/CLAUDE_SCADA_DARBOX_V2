using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// A throwaway database migrated by the real migrator, with the application role able to
/// log in — the same two-step preparation a deployment goes through (ADR-0012).
/// </summary>
public sealed class ScratchDatabase : IAsyncDisposable
{
    private static readonly string ServerHost =
        Environment.GetEnvironmentVariable("SCADA_TEST_DB_HOST") ?? "localhost";

    /// <summary>
    /// Where that server listens. Overridable so the suite can reach a container published
    /// somewhere other than 5432 — the only way to run it on a machine where a native PostgreSQL
    /// already holds 5432 (measured 2026-10-01; these projects used to hard-code the port).
    /// </summary>
    private static readonly string ServerPort =
        Environment.GetEnvironmentVariable("SCADA_TEST_DB_PORT") ?? "5432";

    /// <summary>
    /// The application role is shared by the whole server, so every test run gives it the
    /// same password — the one a developer's own Gateway uses, unless overridden.
    /// </summary>
    public static readonly string ApplicationPassword =
        Environment.GetEnvironmentVariable(GatewayApp.AppPasswordVariable) ?? "scada_app";

    private static readonly DatabaseAvailability AvailabilityProbe = new(() =>
    {
        try
        {
            using var source = NpgsqlDataSource.Create(ServerConnectionString);
            using var command = source.CreateCommand("SELECT 1");
            command.ExecuteScalar();
            return (true, (string?)null);
        }
        catch (Exception exception)
        {
            return (false, $"No PostgreSQL/TimescaleDB answered at {ServerHost}:{ServerPort} ({exception.Message.Trim()}).");
        }
    });

    private ScratchDatabase(string name) => Name = name;

    public string Name { get; }

    /// <summary>The migrator's credential. Never handed to the Gateway except to prove it is refused.</summary>
    public string PrivilegedConnectionString => ConnectionStringFor(Name);

    public string ApplicationConnectionString =>
        ApplicationRole.ConnectionStringFor(PrivilegedConnectionString, ApplicationPassword);

    internal static bool IsAvailable => AvailabilityProbe.IsAvailable;

    /// <summary>Why it is not reachable, in a sentence a skip message can carry.</summary>
    internal static string Unavailable => AvailabilityProbe.Why;

    /// <remarks>
    /// <c>Command Timeout=0</c>, for the reason <c>Persistence.Tests</c>' copy of this string
    /// documents at length: this connection carries <c>CREATE</c>/<c>DROP DATABASE</c> and nothing
    /// else, and a client-side ten-second deadline made the client give up while the server was
    /// still working — a failed test that passes on its own. The <c>Timeout=3</c> stays, because
    /// failing fast on an absent server is what the availability probe needs.
    /// </remarks>
    private static string ServerConnectionString =>
        $"Host={ServerHost};Port={ServerPort};Database=postgres;Username=scada;Password=scada;Timeout=3;Command Timeout=0";

    public static async Task<ScratchDatabase> CreateMigratedAsync()
    {
        var database = new ScratchDatabase($"scada_gateway_test_{Guid.NewGuid():N}");

        await using (var server = NpgsqlDataSource.Create(ServerConnectionString))
        await using (var create = server.CreateCommand($"CREATE DATABASE {database.Name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        // The production migrator and the production password step, not a hand-built
        // schema: a test that made its own tables could pass while the shipped ones were wrong.
        await DatabaseMigrator.RunAsync(
            database.PrivilegedConnectionString, ApplicationPassword, DatabaseMigrator.DefaultLockTimeout, CancellationToken.None);

        return database;
    }

    /// <summary>Command-line arguments that point a Gateway at this database as the application role.</summary>
    public string[] ApplicationArgs(params string[] more) =>
        [$"--ConnectionStrings:ScadaDb={ApplicationConnectionString}", "--urls=http://127.0.0.1:0", .. more];

    public async Task ExecutePrivilegedAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(PrivilegedConnectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var drop = server.CreateCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    private static string ConnectionStringFor(string database) =>
        $"Host={ServerHost};Port={ServerPort};Database={database};Username=scada;Password=scada";
}

/// <summary>A fact that reports as skipped, rather than passing, when no database is reachable.</summary>
public sealed class RequiresDatabaseFactAttribute : FactAttribute
{
    public RequiresDatabaseFactAttribute()
    {
        if (!ScratchDatabase.IsAvailable)
        {
            Skip = ScratchDatabase.Unavailable;
        }
    }
}

/// <summary>A theory that reports as skipped, rather than passing, when no database is reachable.</summary>
public sealed class RequiresDatabaseTheoryAttribute : TheoryAttribute
{
    public RequiresDatabaseTheoryAttribute()
    {
        if (!ScratchDatabase.IsAvailable)
        {
            Skip = ScratchDatabase.Unavailable;
        }
    }
}
