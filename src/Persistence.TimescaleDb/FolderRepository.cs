using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Folder tree storage (ADR-0008).</summary>
/// <remarks>
/// Cross-site placement is not checked here — the composite foreign key
/// <c>(site_id, parent_folder_id) REFERENCES folder (site_id, id)</c> makes such a row
/// unwritable, so no repository code can forget it. What the key cannot express is
/// "no cycles", which is why <see cref="UpdateAsync"/> checks that explicitly.
/// </remarks>
public sealed class FolderRepository : IFolderRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static FolderRepository() => DapperConfiguration.Ensure();

    public FolderRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Folder>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<FolderRow>(
            new CommandDefinition(
                """
                SELECT id, site_id, parent_folder_id, name
                FROM folder_active
                WHERE site_id = @siteId
                ORDER BY name
                """,
                new { siteId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Folder folder, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO folder (id, site_id, parent_folder_id, name)
                VALUES (@Id, @SiteId, @ParentFolderId, @Name)
                """,
                folder,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.FolderTakenAsync(_dataSource, folder.Name, folder.SiteId, folder.ParentFolderId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(Folder folder, CancellationToken cancellationToken)
    {
        // The guard is part of the UPDATE rather than a read-then-write: a separate
        // check could pass and then be invalidated by a concurrent move before the
        // write lands.
        //
        // The recursive term uses UNION, not UNION ALL. Deduplication is what makes it
        // terminate if the table already contains a cycle, so the guard cannot itself
        // hang on the condition it exists to prevent.
        // The descendant walk reads folder_active, not the base table: a deleted folder
        // has no live children (deletion requires an empty folder), so counting it as an
        // ancestor could only refuse a move that is actually fine.
        const string sql = """
            UPDATE folder
            SET name = @Name, parent_folder_id = @ParentFolderId
            WHERE id = @Id
              AND deleted_at IS NULL
              AND (
                    @ParentFolderId IS NULL
                    OR @ParentFolderId NOT IN (
                        WITH RECURSIVE descendants AS (
                            SELECT id FROM folder_active WHERE id = @Id
                            -- UNION, never UNION ALL. The deduplication is load-bearing:
                            -- it is what makes this terminate if the table already
                            -- contains a cycle. UNION ALL would recurse forever on the
                            -- very condition this query exists to detect.
                            UNION
                            SELECT f.id FROM folder_active f
                            JOIN descendants d ON f.parent_folder_id = d.id
                        )
                        SELECT id FROM descendants
                    )
              )
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(
                new CommandDefinition(sql, folder, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.FolderTakenAsync(_dataSource, folder.Name, folder.SiteId, folder.ParentFolderId, cancellationToken).ConfigureAwait(false);
        }

        if (updated > 0)
        {
            return;
        }

        // Nothing was written, which means either the folder is gone or the move was
        // refused. Distinguishing them gives the caller an honest reason.
        var exists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM folder_active WHERE id = @Id)",
                new { folder.Id },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        throw exists
            ? new ConfigurationConflictException(
                $"Folder '{folder.Name}' cannot be moved under itself or one of its own descendants.")
            : new ConfigurationConflictException($"Folder {folder.Id} no longer exists.");
    }

    public async Task DeleteAsync(Guid folderId, CancellationToken cancellationToken)
    {
        // Emptiness is checked inside the UPDATE, so a device or folder moved in
        // concurrently cannot land in a folder that a parallel request is deleting.
        const string sql = """
            UPDATE folder
            SET deleted_at = now()
            WHERE id = @folderId
              AND deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM folder_active WHERE parent_folder_id = @folderId)
              AND NOT EXISTS (SELECT 1 FROM device_active WHERE folder_id = @folderId)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { folderId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            return;
        }

        // Nothing was written: either the folder is already gone, or it still holds
        // something. Saying which is the difference between an error the operator can
        // act on and one they cannot.
        var stillHasContents = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM folder_active WHERE parent_folder_id = @folderId)
                    OR EXISTS (SELECT 1 FROM device_active WHERE folder_id = @folderId)
                """,
                new { folderId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        throw stillHasContents
            ? new ConfigurationConflictException(
                "This folder still contains a folder or a device. Move or delete the contents first — "
                + "deleting a folder never moves or removes what is inside it.")
            : new ConfigurationConflictException($"Folder {folderId} no longer exists.");
    }
}
