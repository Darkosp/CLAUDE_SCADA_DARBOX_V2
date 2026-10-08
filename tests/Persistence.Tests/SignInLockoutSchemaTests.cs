using Npgsql;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The lockout columns and the way out of a lock (ADR-0031 §1, §5): the count is durable, the view carries
/// it, the database refuses a count that cannot be reached, and an Admin's password reset clears both.
/// </summary>
public sealed class SignInLockoutSchemaTests : IClassFixture<TestDatabase>
{
    private const string CheckViolation = "23514";

    private readonly TestDatabase _database;

    public SignInLockoutSchemaTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_count_and_the_deadline_come_back_through_the_active_view()
    {
        var seeded = await SeedAsync();
        var store = new SecurityStore(_database.ApplicationDataSource);

        var before = await store.FindCredentialAsync(seeded.Username, CancellationToken.None);
        Assert.Equal(0, before?.FailedSignIns);
        Assert.Null(before?.LockedUntilUtc);
        Assert.False(before!.Lockout.IsLockedAt(DateTimeOffset.UtcNow));

        var until = DateTimeOffset.UtcNow.AddMinutes(15);
        await store.RecordSignInFailureAsync(seeded.UserId, new SignInLockout(5, until), CancellationToken.None);

        var after = await store.FindCredentialAsync(seeded.Username, CancellationToken.None);

        // **Durable on purpose** (ADR-0031 §1): an attacker's cheapest move against a counter kept in the
        // process is to make the process restart, and a lock that a restart clears is not a lock.
        Assert.Equal(5, after?.FailedSignIns);
        // **Compared to the precision the database keeps.** `timestamptz` holds microseconds, so the instant
        // written back can differ from the one handed in by 100 ns — and the claim being tested is that the
        // deadline survived the round trip, not that Postgres stores a .NET tick.
        Assert.Equal(until, after!.LockedUntilUtc!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(after!.Lockout.IsLockedAt(DateTimeOffset.UtcNow));
    }

    [RequiresDatabaseFact]
    public async Task A_password_reset_is_the_way_out_of_a_lock()
    {
        var seeded = await SeedAsync();
        var store = new SecurityStore(_database.ApplicationDataSource);

        await store.RecordSignInFailureAsync(
            seeded.UserId,
            new SignInLockout(5, DateTimeOffset.UtcNow.AddMinutes(15)),
            CancellationToken.None);

        Assert.True(await store.SetPasswordHashAsync(seeded.UserId, "a-new-hash", CancellationToken.None));

        var cleared = await store.FindCredentialAsync(seeded.Username, CancellationToken.None);

        Assert.Equal(0, cleared?.FailedSignIns);
        Assert.Null(cleared?.LockedUntilUtc);
    }

    [RequiresDatabaseFact]
    public async Task A_negative_count_is_refused_by_the_schema()
    {
        // A negative count would put the threshold permanently out of reach — a lockout that silently is not
        // one — and two writers touch this column: the sign-in path increments it and a reset clears it.
        var seeded = await SeedAsync();

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE app_user SET failed_sign_ins = -1 WHERE id = '{seeded.UserId}';",
            connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(CheckViolation, refused.SqlState);
        Assert.Contains("ck_app_user_failed_sign_ins", refused.MessageText);
    }

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), $"locked-{Guid.NewGuid():N}"[..20]);
        var tenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Lockout tenant');
            INSERT INTO app_user (id, tenant_id, username, password_hash, is_admin)
            VALUES ('{seeded.UserId}', '{tenantId}', '{seeded.Username}', 'not-a-real-hash', false);
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        return seeded;
    }

    private sealed record Seeded(Guid UserId, Guid TenantId, string Username);
}
