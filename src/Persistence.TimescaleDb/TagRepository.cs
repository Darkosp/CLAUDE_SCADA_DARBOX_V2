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
                $"""
                SELECT {TagColumns}
                FROM tag_active
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

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO tag (id, device_id, name, value_kind, unit_symbol, unit_dimension,
                                 unit_factor_to_si, unit_offset_to_si, source_address, is_writable,
                                 range_low, range_high)
                VALUES (@Id, @DeviceId, @Name, @ValueKind, @UnitSymbol, @UnitDimension,
                        @UnitFactorToSi, @UnitOffsetToSi, @SourceAddress, @IsWritable,
                        @RangeLow, @RangeHigh)
                """,
                ToParameters(tag),
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.TagTakenAsync(_dataSource, tag.Name, tag.DeviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(Tag tag, CancellationToken cancellationToken)
    {
        // device_id and value_kind are deliberately not updatable. A tag's kind is what
        // its history was written as (ADR-0003), and re-pointing a tag at another device
        // would silently reinterpret historian rows already keyed by this tag's ID.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tag
                SET name = @Name,
                    unit_symbol = @UnitSymbol,
                    unit_dimension = @UnitDimension,
                    unit_factor_to_si = @UnitFactorToSi,
                    unit_offset_to_si = @UnitOffsetToSi,
                    source_address = @SourceAddress,
                    is_writable = @IsWritable,
                    range_low = @RangeLow,
                    range_high = @RangeHigh
                WHERE id = @Id AND deleted_at IS NULL
                """,
                ToParameters(tag),
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.TagTakenAsync(_dataSource, tag.Name, tag.DeviceId, cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Tag {tag.Id} no longer exists.");
        }
    }

    public async Task DeleteAsync(Guid tagId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE tag SET deleted_at = now() WHERE id = @tagId AND deleted_at IS NULL",
            new { tagId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted == 0)
        {
            throw new ConfigurationConflictException($"Tag {tagId} no longer exists.");
        }
    }

    public async Task<TagIdentity?> FindIdentityIncludingDeletedAsync(
        Guid tagId,
        CancellationToken cancellationToken)
    {
        // Deliberately the base tables, not the active views — this is the one read that
        // must see past a deletion, so a trend for a device retired last year reads as
        // its name rather than a UUID (ADR-0009).
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<TagIdentity>(new CommandDefinition(
            """
            SELECT t.id AS tag_id,
                   t.name AS tag_name,
                   d.name AS device_name,
                   d.site_id AS site_id,
                   (t.deleted_at IS NOT NULL OR d.deleted_at IS NOT NULL) AS is_deleted
            FROM tag t
            JOIN device d ON d.id = t.device_id
            WHERE t.id = @tagId
            """,
            new { tagId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
        // Null for a tag that has declared no range, which is the default and the only thing the
        // migration can produce (ADR-0030).
        RangeLow = tag.Range?.Low,
        RangeHigh = tag.Range?.High,
    };
}
