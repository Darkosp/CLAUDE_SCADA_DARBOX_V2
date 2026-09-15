using Npgsql;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0011's append-only criterion, executed over the application's own connection.
/// </summary>
/// <remarks>
/// Every statement here runs through <see cref="TestDatabase.ApplicationDataSource"/>, never
/// <see cref="TestDatabase.DataSource"/>. A superuser bypasses grants entirely, so the same
/// checks run as the migrator's role would prove nothing — and the first test pins down which
/// role the connection really is, so that could not happen unnoticed.
/// </remarks>
public sealed class AuditLogAppendOnlyTests : IClassFixture<TestDatabase>
{
    private const string InsufficientPrivilege = "42501";

    private readonly TestDatabase _database;

    public AuditLogAppendOnlyTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_connection_under_test_is_the_unprivileged_application_role()
    {
        await using var command = _database.ApplicationDataSource.CreateCommand(
            "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(ApplicationRole.Name, reader.GetString(0));
        Assert.False(reader.GetBoolean(1), "The application role must not be a superuser.");
        Assert.False(reader.GetBoolean(2), "The application role must not bypass row security.");
    }

    [RequiresDatabaseFact]
    public async Task The_application_can_append_an_entry()
    {
        var before = await CountAsync();

        await new SecurityStore(_database.ApplicationDataSource).AppendAsync(
            new AuditEntry(Guid.NewGuid(), "test.append", "test", Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(before + 1, await CountAsync());
    }

    [RequiresDatabaseFact]
    public Task An_update_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("UPDATE audit_log SET action = 'rewritten'");

    [RequiresDatabaseFact]
    public Task A_delete_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("DELETE FROM audit_log");

    [RequiresDatabaseFact]
    public Task A_truncate_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("TRUNCATE audit_log");

    [RequiresDatabaseFact]
    public async Task The_application_cannot_rewrite_the_migration_journal()
    {
        // Otherwise a Gateway could mark scripts as applied and talk its own schema check
        // into passing (ADR-0012).
        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsApplicationAsync("DELETE FROM schemaversions"));

        Assert.Equal(InsufficientPrivilege, refused.SqlState);
    }

    private async Task AssertRefusedAsync(string statement)
    {
        // A real entry first, so a statement that did nothing because the table was empty
        // could not be mistaken for one that was refused.
        await new SecurityStore(_database.ApplicationDataSource).AppendAsync(
            new AuditEntry(null, "test.must_survive"),
            CancellationToken.None);
        var before = await CountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsApplicationAsync(statement));

        Assert.Equal(InsufficientPrivilege, refused.SqlState);
        Assert.Equal(before, await CountAsync());
    }

    private async Task ExecuteAsApplicationAsync(string statement)
    {
        await using var command = _database.ApplicationDataSource.CreateCommand(statement);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync()
    {
        await using var command = _database.ApplicationDataSource.CreateCommand(
            "SELECT count(*) FROM audit_log WHERE action <> 'rewritten'");
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
