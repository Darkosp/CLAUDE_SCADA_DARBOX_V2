using ScadaDarbox.Core.Security;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Sessions read back from storage — the path taken after a restart, or for any session no
/// longer cached — over the application's own connection.
/// </summary>
public sealed class SessionStorageTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public SessionStorageTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_stored_session_reads_back_with_its_times_intact()
    {
        var store = new SecurityStore(_database.ApplicationDataSource);
        var userId = await CreateUserAsync(store);
        var tokenHash = SessionTokens.Hash(SessionTokens.Generate());

        // Sub-second precision on purpose: a read that truncated or shifted the clocks would
        // quietly move both expiry deadlines.
        var created = new DateTimeOffset(2026, 9, 15, 6, 30, 12, 345, TimeSpan.Zero);
        var session = new Session(Guid.NewGuid(), userId, created, created.AddMinutes(5), RevokedAtUtc: null);

        await store.CreateSessionAsync(session, tokenHash, CancellationToken.None);
        var read = await store.FindSessionAsync(tokenHash, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(session.Id, read!.Id);
        Assert.Equal(userId, read.UserId);
        Assert.Equal(session.CreatedAtUtc, read.CreatedAtUtc);
        Assert.Equal(session.LastSeenAtUtc, read.LastSeenAtUtc);
        Assert.Equal(TimeSpan.Zero, read.CreatedAtUtc.Offset);
        Assert.Null(read.RevokedAtUtc);
    }

    [RequiresDatabaseFact]
    public async Task A_revoked_session_reads_back_as_revoked()
    {
        var store = new SecurityStore(_database.ApplicationDataSource);
        var userId = await CreateUserAsync(store);
        var tokenHash = SessionTokens.Hash(SessionTokens.Generate());
        var now = new DateTimeOffset(2026, 9, 15, 7, 0, 0, TimeSpan.Zero);
        var session = new Session(Guid.NewGuid(), userId, now, now, RevokedAtUtc: null);

        await store.CreateSessionAsync(session, tokenHash, CancellationToken.None);
        await store.RevokeSessionAsync(session.Id, now.AddMinutes(1), CancellationToken.None);

        var read = await store.FindSessionAsync(tokenHash, CancellationToken.None);
        Assert.Equal(now.AddMinutes(1), read!.RevokedAtUtc);
    }

    [RequiresDatabaseFact]
    public async Task A_restarted_session_manager_validates_a_live_token_from_storage()
    {
        var store = new SecurityStore(_database.ApplicationDataSource);
        var userId = await CreateUserAsync(store);
        var users = new UserDirectorySource(new UserDirectory(await store.GetActiveUsersAsync(CancellationToken.None)));

        var issued = await new SessionManager(store, users, new SessionPolicy(), TimeProvider.System)
            .IssueAsync(userId, CancellationToken.None);

        // A second manager has an empty cache, as after a Gateway restart.
        var restarted = new SessionManager(store, users, new SessionPolicy(), TimeProvider.System);
        var validated = await restarted.ValidateAsync(issued.Token, CancellationToken.None);

        Assert.NotNull(validated);
        Assert.Equal(userId, validated!.UserId);
    }

    private async Task<Guid> CreateUserAsync(SecurityStore store)
    {
        var tenantId = Guid.NewGuid();
        await using (var command = _database.DataSource.CreateCommand(
            $"INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Session test tenant')"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var user = new NewUser(Guid.NewGuid(), tenantId, $"session-{Guid.NewGuid():N}"[..20], "not-a-real-hash", IsAdmin: false);
        await store.CreateUserAsync(user, CancellationToken.None);
        return user.Id;
    }
}
