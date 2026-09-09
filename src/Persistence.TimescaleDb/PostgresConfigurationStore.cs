using System.Text.Json;
using Npgsql;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Reads the configured hierarchy from PostgreSQL. Configuration lives in the same
/// engine as the time-series data (ADR-0006).
/// </summary>
public sealed class PostgresConfigurationStore : IConfigurationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresConfigurationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Tenant> GetTenantAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT id, name FROM tenant LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Every deployment holds exactly one tenant row (ADR-0004); its absence means
            // the instance was never provisioned, not that tenancy is optional.
            throw new InvalidOperationException("No tenant row found — the instance is not provisioned.");
        }

        return new Tenant { Id = reader.GetGuid(0), Name = reader.GetString(1) };
    }

    public async Task<IReadOnlyList<Site>> GetSitesAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT id, tenant_id, name, time_zone_id FROM site ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var sites = new List<Site>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sites.Add(new Site
            {
                Id = reader.GetGuid(0),
                TenantId = reader.GetGuid(1),
                Name = reader.GetString(2),
                TimeZoneId = reader.GetString(3),
            });
        }

        return sites;
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT id, site_id, name, driver_key, connection_settings, scan_interval_ms FROM device ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var devices = new List<Device>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var settingsJson = reader.GetString(4);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson)
                           ?? new Dictionary<string, string>();

            devices.Add(new Device
            {
                Id = reader.GetGuid(0),
                SiteId = reader.GetGuid(1),
                Name = reader.GetString(2),
                DriverKey = reader.GetString(3),
                ConnectionSettings = settings,
                ScanInterval = TimeSpan.FromMilliseconds(reader.GetInt32(5)),
            });
        }

        return devices;
    }

    public async Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT id, device_id, name, value_kind, unit_symbol, unit_dimension,
                   unit_factor_to_si, unit_offset_to_si, source_address, is_writable
            FROM tag
            ORDER BY name
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var tags = new List<Tag>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // A unit is only reconstituted when the dimension and factor are present:
            // a bare symbol is not a unit (ADR-0005).
            UnitOfMeasure? unit = reader.IsDBNull(5) || reader.IsDBNull(6)
                ? null
                : new UnitOfMeasure(
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    (Dimension)reader.GetInt16(5),
                    reader.GetDouble(6),
                    reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7));

            tags.Add(new Tag
            {
                Id = reader.GetGuid(0),
                DeviceId = reader.GetGuid(1),
                Name = reader.GetString(2),
                ValueKind = (TagValueKind)reader.GetInt16(3),
                Unit = unit,
                SourceAddress = reader.GetString(8),
                IsWritable = reader.GetBoolean(9),
            });
        }

        return tags;
    }
}
