namespace ScadaDarbox.Core.Security;

/// <summary>A user's stored password hash and how their sign-ins have been going, for checking a login.</summary>
public sealed record StoredCredential(
    Guid UserId,
    string PasswordHash,
    int FailedSignIns,
    DateTimeOffset? LockedUntilUtc)
{
    /// <summary>How many failures this account carries, and whether it is shut (ADR-0031).</summary>
    public SignInLockout Lockout => new(FailedSignIns, LockedUntilUtc);
}

/// <summary>A user as created by an Admin, or by the first-Admin bootstrap.</summary>
public sealed record NewUser(Guid Id, Guid TenantId, string Username, string PasswordHash, bool IsAdmin);

/// <summary>
/// Persistence for users, Site roles and sessions (ADR-0011).
/// </summary>
public interface ISecurityStore
{
    /// <summary>Every active user with their current roles.</summary>
    Task<IReadOnlyList<UserAccess>> GetActiveUsersAsync(CancellationToken cancellationToken);

    /// <summary>The credential of an active user, matched case-insensitively by name.</summary>
    Task<StoredCredential?> FindCredentialAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="user"/> only if no user row exists at all, deactivated
    /// ones included.
    /// </summary>
    /// <returns>False when the table was not empty, and nothing was written.</returns>
    Task<bool> CreateFirstUserAsync(NewUser user, CancellationToken cancellationToken);

    /// <exception cref="Configuration.ConfigurationConflictException">The name is taken by an active user.</exception>
    Task CreateUserAsync(NewUser user, CancellationToken cancellationToken);

    /// <summary>Soft-deletes a user and revokes every session they hold, in one transaction.</summary>
    /// <exception cref="Configuration.ConfigurationConflictException">
    /// The user is the last active Admin. Removing them would leave nobody able to manage
    /// users, and the only way back would be editing the database by hand.
    /// </exception>
    /// <returns>False when there is no such active user.</returns>
    Task<bool> DeactivateUserAsync(Guid userId, DateTimeOffset atUtc, CancellationToken cancellationToken);

    /// <exception cref="Configuration.ConfigurationConflictException">Demoting the last active Admin.</exception>
    /// <returns>False when there is no such active user.</returns>
    Task<bool> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);

    /// <returns>False when there is no such active user.</returns>
    Task<bool> SetPasswordHashAsync(Guid userId, string passwordHash, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed sign-in, and the lock it may have earned (ADR-0031).
    /// </summary>
    /// <remarks>
    /// Written on the account rather than kept in the process: an in-memory counter is one an attacker
    /// clears by making the Gateway restart, and a restart is the cheapest thing they can ask for.
    /// </remarks>
    Task RecordSignInFailureAsync(Guid userId, SignInLockout lockout, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets an account's failures and any lock on it — what a successful sign-in leaves behind, and what
    /// an Admin's password reset does (ADR-0031 §3, §5).
    /// </summary>
    Task ClearSignInFailuresAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Grants or changes a user's role on one Site.</summary>
    /// <returns>False when the user or the Site does not exist.</returns>
    Task<bool> SetSiteRoleAsync(Guid userId, Guid siteId, SiteRole role, CancellationToken cancellationToken);

    /// <returns>False when the user held no role on that Site.</returns>
    Task<bool> RemoveSiteRoleAsync(Guid userId, Guid siteId, CancellationToken cancellationToken);

    Task CreateSessionAsync(Session session, byte[] tokenHash, CancellationToken cancellationToken);

    Task<Session?> FindSessionAsync(byte[] tokenHash, CancellationToken cancellationToken);

    Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAtUtc, CancellationToken cancellationToken);

    Task RevokeSessionAsync(Guid sessionId, DateTimeOffset atUtc, CancellationToken cancellationToken);
}
