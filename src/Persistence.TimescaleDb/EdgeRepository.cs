using Dapper;
using Npgsql;
using NpgsqlTypes;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Edge configuration storage (ADR-0008, ADR-0019).</summary>
/// <remarks>
/// The name's uniqueness is the database's to enforce (migration 0012); this only turns a
/// violation into a refusal an operator can read, as it does for the other name indexes.
/// </remarks>
public sealed class EdgeRepository : IEdgeRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static EdgeRepository() => DapperConfiguration.Ensure();

    public EdgeRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Edge>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<EdgeRow>(
            new CommandDefinition(
                "SELECT id, tenant_id, name, link_device_id, driver_keys, drivers_declared_at "
                + "FROM edge_active ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Edge edge, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO edge (id, tenant_id, name, link_device_id) VALUES (@Id, @TenantId, @Name, @LinkDeviceId)",
                edge,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw UniqueNames.EdgeTaken(edge.Name);
        }
    }

    public async Task UpdateAsync(Edge edge, CancellationToken cancellationToken)
    {
        // tenant_id is deliberately not updatable. It is the ownership scope (ADR-0004), not a
        // field an edit form should be able to move.
        //
        // The link is only repointable while no device is assigned (ADR-0019). A device an edge
        // reads is not polled by the Gateway, so the link is the only thing feeding its tags;
        // clearing or moving it under a live assignment would leave those tags with no source at
        // all, and nothing would say so. The guard is part of the statement, so an assignment
        // landing at the same moment cannot slip past it, and setting the link to what it already
        // is stays allowed.
        const string sql = """
            UPDATE edge
            SET name = @Name,
                link_device_id = @LinkDeviceId
            WHERE id = @Id
              AND deleted_at IS NULL
              AND (link_device_id IS NOT DISTINCT FROM @LinkDeviceId
                   OR NOT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @Id))
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(
                new CommandDefinition(sql, edge, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw UniqueNames.EdgeTaken(edge.Name);
        }

        if (updated > 0)
        {
            return;
        }

        // Nothing was written: either the edge is gone, or its link was being changed while
        // devices are still assigned. Saying which is the difference between an error the
        // operator can act on and one they cannot.
        var stillHasDevices = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @Id)",
                new { edge.Id },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        throw stillHasDevices
            ? new ConfigurationConflictException(
                "This edge still has devices assigned to it. Unassign them before changing which "
                + "device carries its link: while a device is assigned, that link is the only thing "
                + "reading its tags, and moving it would leave them with no source at all.")
            : new ConfigurationConflictException($"Edge {edge.Id} no longer exists.");
    }

    public async Task<bool> RecordDriversAsync(
        Guid edgeId,
        IReadOnlyList<string> driverKeys,
        DateTimeOffset declaredAtUtc,
        CancellationToken cancellationToken)
    {
        // Only the declaration is touched. It is not an edit of the edge: a declaration arriving
        // must not be able to rewrite a name or a link, and it must not be refused because a link
        // is being repointed at that instant — the two have nothing to do with each other.
        const string sql = """
            UPDATE edge
            SET driver_keys = @driver_keys,
                drivers_declared_at = @declared_at
            WHERE id = @edge_id
              AND deleted_at IS NULL
            """;

        // Npgsql's own parameters rather than Dapper's here, for the array: the element type has to
        // be named rather than inferred — an edge that declares no drivers is a legal declaration,
        // and an empty array gives the driver nothing to infer a type from (the shape AlarmJournal
        // already takes for its own typed parameters).
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("edge_id", NpgsqlDbType.Uuid, edgeId);
        command.Parameters.AddWithValue("driver_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, driverKeys.ToArray());
        command.Parameters.AddWithValue("declared_at", NpgsqlDbType.TimestampTz, declaredAtUtc);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task DeleteAsync(Guid edgeId, CancellationToken cancellationToken)
    {
        // Emptiness is checked inside the UPDATE, so a device assigned to this edge concurrently
        // cannot land on an edge a parallel request is deleting.
        const string sql = """
            UPDATE edge
            SET deleted_at = now()
            WHERE id = @edgeId
              AND deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @edgeId)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { edgeId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            return;
        }

        // Nothing was written: either the edge is already gone, or it still has a device
        // assigned. Saying which is the difference between an error the operator can act on and
        // one they cannot.
        var stillHasDevices = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @edgeId)",
                new { edgeId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        throw stillHasDevices
            ? new ConfigurationConflictException(
                "This edge still has devices assigned to it. Unassign them first — deleting an edge "
                + "never unassigns what it reads, because what an edge reads must not change as a "
                + "side effect of something else.")
            : new ConfigurationConflictException($"Edge {edgeId} no longer exists.");
    }
}
