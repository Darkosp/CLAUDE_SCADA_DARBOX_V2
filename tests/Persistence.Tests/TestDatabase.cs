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

    /// <summary>
    /// Where that server listens. Overridable so the suite can reach a container published
    /// somewhere other than 5432 — the only way to run it on a machine where a native PostgreSQL
    /// already holds 5432 (measured 2026-10-01; these projects used to hard-code the port).
    /// </summary>
    private static readonly string ServerPort =
        Environment.GetEnvironmentVariable("SCADA_TEST_DB_PORT") ?? "5432";

    /// <remarks>
    /// <c>Command Timeout=0</c>: no client-side deadline on <c>CREATE</c>/<c>DROP DATABASE</c>.
    /// This connection carries DDL and nothing else, and creating or dropping a database is slow
    /// on a loaded server — seven assemblies at once, several of them doing the same thing, each
    /// migration running hundreds of statements. With the ten seconds this string used to carry,
    /// the <em>client</em> gave up while the server was still working, which surfaced as
    /// <c>NpgsqlException … TimeoutException: Timeout during reading attempt</c> inside
    /// <see cref="DropEmptyAsync"/> and as a failed test that passes on its own. Measured
    /// 2026-10-02: five of eight whole-solution runs, never once in isolation.
    ///
    /// <c>Timeout=30</c> is the <em>connect</em> deadline, and it was three until 2026-10-05. The
    /// note that used to sit here — "connecting fast and failing fast is still right" — was the
    /// mistake, and it cost a flake that took a hunt to name:
    /// <c>NpgsqlException: The operation has timed out</c> inside
    /// <c>NpgsqlConnector.ConnectAsync</c>, on a loaded machine, in a test about something else
    /// entirely. Thirty test classes each create their own database here, in parallel, and three
    /// seconds is not enough for a connection when six other assemblies are doing the same. What
    /// "failing fast" was protecting is the availability probe below, and that probe does not
    /// depend on this number to be quick: it answers on the first successful connect and only
    /// <em>asks again</em> on a failure, so the cost of a longer deadline is paid once per run and
    /// only when there is no server to find.
    /// </remarks>
    private static string ServerConnectionString =>
        $"Host={ServerHost};Port={ServerPort};Database=postgres;Username=scada;Password=scada;Timeout=30;Command Timeout=0";

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
    /// Whether a server is reachable. A success is asked once, so a machine with a database
    /// spends three seconds in total rather than three per test; a failure is asked again,
    /// because a server that is still starting is not a server that is absent.
    /// </summary>
    internal static bool IsAvailable => AvailabilityProbe.IsAvailable;

    /// <summary>Why it is not reachable, in a sentence a skip message can carry.</summary>
    internal static string Unavailable => AvailabilityProbe.Why;

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
        await DatabaseMigrator.RunAsync(
            connectionString, ApplicationPassword, DatabaseMigrator.DefaultLockTimeout, CancellationToken.None);

        DataSource = NpgsqlDataSource.Create(connectionString);
        ApplicationDataSource = NpgsqlDataSource.Create(
            ApplicationRole.ConnectionStringFor(connectionString, ApplicationPassword));
    }

    public async Task DisposeAsync()
    {
        // xUnit disposes a class fixture even when every test in the class skipped, so on a
        // machine with no database reachable this runs with both data sources still null:
        // the skip path never reached InitializeAsync. Nothing was created, so nothing is
        // owed — and returning here keeps a skipped, otherwise green run from being reported
        // as a failed one, once per class that takes this fixture.
        if (DataSource is null)
        {
            return;
        }

        await ApplicationDataSource.DisposeAsync();
        await DataSource.DisposeAsync();

        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var drop = server.CreateCommand(
            $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A database with nothing in it, not even the migration journal — for tests about what the
    /// migrator does to one. The caller drops it with <see cref="DropEmptyAsync"/>.
    /// </summary>
    internal static async Task<string> CreateEmptyAsync()
    {
        var name = $"scada_test_{Guid.NewGuid():N}";
        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var create = server.CreateCommand($"CREATE DATABASE {name}");
        await create.ExecuteNonQueryAsync();
        return name;
    }

    internal static async Task DropEmptyAsync(string name)
    {
        await using var server = NpgsqlDataSource.Create(ServerConnectionString);
        await using var drop = server.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A connection string for one database, as the privileged role.
    /// </summary>
    /// <remarks>
    /// <c>Timeout=30</c> for the reason <see cref="ServerConnectionString"/> gives at length: thirty
    /// test classes create and migrate databases in parallel here, and Npgsql's default of fifteen
    /// seconds to <em>connect</em> is not always enough on a loaded machine. This string used to
    /// carry no deadline of its own, so everything built from it — the privileged role, and the
    /// application role derived from it by <c>ApplicationRole.ConnectionStringFor</c> — inherited a
    /// default that was measured failing on 2026-10-05, as
    /// <c>NpgsqlException: The operation has timed out</c> in <c>ConnectAsync</c> inside
    /// <c>MigrationLockTests</c>.
    ///
    /// It is deliberately the same number in both test projects, so that a flake cannot be a
    /// difference between two helpers that look alike.
    /// </remarks>
    internal static string ConnectionStringFor(string database) =>
        $"Host={ServerHost};Port={ServerPort};Database={database};Username=scada;Password=scada;Timeout=30";

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
            Skip = TestDatabase.Unavailable;
        }
    }
}
