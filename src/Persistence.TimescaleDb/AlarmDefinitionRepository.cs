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
                $"SELECT {AlarmDefinitionColumns} FROM alarm_definition_active WHERE tag_id = @tagId",
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
            INSERT INTO alarm_definition (id, tag_id, high_limit, low_limit, on_delay_seconds, deadband, priority)
            VALUES (@Id, @TagId, @HighLimit, @LowLimit, @OnDelaySeconds, @Deadband, @Priority)
            """,
            Parameters(definition),
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
            SET high_limit = @HighLimit,
                low_limit = @LowLimit,
                on_delay_seconds = @OnDelaySeconds,
                deadband = @Deadband,
                priority = @Priority
            WHERE id = @Id AND deleted_at IS NULL
            """,
            Parameters(definition),
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

    /// <summary>
    /// A definition as the columns hold it.
    /// </summary>
    /// <remarks>
    /// The conversion from <see cref="TimeSpan"/> to seconds lives here rather than in the domain,
    /// for the reason ADR-0008 gives: the domain has no business knowing what a column looks like.
    /// <c>null</c> survives as <c>null</c> in both directions, which is the property that matters —
    /// an alarm with no on-delay must not acquire one of zero.
    /// </remarks>
    private static object Parameters(AlarmDefinition definition) => new
    {
        definition.Id,
        definition.TagId,
        definition.HighLimit,
        definition.LowLimit,
        OnDelaySeconds = definition.OnDelaySeconds?.TotalSeconds,
        definition.Deadband,

        // As its name, so the column reads as what it means and the CHECK can enforce the three.
        // Null stays null: not yet rationalised is a state, and storing it as any of the three would
        // be the product making an assessment it is not entitled to make (ADR-0034 §2).
        Priority = definition.Priority?.ToString(),
    };
}
