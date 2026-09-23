using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0012's criterion that the migrator is a no-op on a database that is already current.
/// </summary>
/// <remarks>
/// A second run that is not a no-op shows up only on the day someone deploys a second time
/// — which in the Compose topologies is every restart of the stack. It is run here through the
/// migrator's own entry point: the migration, then setting the application role's password,
/// both under the migration lock (ADR-0014).
/// </remarks>
public sealed class MigratorRerunTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public MigratorRerunTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task Running_the_migrator_again_on_a_current_database_changes_nothing()
    {
        // Real data first, so "changes nothing" covers what a rerun could destroy, not only
        // the shape of the schema.
        var tenantId = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Survives a rerun')");

        var before = await SnapshotAsync(tenantId);

        // The snapshot has real content, so identical snapshots below mean something.
        Assert.Equal(DatabaseMigrator.ScriptNames().Count, before.Journal.Count);
        Assert.Contains("app_user|r", before.Objects);
        Assert.Contains(before.Views, view => view.StartsWith("tag_active|", StringComparison.Ordinal));
        Assert.Contains("audit_log|INSERT", before.Grants);
        Assert.Contains(before.RowCounts, row => row.StartsWith("tenant|", StringComparison.Ordinal) && row != "tenant|0");
        Assert.Equal(1, before.SentinelRows);

        var rerun = await Record.ExceptionAsync(() => DatabaseMigrator.RunAsync(
            _database.PrivilegedConnectionString,
            TestDatabase.ApplicationPassword,
            DatabaseMigrator.DefaultLockTimeout,
            CancellationToken.None));
        Assert.Null(rerun);

        var after = await SnapshotAsync(tenantId);

        Assert.Equal(before.Journal, after.Journal);
        Assert.Equal(before.Objects, after.Objects);
        Assert.Equal(before.Views, after.Views);
        Assert.Equal(before.Grants, after.Grants);

        // Every table, not only the one holding the sentinel: a rerun that seeds or appends
        // anything — a default row, a record of having run — is not a no-op either.
        Assert.Equal(before.RowCounts, after.RowCounts);
        Assert.Equal(before.SentinelRows, after.SentinelRows);

        // And the application can still log in with the password it was just given again.
        await using var fresh = NpgsqlDataSource.Create(
            ApplicationRole.ConnectionStringFor(_database.PrivilegedConnectionString, TestDatabase.ApplicationPassword));
        await using var whoAmI = fresh.CreateCommand("SELECT current_user");
        Assert.Equal(ApplicationRole.Name, await whoAmI.ExecuteScalarAsync());
    }

    private async Task<Snapshot> SnapshotAsync(Guid tenantId) => new(
        await ListAsync("SELECT scriptname || '|' || applied::text FROM schemaversions ORDER BY schemaversionsid"),
        await ListAsync(
            """
            SELECT c.relname || '|' || c.relkind::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
            ORDER BY 1
            """),
        await ListAsync("SELECT viewname || '|' || md5(definition) FROM pg_views WHERE schemaname = 'public' ORDER BY 1"),
        await ListAsync(
            """
            SELECT table_name || '|' || privilege_type
            FROM information_schema.role_table_grants
            WHERE grantee = 'scada_app'
            ORDER BY 1
            """),
        await RowCountsAsync(),
        await CountAsync($"SELECT count(*) FROM tenant WHERE id = '{tenantId}'"));

    /// <summary>The number of rows in every base table of the schema, as "table|count".</summary>
    private async Task<IReadOnlyList<string>> RowCountsAsync()
    {
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

        return counts;
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

    private sealed record Snapshot(
        IReadOnlyList<string> Journal,
        IReadOnlyList<string> Objects,
        IReadOnlyList<string> Views,
        IReadOnlyList<string> Grants,
        IReadOnlyList<string> RowCounts,
        long SentinelRows);
}
