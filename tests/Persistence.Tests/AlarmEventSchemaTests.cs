using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Migration 0009: the alarm journal's append-only posture, its shape, and the new
/// opt-in default for grants (ADR-0013).
/// </summary>
/// <remarks>
/// Everything about what the application may or may not do runs over
/// <see cref="TestDatabase.ApplicationDataSource"/>. A superuser bypasses grants, so the
/// same checks run as the migrator's role would prove nothing.
/// </remarks>
public sealed class AlarmEventSchemaTests : IClassFixture<TestDatabase>
{
    private const string InsufficientPrivilege = "42501";
    private const string CheckViolation = "23514";

    private readonly TestDatabase _database;

    public AlarmEventSchemaTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_application_can_append_to_and_read_the_journal()
    {
        var before = await CountAsync();

        await ExecuteAsApplicationAsync(EngineEvent("EvaluationStarted"));

        Assert.Equal(before + 1, await CountAsync());
    }

    [RequiresDatabaseFact]
    public Task An_update_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("UPDATE alarm_event SET reason = 'rewritten'");

    [RequiresDatabaseFact]
    public Task A_delete_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("DELETE FROM alarm_event");

    [RequiresDatabaseFact]
    public Task A_truncate_over_the_application_connection_is_refused() =>
        AssertRefusedAsync("TRUNCATE alarm_event");

    [RequiresDatabaseFact]
    public async Task A_table_created_after_this_migration_is_not_writable_by_default()
    {
        // Grants are opt-in from 0009 on: a new table the migration author forgot to think
        // about gets SELECT and INSERT, never UPDATE or DELETE.
        var table = $"created_later_{Guid.NewGuid():N}";
        await ExecutePrivilegedAsync($"CREATE TABLE {table} (x integer)");

        Assert.True(await ApplicationMayAsync(table, "SELECT"));
        Assert.True(await ApplicationMayAsync(table, "INSERT"));
        Assert.False(await ApplicationMayAsync(table, "UPDATE"));
        Assert.False(await ApplicationMayAsync(table, "DELETE"));

        // Tables that already existed keep what they had: configuration still edits.
        Assert.True(await ApplicationMayAsync("tag", "UPDATE"));
        Assert.True(await ApplicationMayAsync("alarm_definition", "UPDATE"));
    }

    [RequiresDatabaseFact]
    public async Task An_alarm_event_must_name_its_occurrence_and_site_and_an_engine_event_must_name_neither()
    {
        var alarm = await SeedAlarmAsync();

        // Well-formed rows of both kinds are accepted, so the refusals below are the
        // constraint at work rather than a row that could never be written.
        await ExecuteAsApplicationAsync(AlarmEvent("Raised", alarm));
        await ExecuteAsApplicationAsync(EngineEvent("EvaluationStopped"));

        await AssertCheckViolationAsync(AlarmEvent("Raised", alarm with { SiteId = null }));
        await AssertCheckViolationAsync(AlarmEvent("Raised", alarm with { OccurrenceId = null }));
        await AssertCheckViolationAsync(
            $"INSERT INTO alarm_event (event_type, occurrence_id, recorded_at) VALUES ('EvaluationStarted', '{Guid.NewGuid()}', now())");
    }

    [RequiresDatabaseFact]
    public async Task A_shelve_without_an_expiry_cannot_be_recorded()
    {
        var alarm = await SeedAlarmAsync();

        await ExecuteAsApplicationAsync(AlarmEvent("Shelved", alarm, shelvedUntil: "now() + interval '1 hour'"));
        await AssertCheckViolationAsync(AlarmEvent("Shelved", alarm));
    }

    [RequiresDatabaseFact]
    public async Task A_journal_gap_is_one_closed_window_with_its_losses_counted()
    {
        const string sevenMinutesAgo = "now() - interval '7 minutes'";

        // Well-formed: both ends of the window, and how many transitions it swallowed.
        await ExecuteAsApplicationAsync(JournalGap(from: sevenMinutesAgo, until: "now()", unrecorded: "3"));

        // Still open — it cannot be written until it has closed.
        await AssertCheckViolationAsync(JournalGap(from: sevenMinutesAgo, until: "NULL", unrecorded: "3"));

        // "There was a gap", with nothing a reader can act on.
        await AssertCheckViolationAsync(JournalGap(from: sevenMinutesAgo, until: "now()", unrecorded: "NULL"));

        // Nothing was lost, so there was no gap to describe.
        await AssertCheckViolationAsync(JournalGap(from: sevenMinutesAgo, until: "now()", unrecorded: "0"));

        // A count of lost transitions belongs to a JournalGap and to nothing else.
        await AssertCheckViolationAsync(
            "INSERT INTO alarm_event (event_type, recorded_at, unrecorded_transitions) VALUES ('EvaluationStarted', now(), 2)");
    }

    private static string JournalGap(string from, string until, string unrecorded) =>
        $"""
        INSERT INTO alarm_event (event_type, recorded_at, gap_from, gap_until, unrecorded_transitions)
        VALUES ('JournalGap', now(), {from}, {until}, {unrecorded})
        """;

    [RequiresDatabaseFact]
    public async Task An_unknown_event_type_or_an_actor_without_a_name_cannot_be_recorded()
    {
        var alarm = await SeedAlarmAsync();

        await AssertCheckViolationAsync(EngineEvent("SomethingElse"));

        // An acknowledgement naming a user id but not the name it read under: the journal
        // must stay readable to someone who cannot look the id up (ADR-0011).
        var named = ActorEvent(alarm, actorUsername: "'operator-one'");
        var unnamed = ActorEvent(alarm, actorUsername: "NULL");

        await ExecuteAsApplicationAsync(named);
        await AssertCheckViolationAsync(unnamed);
    }

    private static string ActorEvent(SeededAlarm alarm, string actorUsername) =>
        $"""
        INSERT INTO alarm_event (event_type, occurrence_id, definition_id, tag_id, site_id,
                                 actor_user_id, actor_username, recorded_at)
        VALUES ('Acknowledged', {Literal(alarm.OccurrenceId)}, '{alarm.DefinitionId}', '{alarm.TagId}',
                {Literal(alarm.SiteId)}, '{Guid.NewGuid()}', {actorUsername}, now())
        """;

    private async Task AssertRefusedAsync(string statement)
    {
        // A real row first, so a statement that affected nothing because the table was
        // empty cannot be mistaken for one that was refused.
        await ExecuteAsApplicationAsync(EngineEvent("EvaluationStarted"));
        var before = await CountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsApplicationAsync(statement));

        Assert.Equal(InsufficientPrivilege, refused.SqlState);
        Assert.Equal(before, await CountAsync());
    }

    private async Task AssertCheckViolationAsync(string statement)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsApplicationAsync(statement));
        Assert.Equal(CheckViolation, refused.SqlState);
    }

    private static string EngineEvent(string type) =>
        $"INSERT INTO alarm_event (event_type, recorded_at) VALUES ('{type}', now())";

    private static string AlarmEvent(string type, SeededAlarm alarm, string? shelvedUntil = null) =>
        $"""
        INSERT INTO alarm_event (event_type, occurrence_id, definition_id, tag_id, site_id, shelved_until, recorded_at)
        VALUES ('{type}', {Literal(alarm.OccurrenceId)}, '{alarm.DefinitionId}', '{alarm.TagId}',
                {Literal(alarm.SiteId)}, {shelvedUntil ?? "NULL"}, now())
        """;

    private static string Literal(Guid? id) => id is { } value ? $"'{value}'" : "NULL";

    /// <summary>A tenant, site, device, tag and alarm definition to hang events on.</summary>
    private async Task<SeededAlarm> SeedAlarmAsync()
    {
        var alarm = new SeededAlarm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        const string noSettings = "{}";

        await ExecutePrivilegedAsync(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Journal tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{alarm.SiteId}', '{tenantId}', 'Journal site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{deviceId}', '{alarm.SiteId}', 'Journal device', 'modbus-tcp', '{noSettings}', 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES ('{alarm.TagId}', '{deviceId}', 'Journal tag', 0, 'holding:0', false);
            INSERT INTO alarm_definition (id, tag_id, high_limit) VALUES ('{alarm.DefinitionId}', '{alarm.TagId}', 10);
            """);

        return alarm;
    }

    private async Task<bool> ApplicationMayAsync(string table, string privilege)
    {
        await using var command = _database.DataSource.CreateCommand(
            $"SELECT has_table_privilege('{ApplicationRole.Name}', '{table}', '{privilege}')");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync()
    {
        await using var command = _database.ApplicationDataSource.CreateCommand("SELECT count(*) FROM alarm_event");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsApplicationAsync(string sql)
    {
        await using var command = _database.ApplicationDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecutePrivilegedAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SeededAlarm(Guid? OccurrenceId, Guid DefinitionId, Guid TagId, Guid? SiteId);
}
