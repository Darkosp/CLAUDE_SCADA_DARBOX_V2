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
    /// <summary>
    /// The advisory lock that makes this seeder one-at-a-time.
    /// </summary>
    /// <remarks>
    /// "SCAD_SE" as bytes — the same convention <see cref="MigrationLock.Key"/> uses, which is
    /// "SCADA_MG", so the two read in a lock listing rather than being numbers nobody can place.
    /// They are deliberately different values: a seeder starting while a migration runs must wait
    /// for the migration rather than take a lock of its own beside it.
    ///
    /// Eight bytes exactly, because a <c>long</c> is what Postgres takes as a lock key and a ninth
    /// byte does not fit: the first attempt at this was ten characters and did not compile.
    /// </remarks>
    private const long LockKey = 0x5343_4144_5F53_4545;

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
        var connectionSettings = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["host"] = modbusHost,
            ["port"] = modbusPort.ToString(),
            ["unitId"] = "1",
        });

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One seeder at a time, and the lock is taken BEFORE the question is asked.
        //
        // This used to ask "is there a tenant yet" on its own connection and then seed on another,
        // which is a read followed by a write with nothing between them: two processes starting at
        // once against an empty database both read zero and both inserted the whole demo dataset --
        // two tenants, two Skopjes, two of every device. It was found by this project's own test
        // host, which starts the app once per test in parallel against one database, and it is
        // exactly the shape a rolling start or a misconfigured Kubernetes update would produce.
        //
        // Transaction-scoped rather than session-scoped: the lock ends when this transaction does,
        // so a process that dies mid-seed releases it without anyone cleaning up. The second seeder
        // waits here, and then asks its question after the first has committed -- which is what
        // makes the answer true rather than merely earlier.
        //
        // Removing this is not a silent change: the test for it fails with `23505: duplicate key
        // value violates unique constraint "tenant_pkey"`, which is what two seeders colliding on
        // fixed ids looks like.
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            take.Parameters.AddWithValue("key", LockKey);
            await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var probe = new NpgsqlCommand("SELECT count(*) FROM tenant", connection, transaction))
        {
            var existing = (long)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (existing > 0)
            {
                // Nothing to do, and the lock is released by the rollback this leaves behind. An
                // empty transaction is cheaper to reason about than a conditional commit.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

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

        // After the transaction, not inside it: a screen is not part of the demo dataset that a
        // caller asked for, and the seed is idempotent by asking whether the Site already has one
        // (ADR-0024 §7). A second run of the seeder therefore leaves the operator's own screens
        // alone, which is the behaviour that matters on an upgrade.
        //
        // Driven by the Sites the transaction just wrote rather than by a list written out here.
        // It used to name Skopje and Bitola one call each, which meant the rule this exists for --
        // "a new Site is not born empty" (ADR-0024 §5) -- held only for as long as nobody added a
        // third Site to the statement above. A Site in this list and not in that one is a Site with
        // no screen, and nothing would have said so.
        var screens = new ScreenRepository(dataSource);

        var toSeed = new List<(Guid Id, string Name)>();

        await using (var reading = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            "SELECT id, name FROM site WHERE tenant_id = @tenant_id ORDER BY name", reading))
        {
            command.Parameters.AddWithValue("tenant_id", TenantId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                toSeed.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var site in toSeed)
        {
            await screens.SeedForSiteAsync(TenantId, site.Id, site.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives every one of <paramref name="sites"/> an Overview screen, if it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from the seeder so that the rule it exists for can be tested without a race: **a new
    /// Site is not born empty** (ADR-0024 §5). The seeder itself runs against an empty tenant and is
    /// therefore hard to aim at one more Site; this takes the list.
    /// </para>
    /// <para>
    /// Calling it twice is what makes it safe on an upgrade — <c>SeedForSiteAsync</c> asks whether the
    /// Site already has a screen before writing one, so an operator's own screens are left alone.
    /// </para>
    /// </remarks>
    public static async Task SeedScreensAsync(
        NpgsqlDataSource dataSource,
        Guid tenantId,
        IReadOnlyList<(Guid Id, string Name)> sites,
        CancellationToken cancellationToken)
    {
        var screens = new ScreenRepository(dataSource);

        foreach (var site in sites)
        {
            await screens.SeedForSiteAsync(tenantId, site.Id, site.Name, cancellationToken).ConfigureAwait(false);
        }
    }
}
