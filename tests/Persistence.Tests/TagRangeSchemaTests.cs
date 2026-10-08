using Npgsql;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The declared range in the schema (ADR-0030): stored, read back through the active view, and refused
/// when it is not a range.
/// </summary>
/// <remarks>
/// **The refusal is tested here rather than only in the API**, because the API is not the only thing that
/// writes this table — a migration, a script and a future import all reach it directly, and migration
/// 0020 therefore puts the rule in the schema too.
///
/// The null case is its own test for the reason this repository keeps repeating: Postgres rejects only a
/// CHECK that evaluates to **false**, so `range_low &lt; range_high` on its own would accept a row with
/// one end missing — the comparison is `unknown`, and `unknown` passes. That is the constraint pair's
/// whole purpose.
/// </remarks>
public sealed class TagRangeSchemaTests : IClassFixture<TestDatabase>
{
    private const string CheckViolation = "23514";
    private static readonly DateTimeOffset At = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database;

    public TagRangeSchemaTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_declared_range_comes_back_through_the_active_view()
    {
        var seeded = await SeedAsync();
        var repository = new TagRepository(_database.ApplicationDataSource);

        await repository.AddAsync(
            Tag(seeded, range: new TagRange(0, 100)),
            CancellationToken.None);

        var stored = Assert.Single(await repository.GetByDeviceAsync(seeded.DeviceId, CancellationToken.None));

        Assert.Equal(0, stored.Range?.Low);
        Assert.Equal(100, stored.Range?.High);
    }

    [RequiresDatabaseFact]
    public async Task A_tag_with_no_range_reads_back_with_none()
    {
        // The upgrade case: every row the migration touches gets null, and null has to survive the round
        // trip as "nothing declared" rather than becoming a default span.
        var seeded = await SeedAsync();
        var repository = new TagRepository(_database.ApplicationDataSource);

        await repository.AddAsync(Tag(seeded, range: null), CancellationToken.None);

        var stored = Assert.Single(await repository.GetByDeviceAsync(seeded.DeviceId, CancellationToken.None));

        Assert.Null(stored.Range);
    }

    [RequiresDatabaseFact]
    public async Task Half_a_range_is_refused_by_the_schema()
    {
        var seeded = await SeedAsync();

        // Written straight at the table, past the repository and the API, because that is the path a
        // migration or a script would take — and it is the path the paired CHECK exists for.
        await AssertRefusedAsync(seeded, "range_low", 10);
        await AssertRefusedAsync(seeded, "range_high", 10);
    }

    [RequiresDatabaseFact]
    public async Task A_range_whose_ends_are_the_wrong_way_round_is_refused_by_the_schema()
    {
        var seeded = await SeedAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(seeded, "range_low, range_high", "10, 5"));

        Assert.Equal(CheckViolation, refused.SqlState);
        Assert.Contains("ck_tag_range_ordered", refused.MessageText);
    }

    private async Task AssertRefusedAsync(Seeded seeded, string column, double value)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(seeded, column, value.ToString()));

        Assert.Equal(CheckViolation, refused.SqlState);
        Assert.Contains("ck_tag_range_paired", refused.MessageText);
    }

    private async Task InsertAsync(Seeded seeded, string columns, string values)
    {
        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable, {columns})
            VALUES ('{Guid.NewGuid()}', '{seeded.DeviceId}', 'Half ranged', 0, '40001', false, {values});
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Tag Tag(Seeded seeded, TagRange? range) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = seeded.DeviceId,
        Name = "Ranged",
        ValueKind = TagValueKind.Numeric,
        SourceAddress = "40001",
        IsWritable = false,
        Range = range,
    };

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Range tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{seeded.SiteId}', '{tenantId}', 'Range site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{seeded.DeviceId}', '{seeded.SiteId}', 'Range device', 'modbus-tcp', '{"{}"}', 1000);
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        return seeded;
    }

    private sealed record Seeded(Guid SiteId, Guid DeviceId);
}
