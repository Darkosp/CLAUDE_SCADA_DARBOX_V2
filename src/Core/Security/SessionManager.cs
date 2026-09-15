using System.Collections.Concurrent;

namespace ScadaDarbox.Core.Security;

/// <summary>
/// Issues, validates, refreshes and revokes sessions (ADR-0011).
/// </summary>
/// <remarks>
/// Sessions are server-side rows, cached here so that validating a token on every request
/// costs a dictionary lookup rather than a query. Every change to a session goes through
/// this class, which is what keeps the cache honest: a Gateway is a single process, so
/// there is no other writer for the cache to fall behind.
/// </remarks>
public sealed class SessionManager
{
    private readonly ISecurityStore _store;
    private readonly UserDirectorySource _users;
    private readonly SessionPolicy _policy;
    private readonly TimeProvider _clock;

    private readonly ConcurrentDictionary<string, CachedSession> _byTokenHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CachedSession> _byId = new();

    public SessionManager(ISecurityStore store, UserDirectorySource users, SessionPolicy policy, TimeProvider clock)
    {
        _store = store;
        _users = users;
        _policy = policy;
        _clock = clock;
    }

    public async Task<IssuedSession> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var token = SessionTokens.Generate();
        var tokenHash = SessionTokens.Hash(token);
        var now = _clock.GetUtcNow();
        var session = new Session(Guid.NewGuid(), userId, now, now, RevokedAtUtc: null);

        await _store.CreateSessionAsync(session, tokenHash, cancellationToken).ConfigureAwait(false);
        Remember(new CachedSession(session, tokenHash));

        return new IssuedSession(token, session.Id, userId);
    }

    /// <summary>
    /// Checks a presented token, and counts the check as use of the session.
    /// </summary>
    /// <returns>Null for an unknown, revoked or expired token, or one whose user is no longer active.</returns>
    public async Task<AuthenticatedSession?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var tokenHash = SessionTokens.Hash(token);

        if (!_byTokenHash.TryGetValue(Convert.ToHexString(tokenHash), out var cached))
        {
            var stored = await _store.FindSessionAsync(tokenHash, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.RevokedAtUtc is not null)
            {
                return null;
            }

            cached = Remember(new CachedSession(stored, tokenHash));
        }

        return await UseAsync(cached, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts an open connection as use of its session, without a token being presented.
    /// </summary>
    /// <returns>False when the session is no longer valid, so the connection must go.</returns>
    public async Task<bool> TouchAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _byId.TryGetValue(sessionId, out var cached)
        && await UseAsync(cached, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Ends one session — a logout, or a single stolen token being revoked.</summary>
    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await _store.RevokeSessionAsync(sessionId, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        if (_byId.TryGetValue(sessionId, out var cached))
        {
            Invalidate(cached);
        }
    }

    /// <summary>
    /// Invalidates every cached session of a user whose sessions were revoked in storage by
    /// another operation — deactivation revokes them in the same transaction as the soft
    /// delete.
    /// </summary>
    public void ForgetUser(Guid userId)
    {
        foreach (var cached in _byId.Values.Where(session => session.UserId == userId).ToList())
        {
            Invalidate(cached);
        }
    }

    /// <summary>Drops cached sessions that can never be valid again, so the cache does not only grow.</summary>
    public void PruneExpired()
    {
        var now = _clock.GetUtcNow();

        foreach (var cached in _byId.Values)
        {
            bool expired;
            lock (cached)
            {
                expired = cached.Revoked || _policy.IsExpired(cached.CreatedAtUtc, cached.LastSeenAtUtc, now);
            }

            if (expired)
            {
                Invalidate(cached);
            }
        }
    }

    private async Task<AuthenticatedSession?> UseAsync(CachedSession cached, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        bool persist;

        lock (cached)
        {
            // Expiry is checked before the last-seen time moves, so a session that has run
            // out can never be revived by being presented again.
            if (cached.Revoked || _policy.IsExpired(cached.CreatedAtUtc, cached.LastSeenAtUtc, now))
            {
                cached.Revoked = true;
            }
            else if (_users.Current.Find(cached.UserId) is not null)
            {
                cached.LastSeenAtUtc = now;
            }
            else
            {
                return null;
            }

            persist = !cached.Revoked && now - cached.PersistedLastSeenAtUtc >= _policy.LastSeenWriteInterval;
            if (persist)
            {
                cached.PersistedLastSeenAtUtc = now;
            }
        }

        if (cached.Revoked)
        {
            Invalidate(cached);
            return null;
        }

        if (persist)
        {
            await _store.TouchSessionAsync(cached.Id, now, cancellationToken).ConfigureAwait(false);
        }

        return new AuthenticatedSession(cached.Id, cached.UserId);
    }

    private CachedSession Remember(CachedSession session)
    {
        var winner = _byTokenHash.GetOrAdd(session.Key, session);
        _byId.TryAdd(winner.Id, winner);
        return winner;
    }

    /// <summary>
    /// Marks a session unusable and drops it from the cache. The mark matters as much as
    /// the removal: a request that fetched the entry a moment earlier still holds it.
    /// </summary>
    private void Invalidate(CachedSession session)
    {
        lock (session)
        {
            session.Revoked = true;
        }

        _byTokenHash.TryRemove(session.Key, out _);
        _byId.TryRemove(session.Id, out _);
    }

    private sealed class CachedSession
    {
        public CachedSession(Session session, byte[] tokenHash)
        {
            Id = session.Id;
            UserId = session.UserId;
            CreatedAtUtc = session.CreatedAtUtc;
            LastSeenAtUtc = session.LastSeenAtUtc;
            PersistedLastSeenAtUtc = session.LastSeenAtUtc;
            Revoked = session.RevokedAtUtc is not null;
            Key = Convert.ToHexString(tokenHash);
        }

        public Guid Id { get; }

        public Guid UserId { get; }

        public string Key { get; }

        public DateTimeOffset CreatedAtUtc { get; }

        public DateTimeOffset LastSeenAtUtc { get; set; }

        public DateTimeOffset PersistedLastSeenAtUtc { get; set; }

        public bool Revoked { get; set; }
    }
}
