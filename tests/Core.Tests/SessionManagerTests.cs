using System.Security.Cryptography;
using System.Text;
using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Core.Tests;

/// <summary>ADR-0011's session criteria: two clocks, revocation, and no recoverable token at rest.</summary>
public class SessionManagerTests
{
    private static readonly Guid UserId = new("cccccccc-0000-4000-8000-000000000001");

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _store = new();
    private readonly UserDirectorySource _users = new(new UserDirectory(
        [new UserAccess(UserId, "operator", IsAdmin: false, new Dictionary<Guid, SiteRole>())]));

    [Fact]
    public async Task A_fresh_token_validates_to_its_user()
    {
        var issued = await Manager().IssueAsync(UserId, CancellationToken.None);

        var session = await Manager().ValidateAsync(issued.Token, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(UserId, session!.UserId);
    }

    [Fact]
    public async Task Only_a_hash_of_the_token_is_stored()
    {
        var issued = await Manager().IssueAsync(UserId, CancellationToken.None);

        var stored = Assert.Single(_store.TokenHashes);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token)), stored);
        Assert.DoesNotContain(issued.Token, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_within_the_idle_timeout_keeps_a_session_alive()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        for (var shift = 0; shift < 4; shift++)
        {
            _clock.Advance(TimeSpan.FromHours(11));
            Assert.NotNull(await manager.ValidateAsync(issued.Token, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_session_left_idle_past_the_timeout_is_rejected_and_never_revived()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(12));

        Assert.Null(await manager.ValidateAsync(issued.Token, CancellationToken.None));

        // Presenting it again must not count as the use that would have kept it alive.
        Assert.Null(await manager.ValidateAsync(issued.Token, CancellationToken.None));

        // Nor does a restart, which reloads the session from storage.
        Assert.Null(await Manager().ValidateAsync(issued.Token, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_in_continuous_use_is_rejected_at_its_absolute_lifetime()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        for (var hour = 1; hour < 7 * 24; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.NotNull(await manager.ValidateAsync(issued.Token, CancellationToken.None));
        }

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Null(await manager.ValidateAsync(issued.Token, CancellationToken.None));
        Assert.Null(await Manager().ValidateAsync(issued.Token, CancellationToken.None));
    }

    [Fact]
    public async Task An_open_connection_counts_as_use_of_its_session()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        // An operator watching a screen through a shift and a half, with no REST request at all.
        for (var sweep = 0; sweep < 18; sweep++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.True(await manager.TouchAsync(issued.SessionId, CancellationToken.None));
        }

        Assert.NotNull(await manager.ValidateAsync(issued.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_one_session_leaves_the_users_other_sessions_working()
    {
        var manager = Manager();
        var revoked = await manager.IssueAsync(UserId, CancellationToken.None);
        var kept = await manager.IssueAsync(UserId, CancellationToken.None);

        await manager.RevokeAsync(revoked.SessionId, CancellationToken.None);

        Assert.Null(await manager.ValidateAsync(revoked.Token, CancellationToken.None));
        Assert.Null(await Manager().ValidateAsync(revoked.Token, CancellationToken.None));
        Assert.NotNull(await manager.ValidateAsync(kept.Token, CancellationToken.None));
    }

    [Fact]
    public async Task A_deactivated_user_is_rejected_on_the_very_next_validation()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        _users.Set(UserDirectory.Empty);

        Assert.Null(await manager.ValidateAsync(issued.Token, CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_last_seen_time_is_written_at_most_once_per_interval()
    {
        var manager = Manager();
        var issued = await manager.IssueAsync(UserId, CancellationToken.None);

        for (var request = 0; request < 20; request++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await manager.ValidateAsync(issued.Token, CancellationToken.None);
        }

        Assert.Equal(0, _store.Touches);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await manager.ValidateAsync(issued.Token, CancellationToken.None);

        Assert.Equal(1, _store.Touches);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        Assert.Null(await Manager().ValidateAsync(SessionTokens.Generate(), CancellationToken.None));
    }

    private SessionManager Manager() => new(_store, _users, new SessionPolicy(), _clock);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Sessions only; nothing else a session manager needs.</summary>
    private sealed class InMemorySessionStore : ISecurityStore
    {
        private readonly Dictionary<string, (byte[] Hash, Session Session)> _sessions = [];

        public IReadOnlyList<byte[]> TokenHashes => _sessions.Values.Select(entry => entry.Hash).ToList();

        public int Touches { get; private set; }

        public Task CreateSessionAsync(Session session, byte[] tokenHash, CancellationToken cancellationToken)
        {
            _sessions[Convert.ToHexString(tokenHash)] = (tokenHash.ToArray(), session);
            return Task.CompletedTask;
        }

        public Task<Session?> FindSessionAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.TryGetValue(Convert.ToHexString(tokenHash), out var entry) ? entry.Session : null);

        public Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAtUtc, CancellationToken cancellationToken)
        {
            Touches++;
            Update(sessionId, session => session with { LastSeenAtUtc = lastSeenAtUtc });
            return Task.CompletedTask;
        }

        public Task RevokeSessionAsync(Guid sessionId, DateTimeOffset atUtc, CancellationToken cancellationToken)
        {
            Update(sessionId, session => session with { RevokedAtUtc = atUtc });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UserAccess>> GetActiveUsersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoredCredential?> FindCredentialAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CreateFirstUserAsync(NewUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateUserAsync(NewUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeactivateUserAsync(Guid userId, DateTimeOffset atUtc, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetPasswordHashAsync(Guid userId, string passwordHash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordSignInFailureAsync(Guid userId, SignInLockout lockout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ClearSignInFailuresAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetSiteRoleAsync(Guid userId, Guid siteId, SiteRole role, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveSiteRoleAsync(Guid userId, Guid siteId, CancellationToken cancellationToken) => throw new NotSupportedException();

        private void Update(Guid sessionId, Func<Session, Session> change)
        {
            var key = _sessions.Single(entry => entry.Value.Session.Id == sessionId).Key;
            _sessions[key] = (_sessions[key].Hash, change(_sessions[key].Session));
        }
    }
}
