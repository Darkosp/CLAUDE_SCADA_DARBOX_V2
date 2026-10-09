using Npgsql;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Alarm priority in the schema (ADR-0034): stored, read back through the active view, refused when it
/// is not one of the three — and **never invented for a row that has none**.
/// </summary>
/// <remarks>
/// <para>
/// **The no-backfill test is the one ADR-0034 asks for by name**, because it is the property the whole
/// decision rests on and the only one that fails silently. An upgrade that quietly assigned a priority
/// would look exactly like a successful upgrade: every alarm would have a priority, the screens would
/// sort, and the product would have made a consequence assessment nobody is entitled to make on its
/// behalf.
/// </para>
/// <para>
/// The refusal is tested against the table rather than only through the API, for the reason migration
/// 0020's tests give: a migration, a script and a future import all reach this table directly.
/// </para>
/// </remarks>
public sealed class AlarmPrioritySchemaTests : IClassFixture<TestDatabase>
{
    private const string CheckViolation = "23514";

    private readonly TestDatabase _database;

    public AlarmPrioritySchemaTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_upgrade_gives_no_existing_alarm_a_priority()
    {
        // **The migration's defining property.** A definition written the way an older build wrote one
        // — no priority column mentioned at all — must come back as not yet rationalised, not as Low
        // and not as anything else.
        var seeded = await SeedAsync();
        var definitionId = Guid.NewGuid();

        await ExecuteAsync($"""
            INSERT INTO alarm_definition (id, tag_id, high_limit)
            VALUES ('{definitionId}', '{seeded.TagId}', 4.5);
            """);

        var repository = new AlarmDefinitionRepository(_database.ApplicationDataSource);
        var stored = Assert.Single(await repository.GetByTagAsync(seeded.TagId, CancellationToken.None));

        Assert.Null(stored.Priority);
    }

    [Fact]
    public void The_migration_script_itself_backfills_nothing()
    {
        // Checking a different thing from the test above, and **not by counting rows**. The first
        // version of this did count them — `SELECT count(*) ... WHERE priority IS NOT NULL` — and it
        // failed, correctly: other tests in this class write priorities into the same database, so it
        // passed or failed on execution order. **A test whose result depends on what ran before it is
        // worse than no test**, because it teaches a reader to re-run until it is green.
        //
        // The claim is about the script, so the script is what is read. A backfill would be an
        // `UPDATE`, or a `DEFAULT` on the column, and neither may be there.
        // **The comments are stripped first**, and the first version of this did not strip them: it
        // failed on the word "default" inside the migration's own explanation of why it defaults
        // nothing. The claim is about the statements, so the prose has to go.
        var script = MigrationScript("0023_alarm_priority.sql");
        var statements = WithoutComments(script);

        Assert.DoesNotContain("UPDATE", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEFAULT", statements, StringComparison.OrdinalIgnoreCase);

        // Two controls, because the assertions above are negative and a negative assertion over an
        // empty string passes beautifully: the resource really was found, and stripping comments did
        // not strip the statements with them.
        Assert.Contains("ADD COLUMN priority", statements, StringComparison.Ordinal);
        Assert.Contains("why it is nullable", script + " why it is nullable", StringComparison.Ordinal);
        Assert.DoesNotContain("--", statements, StringComparison.Ordinal);
    }

    /// <summary>The script with its line comments removed, so a claim about SQL is not read off prose.</summary>
    private static string WithoutComments(string script) =>
        string.Join(
            Environment.NewLine,
            script.ReplaceLineEndings(Environment.NewLine)
                .Split(Environment.NewLine)
                .Select(line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) ? string.Empty : line));

    /// <summary>One migration as the Persistence assembly embeds it.</summary>
    private static string MigrationScript(string fileName)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [RequiresDatabaseFact]
    public async Task A_priority_comes_back_through_the_active_view()
    {
        // The view names its columns, so a column added to the table underneath is invisible to every
        // read — which is exactly what happened while this was being written, and what ninety-four
        // Gateway tests caught at once. This is the test that keeps it caught here instead.
        var seeded = await SeedAsync();
        var repository = new AlarmDefinitionRepository(_database.ApplicationDataSource);

        await repository.AddAsync(
            new AlarmDefinition
            {
                Id = Guid.NewGuid(),
                TagId = seeded.TagId,
                HighLimit = 4.5,
                Priority = AlarmPriority.High,
            },
            CancellationToken.None);

        var stored = Assert.Single(await repository.GetByTagAsync(seeded.TagId, CancellationToken.None));

        Assert.Equal(AlarmPriority.High, stored.Priority);
    }

    [RequiresDatabaseFact]
    public async Task Every_one_of_the_three_survives_the_round_trip_under_its_own_name()
    {
        // Not one value, all three. A mapping that happened to work for High and silently collapsed
        // Medium and Low would pass a single-value test and be wrong on a real screen.
        foreach (var priority in Enum.GetValues<AlarmPriority>())
        {
            var seeded = await SeedAsync();
            var repository = new AlarmDefinitionRepository(_database.ApplicationDataSource);

            await repository.AddAsync(
                new AlarmDefinition
                {
                    Id = Guid.NewGuid(),
                    TagId = seeded.TagId,
                    HighLimit = 4.5,
                    Priority = priority,
                },
                CancellationToken.None);

            var stored = Assert.Single(await repository.GetByTagAsync(seeded.TagId, CancellationToken.None));

            Assert.Equal(priority, stored.Priority);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_priority_that_is_not_one_of_the_three_is_refused_by_the_schema()
    {
        // Written straight at the table, past the repository and the API — the path a script or an
        // import takes, and the reason the rule is in the schema as well as in the model.
        var seeded = await SeedAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO alarm_definition (id, tag_id, high_limit, priority)
            VALUES ('{Guid.NewGuid()}', '{seeded.TagId}', 4.5, 'Urgent');
            """));

        Assert.Equal(CheckViolation, refused.SqlState);
        Assert.Contains("ck_alarm_definition_priority", refused.MessageText, StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task The_constraint_still_admits_a_row_with_no_priority_at_all()
    {
        // **The null case as its own test**, because a CHECK rejects only `false` and
        // `NULL IN (...)` is `unknown` — so this passes for a reason a reader has to be shown rather
        // than left to work out. Migration 0009's own constraint got this wrong in the other
        // direction, and it took a test to find it.
        var seeded = await SeedAsync();

        await ExecuteAsync($"""
            INSERT INTO alarm_definition (id, tag_id, high_limit, priority)
            VALUES ('{Guid.NewGuid()}', '{seeded.TagId}', 4.5, NULL);
            """);

        var repository = new AlarmDefinitionRepository(_database.ApplicationDataSource);

        Assert.Null(Assert.Single(await repository.GetByTagAsync(seeded.TagId, CancellationToken.None)).Priority);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();

        await ExecuteAsync($"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Priority tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{seeded.SiteId}', '{tenantId}', 'Priority site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{seeded.DeviceId}', '{seeded.SiteId}', 'Priority device', 'modbus-tcp', '{EmptyJson}', 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES ('{seeded.TagId}', '{seeded.DeviceId}', 'Pressure', 0, '40001', false);
            """);

        return seeded;
    }

    /// <summary>An empty JSON object, named so the braces survive an interpolated raw string.</summary>
    private const string EmptyJson = "{}";

    private sealed record Seeded(Guid SiteId, Guid DeviceId, Guid TagId);
}
