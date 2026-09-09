using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Provisions a minimal hierarchy on an empty database so Phase 1 has something to run
/// against. Configuring devices and tags through the UI is Phase 2 scope; until then a
/// deployment needs one deterministic starting point.
/// </summary>
/// <remarks>
/// The identifiers are fixed rather than generated. A tag's ID is its identity and all
/// history is keyed by it (ADR-0001), so re-running against an existing database must
/// address the same tags, not create new ones with new IDs.
/// </remarks>
public static class DemoConfigurationSeeder
{
    public static readonly Guid TenantId = new("0f7a1b2c-0000-4000-8000-000000000001");
    public static readonly Guid SiteId = new("0f7a1b2c-0000-4000-8000-000000000002");

    /// <summary>
    /// A second site with no devices of its own. ADR-0001 requires every demo dataset to
    /// contain at least two sites, so that multi-site handling is exercised from the
    /// start rather than discovered to be broken at the first two-site deployment.
    /// </summary>
    public static readonly Guid SecondSiteId = new("0f7a1b2c-0000-4000-8000-000000000006");

    /// <summary>A folder in the second site, so the browse tree has nesting to show.</summary>
    public static readonly Guid SecondSiteFolderId = new("0f7a1b2c-0000-4000-8000-000000000007");
    public static readonly Guid DeviceId = new("0f7a1b2c-0000-4000-8000-000000000003");
    public static readonly Guid DischargePressureTagId = new("0f7a1b2c-0000-4000-8000-000000000004");
    public static readonly Guid PumpRunningTagId = new("0f7a1b2c-0000-4000-8000-000000000005");

    /// <summary>
    /// Inserts the demo hierarchy if no tenant exists yet. Does nothing on a database
    /// that has already been provisioned.
    /// </summary>
    public static async Task SeedIfEmptyAsync(
        NpgsqlDataSource dataSource,
        string modbusHost,
        int modbusPort,
        CancellationToken cancellationToken)
    {
        await using (var probe = dataSource.CreateCommand("SELECT count(*) FROM tenant"))
        {
            var existing = (long)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (existing > 0)
            {
                return;
            }
        }

        var connectionSettings = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["host"] = modbusHost,
            ["port"] = modbusPort.ToString(),
            ["unitId"] = "1",
        });

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO tenant (id, name) VALUES (@id, @name);

            INSERT INTO site (id, tenant_id, name, time_zone_id)
            VALUES (@site_id, @id, 'Skopje', 'Europe/Skopje'),
                   (@second_site_id, @id, 'Bitola', 'Europe/Skopje');

            INSERT INTO folder (id, site_id, parent_folder_id, name)
            VALUES (@second_site_folder_id, @second_site_id, NULL, 'Water Works');

            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES (@device_id, @site_id, 'Pump House', 'modbus-tcp', @settings, 1000);

            INSERT INTO tag (id, device_id, name, value_kind, unit_symbol, unit_dimension,
                             unit_factor_to_si, unit_offset_to_si, source_address, is_writable)
            VALUES (@pressure_id, @device_id, 'Discharge Pressure', 0, 'bar', 1, 100000, 0, 'holding:0?scale=0.01', false),
                   (@running_id, @device_id, 'Pump Running', 1, NULL, NULL, NULL, NULL, 'coil:0', false);
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", TenantId);
            command.Parameters.AddWithValue("name", "Darbo");
            command.Parameters.AddWithValue("site_id", SiteId);
            command.Parameters.AddWithValue("second_site_id", SecondSiteId);
            command.Parameters.AddWithValue("second_site_folder_id", SecondSiteFolderId);
            command.Parameters.AddWithValue("device_id", DeviceId);
            command.Parameters.Add(new NpgsqlParameter("settings", NpgsqlDbType.Jsonb) { Value = connectionSettings });
            command.Parameters.AddWithValue("pressure_id", DischargePressureTagId);
            command.Parameters.AddWithValue("running_id", PumpRunningTagId);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
