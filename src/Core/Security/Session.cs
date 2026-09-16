using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ScadaDarbox.Core.Security;

/// <summary>A login session as stored — never the token itself, only what it unlocks.</summary>
public sealed record Session(
    Guid Id,
    Guid UserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc);

/// <summary>A session that has just been checked and is valid right now.</summary>
public sealed record AuthenticatedSession(Guid SessionId, Guid UserId);

/// <summary>A newly issued session, carrying the only copy of its token that will ever exist.</summary>
public sealed record IssuedSession(string Token, Guid SessionId, Guid UserId);

/// <summary>
/// How long a session lives (ADR-0011): whichever of the two clocks runs out first.
/// </summary>
/// <remarks>
/// Both are configuration, not architecture — a deployment may tighten them freely.
/// </remarks>
public sealed class SessionPolicy
{
    /// <summary>Invalid once unused for this long. An open hub connection counts as use.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Invalid this long after login, however continuously it has been used.</summary>
    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How stale the stored last-seen time may become before it is written again.
    /// </summary>
    /// <remarks>
    /// Validation keeps an exact last-seen time in memory; the row is refreshed only this
    /// often, so an ordinary request costs a lookup rather than a write. The worst case is
    /// that after a restart a session looks idle for this much longer than it really was —
    /// negligible against a twelve-hour idle timeout.
    /// </remarks>
    public TimeSpan LastSeenWriteInterval { get; init; } = TimeSpan.FromMinutes(1);

    public bool IsExpired(DateTimeOffset createdAtUtc, DateTimeOffset lastSeenAtUtc, DateTimeOffset nowUtc) =>
        nowUtc - lastSeenAtUtc >= IdleTimeout || nowUtc - createdAtUtc >= AbsoluteLifetime;
}

/// <summary>Creation and hashing of opaque session tokens (ADR-0011).</summary>
public static class SessionTokens
{
    /// <summary>A fresh 128-bit random token, URL-safe so it survives a query string.</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// What is stored in place of the token.
    /// </summary>
    /// <remarks>
    /// A plain SHA-256 is enough here, unlike for a password: the token is 128 random bits,
    /// so there is no dictionary to try, and a slow hash would only slow every request.
    /// </remarks>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
