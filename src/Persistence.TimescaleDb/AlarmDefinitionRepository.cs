using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Alarm threshold storage (ADR-0008).</summary>
/// <remarks>
/// Reads go through <c>alarm_definition_active</c>, which also requires the watched tag
/// to be live — a deleted tag must not leave a threshold behind that still evaluates.
/// </remarks>
public sealed class AlarmDefinitionRepository : IAlarmDefinitionRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static AlarmDefinitionRepository() => DapperConfiguration.Ensure();

    public AlarmDefinitionRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AlarmDefinition>> GetByTagAsync(
        Guid tagId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AlarmDefinitionRow>(
            new CommandDefinition(
                "SELECT id, tag_id, high_limit, low_limit FROM alarm_definition_active WHERE tag_id = @tagId",
                new { tagId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(AlarmDefinition definition, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO alarm_definition (id, tag_id, high_limit, low_limit)
            VALUES (@Id, @TagId, @HighLimit, @LowLimit)
            """,
            definition,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(AlarmDefinition definition, CancellationToken cancellationToken)
    {
        // tag_id is not updatable: pointing a threshold at a different tag would silently
        // reinterpret it rather than edit it.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE alarm_definition
            SET high_limit = @HighLimit, low_limit = @LowLimit
            WHERE id = @Id AND deleted_at IS NULL
            """,
            definition,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Alarm definition {definition.Id} no longer exists.");
        }
    }

    public async Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE alarm_definition SET deleted_at = now() WHERE id = @definitionId AND deleted_at IS NULL",
            new { definitionId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted == 0)
        {
            throw new ConfigurationConflictException($"Alarm definition {definitionId} no longer exists.");
        }
    }
}
