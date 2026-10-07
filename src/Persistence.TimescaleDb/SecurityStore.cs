using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Users, Site roles, sessions and the audit trail (ADR-0011), over Dapper (ADR-0008).</summary>
public sealed class SecurityStore : ISecurityStore, IAuditLog
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private readonly NpgsqlDataSource _dataSource;

    static SecurityStore() => DapperConfiguration.Ensure();

    public SecurityStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<UserAccess>> GetActiveUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var users = await connection.QueryAsync<UserRow>(new CommandDefinition(
            "SELECT id, username, is_admin FROM app_user_active",
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // Joined to the active user view, so a role row left behind by a deactivated user
        // can never grant anything.
        var roles = await connection.QueryAsync<SiteRoleRow>(new CommandDefinition(
            """
            SELECT r.user_id, r.site_id, r.site_role
            FROM user_site_role r
            JOIN app_user_active u ON u.id = r.user_id
            """,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var rolesByUser = roles
            .GroupBy(role => role.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<Guid, SiteRole>)group.ToDictionary(
                    role => role.SiteId,
                    role => Enum.Parse<SiteRole>(role.SiteRole)));

        return users
            .Select(user => new UserAccess(
                user.Id,
                user.Username,
                user.IsAdmin,
                rolesByUser.GetValueOrDefault(user.Id) ?? new Dictionary<Guid, SiteRole>()))
            .ToList();
    }

    public async Task<StoredCredential?> FindCredentialAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition(
            """
            SELECT id AS user_id, password_hash, failed_sign_ins, locked_until AS locked_until_utc
            FROM app_user_active
            WHERE lower(username) = lower(@username)
            """,
            new { username },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>
    /// The credential row as the database hands it over.
    /// </summary>
    /// <remarks>
    /// **A class with settable properties rather than a positional record, and the column is aliased**, both
    /// for reasons Dapper decided for us and both worth knowing before the next row here is written: a
    /// nullable `timestamptz` is offered to a constructor as a non-nullable <see cref="DateTime"/>, so a
    /// `DateTime?` parameter matches nothing, and `locked_until` does not match a property called
    /// `LockedUntilUtc` (underscores are ignored, suffixes are not) — which would have left the lock silently
    /// unread rather than failing, the worse of the two.
    /// </remarks>
    private sealed class CredentialRow
    {
        public Guid UserId { get; set; }

        public string PasswordHash { get; set; } = string.Empty;

        public int FailedSignIns { get; set; }

        public DateTime? LockedUntilUtc { get; set; }

        internal StoredCredential ToDomain() => new(
            UserId,
            PasswordHash,
            FailedSignIns,
            LockedUntilUtc is { } until
                ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc))
                : null);
    }

    public async Task RecordSignInFailureAsync(
        Guid userId,
        SignInLockout lockout,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // One statement, so a failure and the lock it earns cannot land apart. `deleted_at IS NULL` keeps a
        // deactivated account from being written to by an attempt against a name that is no longer live.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app_user
            SET failed_sign_ins = @FailedSignIns, locked_until = @LockedUntilUtc
            WHERE id = @userId AND deleted_at IS NULL
            """,
            new { userId, lockout.FailedSignIns, lockout.LockedUntilUtc },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task ClearSignInFailuresAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET failed_sign_ins = 0, locked_until = NULL WHERE id = @userId AND deleted_at IS NULL",
            new { userId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> CreateFirstUserAsync(NewUser user, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Locked so two Gateways starting together against an empty table cannot both
        // decide they are first and create two initial Admins.
        await LockUsersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app_user",
            transaction: transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (existing > 0)
        {
            return false;
        }

        await InsertUserAsync(connection, transaction, user, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task CreateUserAsync(NewUser user, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertUserAsync(connection, transaction: null, user, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            throw new ConfigurationConflictException($"A user named '{user.Username}' already exists.");
        }
    }

    public async Task<bool> DeactivateUserAsync(Guid userId, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await GuardLastAdminAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // One transaction: a user who is gone but still holds a working session would be
        // exactly the "access stops whenever the token happens to expire" case ADR-0011
        // rules out.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app_user SET deleted_at = @atUtc WHERE id = @userId AND deleted_at IS NULL;
            UPDATE session SET revoked_at = @atUtc WHERE user_id = @userId AND revoked_at IS NULL;
            """,
            new { userId, atUtc },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var exists = isAdmin
            ? await IsActiveUserAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false)
            : await GuardLastAdminAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);

        if (!exists)
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET is_admin = @isAdmin WHERE id = @userId AND deleted_at IS NULL",
            new { userId, isAdmin },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetPasswordHashAsync(Guid userId, string passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // **A reset clears the lock, and that is the way out of one** (ADR-0031 §5): the person who has
        // forgotten a password and the person who has been locked out are in the same conversation with an
        // Admin, and the product does not need a second control to reach the same state.
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app_user
            SET password_hash = @passwordHash, failed_sign_ins = 0, locked_until = NULL
            WHERE id = @userId AND deleted_at IS NULL
            """,
            new { userId, passwordHash },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> SetSiteRoleAsync(Guid userId, Guid siteId, SiteRole role, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Selected from the active user view: the foreign key alone would accept a
            // role for someone already deactivated.
            return await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_site_role (user_id, site_id, site_role)
                SELECT id, @siteId, @role FROM app_user_active WHERE id = @userId
                ON CONFLICT (user_id, site_id) DO UPDATE SET site_role = EXCLUDED.site_role
                """,
                new { userId, siteId, role = role.ToString() },
                cancellationToken: cancellationToken))
                .ConfigureAwait(false) > 0;
        }
        catch (PostgresException exception) when (exception.SqlState == ForeignKeyViolation)
        {
            return false;
        }
    }

    public async Task<bool> RemoveSiteRoleAsync(Guid userId, Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_site_role WHERE user_id = @userId AND site_id = @siteId",
            new { userId, siteId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }

    public async Task CreateSessionAsync(Session session, byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO session (id, user_id, token_hash, created_at, last_seen_at)
            VALUES (@Id, @UserId, @tokenHash, @CreatedAtUtc, @LastSeenAtUtc)
            """,
            new { session.Id, session.UserId, tokenHash, session.CreatedAtUtc, session.LastSeenAtUtc },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<Session?> FindSessionAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            SELECT id, user_id, created_at, last_seen_at, revoked_at
            FROM session
            WHERE token_hash = @tokenHash
            """,
            new { tokenHash },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null
            ? null
            : new Session(row.Id, row.UserId, row.CreatedAt, row.LastSeenAt, row.RevokedAt);
    }

    public async Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE session SET last_seen_at = @lastSeenAtUtc WHERE id = @sessionId AND revoked_at IS NULL",
            new { sessionId, lastSeenAtUtc },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RevokeSessionAsync(Guid sessionId, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE session SET revoked_at = @atUtc WHERE id = @sessionId AND revoked_at IS NULL",
            new { sessionId, atUtc },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_log (actor_user_id, action, entity_type, entity_id, detail)
            VALUES (@actor, @action, @entityType, @entityId, @detail)
            """,
            connection);

        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)entry.ActorUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", entry.Action);
        command.Parameters.AddWithValue("entityType", NpgsqlDbType.Text, (object?)entry.EntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("entityId", NpgsqlDbType.Uuid, (object?)entry.EntityId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(entry.Detail ?? new Dictionary<string, object?>()),
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task InsertUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewUser user,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app_user (id, tenant_id, username, password_hash, is_admin)
            VALUES (@Id, @TenantId, @Username, @PasswordHash, @IsAdmin)
            """,
            user,
            transaction,
            cancellationToken: cancellationToken));

    private static Task<bool> IsActiveUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app_user_active WHERE id = @userId)",
            new { userId },
            transaction,
            cancellationToken: cancellationToken));

    private static Task LockUsersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            "LOCK TABLE app_user IN SHARE ROW EXCLUSIVE MODE",
            transaction: transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Refuses to let the last active Admin stop being one.
    /// </summary>
    /// <returns>False when the user is not an active user at all.</returns>
    private static async Task<bool> GuardLastAdminAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // Two Admins demoting each other at the same moment would otherwise each see the
        // other still standing, and both succeed.
        await LockUsersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var target = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT id, username, is_admin FROM app_user_active WHERE id = @userId",
            new { userId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (target is null)
        {
            return false;
        }

        if (target.IsAdmin)
        {
            var otherAdmins = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM app_user_active WHERE is_admin AND id <> @userId",
                new { userId },
                transaction,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (otherAdmins == 0)
            {
                throw new ConfigurationConflictException(
                    "This is the last active Admin. Make another user an Admin first.");
            }
        }

        return true;
    }

    private sealed record UserRow(Guid Id, string Username, bool IsAdmin);

    private sealed record SiteRoleRow(Guid UserId, Guid SiteId, string SiteRole);

    /// <remarks>
    /// <c>DateTime</c>, not <c>DateTimeOffset</c>: Npgsql reads <c>timestamptz</c> as a UTC
    /// <c>DateTime</c>, and Dapper will not convert while matching a constructor — it fails
    /// to materialise the row at all. The conversion to <see cref="Session"/> is lossless
    /// because the kind is UTC. This read only runs when a session is not already cached (a
    /// revoked one, or any session after a restart), which is how it went unnoticed until a
    /// test presented a revoked token.
    /// </remarks>
    private sealed record SessionRow(
        Guid Id,
        Guid UserId,
        DateTime CreatedAt,
        DateTime LastSeenAt,
        DateTime? RevokedAt);
}
