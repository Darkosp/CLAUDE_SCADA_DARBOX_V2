using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Tag configuration storage (ADR-0008).</summary>
public sealed class TagRepository : ITagRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static TagRepository() => DapperConfiguration.Ensure();

    public TagRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Tag>> GetByDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TagRow>(
            new CommandDefinition(
                """
                SELECT id, device_id, name, value_kind, unit_symbol, unit_dimension,
                       unit_factor_to_si, unit_offset_to_si, source_address, is_writable
                FROM tag
                WHERE device_id = @deviceId
                ORDER BY name
                """,
                new { deviceId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Tag tag, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tag (id, device_id, name, value_kind, unit_symbol, unit_dimension,
                             unit_factor_to_si, unit_offset_to_si, source_address, is_writable)
            VALUES (@Id, @DeviceId, @Name, @ValueKind, @UnitSymbol, @UnitDimension,
                    @UnitFactorToSi, @UnitOffsetToSi, @SourceAddress, @IsWritable)
            """,
            ToParameters(tag),
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(Tag tag, CancellationToken cancellationToken)
    {
        // device_id and value_kind are deliberately not updatable. A tag's kind is what
        // its history was written as (ADR-0003), and re-pointing a tag at another device
        // would silently reinterpret historian rows already keyed by this tag's ID.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tag
            SET name = @Name,
                unit_symbol = @UnitSymbol,
                unit_dimension = @UnitDimension,
                unit_factor_to_si = @UnitFactorToSi,
                unit_offset_to_si = @UnitOffsetToSi,
                source_address = @SourceAddress,
                is_writable = @IsWritable
            WHERE id = @Id
            """,
            ToParameters(tag),
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Tag {tag.Id} no longer exists.");
        }
    }

    private static object ToParameters(Tag tag) => new
    {
        tag.Id,
        tag.DeviceId,
        tag.Name,
        ValueKind = (short)tag.ValueKind,
        UnitSymbol = tag.Unit?.Symbol,
        UnitDimension = tag.Unit is null ? (short?)null : (short)tag.Unit.Dimension,
        UnitFactorToSi = tag.Unit?.FactorToSi,
        UnitOffsetToSi = tag.Unit?.OffsetToSi,
        tag.SourceAddress,
        tag.IsWritable,
    };
}
