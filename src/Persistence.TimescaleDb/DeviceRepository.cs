using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Device configuration storage (ADR-0008).</summary>
public sealed class DeviceRepository : IDeviceRepository
{
    private const string SelectColumns = """
        SELECT id, site_id, folder_id, name, driver_key,
               connection_settings::text AS connection_settings, scan_interval_ms
        FROM device
        """;

    private readonly NpgsqlDataSource _dataSource;

    static DeviceRepository() => DapperConfiguration.Ensure();

    public DeviceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeviceRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE site_id = @siteId ORDER BY name",
                new { siteId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<DeviceRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @deviceId",
                new { deviceId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task AddAsync(Device device, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO device (id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES (@Id, @SiteId, @FolderId, @Name, @DriverKey, @ConnectionSettings::jsonb, @ScanIntervalMs)
            """,
            ToParameters(device),
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(Device device, CancellationToken cancellationToken)
    {
        // site_id is deliberately not updatable. It is the tenant and security scope
        // (ADR-0004), not a placement field: moving a device between sites would change
        // who can see its history, which is not something an edit form should do.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE device
            SET folder_id = @FolderId,
                name = @Name,
                driver_key = @DriverKey,
                connection_settings = @ConnectionSettings::jsonb,
                scan_interval_ms = @ScanIntervalMs
            WHERE id = @Id
            """,
            ToParameters(device),
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Device {device.Id} no longer exists.");
        }
    }

    private static object ToParameters(Device device) => new
    {
        device.Id,
        device.SiteId,
        device.FolderId,
        device.Name,
        device.DriverKey,
        ConnectionSettings = ConnectionSettingsJson.Serialize(device.ConnectionSettings),
        ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds,
    };
}
