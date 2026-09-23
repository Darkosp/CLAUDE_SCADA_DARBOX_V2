using System.Diagnostics;
using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0014: one migrator at a time on a database, enforced by the database.
/// </summary>
/// <remarks>
/// DbUp reads its journal and then executes, with no lock of its own. Measured before this
/// lock existed: three runs meeting on an existing database each applied a new data script —
/// three rows, three journal entries, every run reporting success — and on an empty database
/// all but one run failed on the catalogue. Every run here goes through the migrator's own
/// entry point, as separate concurrent callers each with their own connections, which is what
/// separate processes look like to the database.
/// </remarks>
public sealed class MigrationLockTests : IClassFixture<TestDatabase>
{
    // Long enough that every run has read the journal before any has committed: without the
    // lock, this is what makes the race certain rather than likely.
    private const string SlowDataScript = "SELECT pg_sleep(1); INSERT INTO {0} (n) VALUES (1)";

    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(2);

    private readonly TestDatabase _database;

    public MigrationLockTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task Three_migrators_meeting_on_an_existing_database_apply_a_new_data_script_once()
    {
        const string table = "lock_probe_once";
        const string scriptName = "zz_test_0001_data_script_applied_once";
        await ExecuteAsync($"CREATE TABLE {table} (n int)");

        var outcomes = await RunTogetherAsync(3, () => DatabaseMigrator.RunAsync(
            _database.PrivilegedConnectionString,
            TestDatabase.ApplicationPassword,
            Generous,
            [(scriptName, string.Format(SlowDataScript, table))],
            CancellationToken.None));

        // Applied once: one row, one journal entry. Checked together so a failure shows both.
        // Without the lock every run applies it and every run succeeds, so this is what fails —
        // not the outcome check below.
        var applied = (
            Rows: await CountAsync($"SELECT count(*) FROM {table}"),
            JournalEntries: await CountAsync($"SELECT count(*) FROM schemaversions WHERE scriptname = '{scriptName}'"));
        Assert.Equal((Rows: 1L, JournalEntries: 1L), applied);

        // One run applied it and the others found it done; none of them failed.
        Assert.All(outcomes, Assert.Null);
    }

    [RequiresDatabaseFact]
    public async Task Migrators_meeting_on_an_empty_database_all_succeed()
    {
        var name = await TestDatabase.CreateEmptyAsync();
        try
        {
            var connectionString = TestDatabase.ConnectionStringFor(name);

            var outcomes = await RunTogetherAsync(4, () => DatabaseMigrator.RunAsync(
                connectionString, TestDatabase.ApplicationPassword, Generous, CancellationToken.None));

            // Without the lock, all but one of these fail on the catalogue while creating the
            // first script's tables.
            Assert.All(outcomes, Assert.Null);

            // And the four together left exactly what one run leaves.
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var journal = dataSource.CreateCommand("SELECT count(*), count(DISTINCT scriptname) FROM schemaversions");
            await using var reader = await journal.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.Equal(DatabaseMigrator.ScriptNames().Count, reader.GetInt64(0));
            Assert.Equal(DatabaseMigrator.ScriptNames().Count, reader.GetInt64(1));
        }
        finally
        {
            await TestDatabase.DropEmptyAsync(name);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_migrator_is_not_held_up_by_a_run_on_a_different_database()
    {
        // The parallel test fixtures each migrate a database of their own at the same time;
        // they depend on this.
        await using var holder = await HoldLockAsync(_database.PrivilegedConnectionString);

        var name = await TestDatabase.CreateEmptyAsync();
        try
        {
            var run = DatabaseMigrator.RunAsync(
                TestDatabase.ConnectionStringFor(name),
                TestDatabase.ApplicationPassword,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            var failure = await Record.ExceptionAsync(() => run);
            Assert.Null(failure);
        }
        finally
        {
            await TestDatabase.DropEmptyAsync(name);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_migrator_that_cannot_take_the_lock_gives_up_in_time_names_the_holder_and_applies_nothing()
    {
        const string table = "lock_probe_timeout";
        const string scriptName = "zz_test_0002_data_script_never_applied";
        await ExecuteAsync($"CREATE TABLE {table} (n int)");

        await using var holder = await HoldLockAsync(_database.PrivilegedConnectionString);
        var timeout = TimeSpan.FromSeconds(2);
        var before = await StateAsync();

        var clock = Stopwatch.StartNew();
        var run = DatabaseMigrator.RunAsync(
            _database.PrivilegedConnectionString,
            TestDatabase.ApplicationPassword,
            timeout,
            [(scriptName, $"INSERT INTO {table} (n) VALUES (1)")],
            CancellationToken.None);

        // A run that waited forever would hang the suite rather than fail it.
        var outcome = await Record.ExceptionAsync(() => run.WaitAsync(Generous));
        clock.Stop();

        // First, and before anything about the exception: the run that waited applied nothing.
        // The journal is entry for entry what it was, and no table gained or lost a row. A run
        // that went ahead after its wait fails here, whatever it then reports.
        var after = await StateAsync();
        Assert.Equal(before.Journal, after.Journal);
        Assert.Equal(before.RowCounts, after.RowCounts);

        // It gave up for the right reason, having waited for the lock, and not much longer
        // than it was told to.
        var refusal = Assert.IsType<MigrationLockTimeoutException>(outcome);
        Assert.InRange(clock.Elapsed, timeout - TimeSpan.FromMilliseconds(100), timeout + TimeSpan.FromSeconds(15));

        // The message says why, and who is in the way.
        Assert.Contains("Another migrator holds database", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"process {holder.ProcessId} (", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationLock.ApplicationName, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts every run at once and waits for all of them; null where a run succeeded.</summary>
    private static async Task<IReadOnlyList<Exception?>> RunTogetherAsync(int count, Func<Task> run)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each on a thread of its own: DbUp's upgrade is synchronous, and runs sharing one
        // thread would take turns instead of meeting.
        var runs = Enumerable.Range(0, count)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await Record.ExceptionAsync(run);
            }))
            .ToList();

        start.SetResult();
        return await Task.WhenAll(runs).WaitAsync(Generous);
    }

    /// <summary>
    /// Another migrator, holding the lock on <paramref name="connectionString"/>'s database —
    /// taken by the production code, so a change to where the lock is taken applies to both sides.
    /// </summary>
    private static Task<MigrationLock> HoldLockAsync(string connectionString) =>
        MigrationLock.AcquireAsync(connectionString, Generous, CancellationToken.None);

    /// <summary>The whole journal, and the row count of every table: what "applied nothing" is checked against.</summary>
    private async Task<(IReadOnlyList<string> Journal, IReadOnlyList<string> RowCounts)> StateAsync()
    {
        var journal = await ListAsync("SELECT scriptname || '|' || applied::text FROM schemaversions ORDER BY schemaversionsid");
        var tables = await ListAsync(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY 1
            """);

        var counts = new List<string>(tables.Count);
        foreach (var table in tables)
        {
            counts.Add($"{table}|{await CountAsync($"SELECT count(*) FROM \"{table}\"")}");
        }

        return (journal, counts);
    }

    private async Task<IReadOnlyList<string>> ListAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
