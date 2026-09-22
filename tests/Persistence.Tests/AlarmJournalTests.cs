using Npgsql;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The journal as the engine uses it (ADR-0013), over the application role, and the
/// historian heartbeat that bounds an outage.
/// </summary>
public sealed class AlarmJournalTests : IClassFixture<TestDatabase>
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database;

    public AlarmJournalTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task Every_field_of_an_event_survives_the_round_trip()
    {
        var seeded = await SeedAsync();
        var journal = new AlarmJournal(_database.ApplicationDataSource);
        var raised = Event(seeded, AlarmEventType.Raised) with
        {
            SourceTimeUtc = At.AddSeconds(-3),
            Limit = AlarmLimit.Low,
            LimitValue = 2.5,
            Value = 1.25,
            UnitSymbol = "bar",
            TagPath = "Site/Device/Tag",
            DetectedAfterRestart = true,
        };
        var shelved = Event(seeded, AlarmEventType.Shelved) with
        {
            Actor = new AlarmActor(Guid.NewGuid(), "ana"),
            ShelvedUntilUtc = At.AddHours(1),
            Reason = "maintenance",
        };

        await journal.AppendAsync(raised, CancellationToken.None);
        await journal.AppendAsync(shelved, CancellationToken.None);

        var read = (await journal.ReadOpenAsync(CancellationToken.None))
            .Where(e => e.OccurrenceId == seeded.OccurrenceId)
            .ToList();

        Assert.Equal([raised, shelved], read);
    }

    [RequiresDatabaseFact]
    public async Task Reading_open_occurrences_leaves_out_retired_ones_and_the_engines_own_events()
    {
        var open = await SeedAsync();
        var retired = await SeedAsync();
        var journal = new AlarmJournal(_database.ApplicationDataSource);

        await journal.AppendAsync(Event(open, AlarmEventType.Raised), CancellationToken.None);
        await journal.AppendAsync(Event(retired, AlarmEventType.Raised), CancellationToken.None);
        await journal.AppendAsync(Event(open, AlarmEventType.Cleared), CancellationToken.None);
        await journal.AppendAsync(Event(retired, AlarmEventType.Retired), CancellationToken.None);

        // Engine events have no occurrence. "Not retired" alone would match them, since no
        // Retired row shares a NULL occurrence — the three-valued trap (ADR-0013).
        await journal.AppendAsync(
            new AlarmEvent { Type = AlarmEventType.EvaluationStarted, RecordedAtUtc = At, GapUntilUtc = At },
            CancellationToken.None);

        var read = await journal.ReadOpenAsync(CancellationToken.None);

        Assert.Equal(
            [AlarmEventType.Raised, AlarmEventType.Cleared],
            read.Where(e => e.OccurrenceId == open.OccurrenceId).Select(e => e.Type));
        Assert.DoesNotContain(read, e => e.OccurrenceId == retired.OccurrenceId);
        Assert.DoesNotContain(read, e => e.OccurrenceId is null);
    }

    [RequiresDatabaseFact]
    public async Task The_last_ingestion_is_found_however_old_the_newest_sample_is()
    {
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        // Only samples far outside the narrow windows at first, so the answer has to come
        // from the widest one.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-60);
        var longAgoIngested = new DateTimeOffset(longAgo.Year, longAgo.Month, longAgo.Day, 0, 0, 0, TimeSpan.Zero);
        await historian.WriteAsync(
            [new HistorianSample(seeded.TagId, new TagValue.Numeric(1), longAgoIngested, longAgoIngested, Quality.Good)],
            CancellationToken.None);

        Assert.Equal(longAgoIngested, await historian.LastIngestedAtAsync(CancellationToken.None));

        var recent = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        await historian.WriteAsync(
            [new HistorianSample(seeded.TagId, new TagValue.Numeric(2), recent.AddMinutes(-1), recent, Quality.Good)],
            CancellationToken.None);

        Assert.Equal(recent, await historian.LastIngestedAtAsync(CancellationToken.None));
    }

    private static AlarmEvent Event(Seeded seeded, AlarmEventType type) => new()
    {
        Type = type,
        RecordedAtUtc = At,
        OccurrenceId = seeded.OccurrenceId,
        DefinitionId = seeded.DefinitionId,
        TagId = seeded.TagId,
        SiteId = seeded.SiteId,
    };

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        const string noSettings = "{}";

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Journal tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{seeded.SiteId}', '{tenantId}', 'Journal site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{deviceId}', '{seeded.SiteId}', 'Journal device', 'modbus-tcp', '{noSettings}', 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES ('{seeded.TagId}', '{deviceId}', 'Journal tag', 0, 'holding:0', false);
            INSERT INTO alarm_definition (id, tag_id, high_limit) VALUES ('{seeded.DefinitionId}', '{seeded.TagId}', 10);
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        return seeded;
    }

    private sealed record Seeded(Guid OccurrenceId, Guid DefinitionId, Guid TagId, Guid SiteId);
}
