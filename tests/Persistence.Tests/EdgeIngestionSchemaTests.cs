using Npgsql;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Migration 0011 over the application role (ADR-0017): pushed samples stored once per (tag,
/// source time), and the two source events — a loss and a clock skew — that say what they must,
/// with every null case refused explicitly rather than let through as "unknown".
/// </summary>
public sealed class EdgeIngestionSchemaTests : IClassFixture<TestDatabase>
{
    private const string CheckViolation = "23514";
    private static readonly DateTimeOffset At = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database;

    public EdgeIngestionSchemaTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_pushed_batch_written_twice_is_stored_once()
    {
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);
        var batch = Samples(seeded.TagId, At, 10);

        Assert.Equal(10, await historian.WriteOnceAsync(batch, CancellationToken.None));
        Assert.Equal(0, await historian.WriteOnceAsync(batch, CancellationToken.None));

        // Re-delivered with a later arrival and even a different value: still the same (tag, time),
        // so the first stays and the second is not stored — not merged, not an error.
        var replayed = batch.Select(s => s with { IngestedAtUtc = s.IngestedAtUtc.AddMinutes(5), Value = new TagValue.Numeric(-1) }).ToList();
        Assert.Equal(0, await historian.WriteOnceAsync(replayed, CancellationToken.None));

        var stored = await historian.ReadAsync(seeded.TagId, At.AddMinutes(-1), At.AddMinutes(1), CancellationToken.None);
        Assert.Equal(batch.Select(s => (s.SourceTimestampUtc, s.Value, s.IngestedAtUtc)), stored.Select(s => (s.SourceTimestampUtc, s.Value, s.IngestedAtUtc)));
    }

    [RequiresDatabaseFact]
    public async Task A_duplicate_inside_one_batch_is_stored_once_and_new_samples_still_are()
    {
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);
        var one = Samples(seeded.TagId, At, 1)[0];
        var next = Samples(seeded.TagId, At.AddSeconds(1), 1)[0];

        Assert.Equal(2, await historian.WriteOnceAsync([one, one, next], CancellationToken.None));

        var stored = await historian.ReadAsync(seeded.TagId, At.AddMinutes(-1), At.AddMinutes(1), CancellationToken.None);
        Assert.Equal([one.SourceTimestampUtc, next.SourceTimestampUtc], stored.Select(s => s.SourceTimestampUtc));
    }

    [RequiresDatabaseFact]
    public async Task The_polled_path_is_untouched()
    {
        // The key is scoped to pushed samples. A polled OPC UA tag whose value has not changed is
        // read with the same source time scan after scan, and is stored as it always was.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);
        var one = Samples(seeded.TagId, At, 1);

        await historian.WriteAsync(one, CancellationToken.None);
        await historian.WriteAsync(one, CancellationToken.None);

        Assert.Equal(2, (await historian.ReadAsync(seeded.TagId, At.AddMinutes(-1), At.AddMinutes(1), CancellationToken.None)).Count);
    }

    [RequiresDatabaseFact]
    public async Task A_loss_is_recorded_once_by_its_id_and_reads_back_whole()
    {
        var seeded = await SeedAsync();
        var journal = new AlarmJournal(_database.ApplicationDataSource);
        var loss = Loss(seeded);

        Assert.True(await journal.AppendLossOnceAsync(loss, CancellationToken.None));
        Assert.False(await journal.AppendLossOnceAsync(loss with { RecordedAtUtc = At.AddMinutes(1) }, CancellationToken.None));

        var read = (await journal.ReadHistoryAsync(new AlarmJournalQuery([seeded.SiteId]), CancellationToken.None))
            .Where(e => e.DeviceId == seeded.DeviceId)
            .ToList();
        Assert.Equal([loss], read);

        // Site-scoped like an alarm event: a reader of another Site does not see it.
        Assert.DoesNotContain(
            await journal.ReadHistoryAsync(new AlarmJournalQuery([Guid.NewGuid()]), CancellationToken.None),
            e => e.DeviceId == seeded.DeviceId);
    }

    [RequiresDatabaseFact]
    public async Task A_loss_that_does_not_say_how_much_or_when_is_refused()
    {
        var seeded = await SeedAsync();

        // Each of these is the null case of a comparison a CHECK would otherwise read as unknown
        // and let through.
        await AssertRefusedAsync(Loss(seeded) with { LostSamples = null });
        await AssertRefusedAsync(Loss(seeded) with { LostSamples = 0 });
        await AssertRefusedAsync(Loss(seeded) with { GapFromUtc = null });
        await AssertRefusedAsync(Loss(seeded) with { GapUntilUtc = null });
        await AssertRefusedAsync(Loss(seeded) with { GapFromUtc = At.AddHours(1) });

        // And it belongs to a device on a Site, never to nobody.
        await AssertRefusedAsync(Loss(seeded) with { DeviceId = null });
        await AssertRefusedAsync(Loss(seeded) with { SiteId = null });
    }

    [RequiresDatabaseFact]
    public async Task A_loss_without_an_id_is_refused()
    {
        var seeded = await SeedAsync();

        // Through the plain append, which the id-keyed one would refuse before the database did.
        await AssertRefusedAsync(Loss(seeded) with { LossId = null }, once: false);
    }

    [RequiresDatabaseFact]
    public async Task A_clock_skew_that_does_not_say_by_how_much_or_what_the_source_read_is_refused()
    {
        var seeded = await SeedAsync();
        var journal = new AlarmJournal(_database.ApplicationDataSource);

        await journal.AppendAsync(Skew(seeded), CancellationToken.None);

        await AssertRefusedAsync(Skew(seeded) with { ClockSkewSeconds = null }, once: false);
        await AssertRefusedAsync(Skew(seeded) with { SourceTimeUtc = null }, once: false);

        // Only a skew carries a skew, and only a loss a count.
        await AssertRefusedAsync(Skew(seeded) with { LostSamples = 5 }, once: false);
        await AssertRefusedAsync(Loss(seeded) with { ClockSkewSeconds = 600 });
    }

    private async Task AssertRefusedAsync(AlarmEvent entry, bool once = true)
    {
        var journal = new AlarmJournal(_database.ApplicationDataSource);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => once
            ? journal.AppendLossOnceAsync(entry, CancellationToken.None)
            : journal.AppendAsync(entry, CancellationToken.None));

        Assert.Equal(CheckViolation, refused.SqlState);
    }

    private static AlarmEvent Loss(Seeded seeded) => new()
    {
        Type = AlarmEventType.SamplesLost,
        RecordedAtUtc = At,
        SiteId = seeded.SiteId,
        DeviceId = seeded.DeviceId,
        TagPath = "Edge site/Edge device",
        GapFromUtc = At.AddHours(-5),
        GapUntilUtc = At.AddHours(-4),
        LostSamples = 1200,
        LossId = Guid.NewGuid(),
    };

    private static AlarmEvent Skew(Seeded seeded) => new()
    {
        Type = AlarmEventType.SourceClockSkew,
        RecordedAtUtc = At,
        SourceTimeUtc = At.AddMinutes(10),
        SiteId = seeded.SiteId,
        DeviceId = seeded.DeviceId,
        TagPath = "Edge site/Edge device",
        ClockSkewSeconds = 600,
    };

    private static List<HistorianSample> Samples(Guid tagId, DateTimeOffset from, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new HistorianSample(tagId, new TagValue.Numeric(i), from.AddSeconds(i), from.AddMinutes(1), Quality.Good))
            .ToList();

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Edge tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{seeded.SiteId}', '{tenantId}', 'Edge site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{seeded.DeviceId}', '{seeded.SiteId}', 'Edge device', 'mqtt', '{"{}"}', 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES ('{seeded.TagId}', '{seeded.DeviceId}', 'Edge tag', 0, 'pressure', false);
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        return seeded;
    }

    private sealed record Seeded(Guid SiteId, Guid DeviceId, Guid TagId);
}
