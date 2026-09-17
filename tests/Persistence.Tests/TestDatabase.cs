using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// A throwaway database, migrated from empty by the real migrator, for tests that have
/// to prove behaviour the database itself enforces.
/// </summary>
/// <remarks>
/// These tests need a live PostgreSQL/TimescaleDB — the constraints under test are not
/// something an in-memory fake can demonstrate, since the whole point is that the
/// database refuses the write. Without one reachable they report as skipped rather than
/// passing silently.
/// </remarks>
public sealed class TestDatabase : IAsyncLifetime
{
    /// <summary>
    /// Where to reach the server. Overridable so a CI environment can point these tests
    /// at its own database — and so the skip path below can be exercised on demand by
    /// pointing them somewhere that does not answer.
    /// </summary>
    private static readonly string ServerHost =
        Environment.GetEnvironmentVariable("SCADA_TEST_DB_HOST") ?? "localhost";

    private static string ServerConnectionString =>
        $"Host={ServerHost};Port=5432;Database=postgres;Username=scada;Password=scada;Timeout=3;Command Timeout=10";

    /// <summary>
    /// The application role belongs to the whole server, so every run gives it the same
    /// password — the one a developer's own Gateway uses, unless overridden.
    /// </summary>
    public static readonly string ApplicationPassword =
        Environment.GetEnvironmentVariable("SCADA_APP_DB_PASSWORD") ?? "scada_app";

    private readonly string _databaseName = $"scada_test_{Guid.NewGuid():N}";

    /// <summary>The migrator's credential for this database, for tests that run the migrator itself.</summary>
    public string PrivilegedConnectionString => ConnectionStringFor(_databaseName);

    /// <summary>
    /// Connected as the migrator's privileged role. Right for testing what the schema itself
    /// enforces; wrong for anything about grants, which a superuser simply bypasses.
    /// </summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>
    /// Connected as the application role the Gateway runs as (ADR-0011). Any test about what
    /// the application is <em>not allowed</em> to do must use this one.
    /// </summary>
    public NpgsqlDataSource ApplicationDataSource { get; private set; } = null!;

    /// <summary>
    /// Whether a server is reachable. Probed once so a machine without Docker running
    /// spends three seconds in total, not three per test.
    /// </summary>
    internal static bool IsAvailable => AvailabilityProbe.Value;

    public async Task InitializeAsync()
    {
        await using (var server = NpgsqlDataSource.Create(ServerConnectionString))
        await using (var create = server.CreateCommand($"CREATE DATABASE {_databaseName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = ConnectionStringFor(_databaseName);

        // Deliberately the production migrator, not a hand-written schema: a test that
        // built its own tables could pass while the shipped migration was broken.
        DatabaseMigrator.Migrate(connectionString);
        await ApplicationRole.SetPasswordAsync(connectionString, ApplicationPassword, CancellationToken.None);

        DataSource = NpgsqlDataSource.Create(connectionString);
        ApplicationDataSource = NpgsqlDataSource.Create(
            ApplicationRole.ConnectionStringFor(connectionString, ApplicationPassword));
    }

    public async Task DisposeAsync()
    {
        await ApplicationDataSource.DisposeAsync();
        await DataSource.DisposeAsync();

        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var drop = server.CreateCommand(
            $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    private static string ConnectionStringFor(string database) =>
        $"Host={ServerHost};Port=5432;Database={database};Username=scada;Password=scada";

    private static readonly Lazy<bool> AvailabilityProbe = new(() =>
    {
        try
        {
            using var source = NpgsqlDataSource.Create(ServerConnectionString);
            using var command = source.CreateCommand("SELECT 1");
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });
}

/// <summary>
/// A fact that reports as skipped, rather than passing, when no database is reachable.
/// </summary>
public sealed class RequiresDatabaseFactAttribute : FactAttribute
{
    public RequiresDatabaseFactAttribute()
    {
        if (!TestDatabase.IsAvailable)
        {
            Skip = "No PostgreSQL/TimescaleDB reachable — start it with 'docker compose up -d'.";
        }
    }
}
