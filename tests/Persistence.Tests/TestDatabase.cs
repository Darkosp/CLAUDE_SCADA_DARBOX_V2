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
    private const string ServerConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=scada;Password=scada;Timeout=3;Command Timeout=10";

    private readonly string _databaseName = $"scada_test_{Guid.NewGuid():N}";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

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

        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();

        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var drop = server.CreateCommand(
            $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    private static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5432;Database={database};Username=scada;Password=scada";

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
            Skip = "No PostgreSQL/TimescaleDB on localhost:5432 — start it with 'docker compose up -d'.";
        }
    }
}
