using Npgsql;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The bucketed read (ADR-0029): the window reduced where the readings are, rather than in a browser
/// that has already been sent all of them.
/// </summary>
/// <remarks>
/// **This is where the rule that used to live in the client is tested now.** Min and max of what a
/// trend would have plotted, never an average and never every n-th reading — the client's
/// `trendColumns` and its tests were deleted, not moved, because a second reduction that nothing
/// calls is a rule with two homes.
///
/// The comparisons against NaN and the infinities are the least obvious thing in the query and the
/// first test below is what holds them: JSON carries neither, so the raw path sends such a reading as
/// "no value" and a trend drops it. An envelope built over them would show a value a raw read of the
/// same window never would.
/// </remarks>
public sealed class HistorizedHistoryTests : IClassFixture<TestDatabase>
{
    private static readonly DateTimeOffset At = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly TestDatabase _database;

    public HistorizedHistoryTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_width_of_zero_is_refused_by_name()
    {
        // date_bin refuses a zero stride too, with a message about a stride — so the refusal happens
        // before the round trip, naming the width that arrived (ADR-0029 §5).
        // Requires a database only because this class does: the refusal itself is decided before
        // anything is sent.
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => historian.ReadBucketsAsync(
            Guid.NewGuid(), At, At.AddMinutes(1), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("bucketWidth", refused.ParamName);
    }

    [RequiresDatabaseFact]
    public async Task A_buckets_envelope_is_the_extremes_of_what_was_measured_in_it()
    {
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, At.AddSeconds(1), 3.4),
                Reading(seeded.TagId, At.AddSeconds(2), 9.9),
                Reading(seeded.TagId, At.AddSeconds(3), 3.5),
            ],
            CancellationToken.None);

        var bucket = Assert.Single(await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(5), Minute, CancellationToken.None));

        Assert.Equal(At, bucket.StartUtc);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(3.4, bucket.Low);
        Assert.Equal(9.9, bucket.High);
    }

    [RequiresDatabaseFact]
    public async Task A_reading_a_trend_would_not_plot_does_not_widen_the_envelope()
    {
        // **The rule that keeps a reduced read from disagreeing with a raw one.** A Bad reading may
        // still carry a number — the raw path sends it, and a trend drops it because only Good
        // readings are plotted. So must the envelope: 500 in a bucket of 3.4–3.6 would be a value no
        // trend of this window would ever have drawn.
        //
        // The same applies to readings that carry no number at all, and to the two a JSON response
        // cannot express: NaN and the infinities go out as "no value" (TagValueDto), and Postgres'
        // float ordering puts NaN above every number, so a max() that included it would win outright.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, At.AddSeconds(1), 3.4),
                Reading(seeded.TagId, At.AddSeconds(2), 500, Quality.Bad),
                Reading(seeded.TagId, At.AddSeconds(3), 3.6),
                new HistorianSample(seeded.TagId, new TagValue.Boolean(true), At.AddSeconds(4), At, Quality.Good),
                Reading(seeded.TagId, At.AddSeconds(5), double.NaN),
                Reading(seeded.TagId, At.AddSeconds(6), double.PositiveInfinity),
                Reading(seeded.TagId, At.AddSeconds(7), double.NegativeInfinity),
            ],
            CancellationToken.None);

        var bucket = Assert.Single(await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(5), Minute, CancellationToken.None));

        Assert.Equal(7, bucket.Count);
        Assert.Equal(3.4, bucket.Low);
        Assert.Equal(3.6, bucket.High);
    }

    [RequiresDatabaseFact]
    public async Task A_bucket_of_nothing_but_unplottable_readings_comes_back_with_a_count_and_no_value()
    {
        // Returned rather than dropped, which is what makes a device answering with nothing but Bad a
        // hole with a count on the chart instead of a stretch that looks unmeasured (ADR-0029 §4).
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, At.AddSeconds(1), 1, Quality.Bad),
                Reading(seeded.TagId, At.AddSeconds(2), 2, Quality.Bad),
                Reading(seeded.TagId, At.AddSeconds(3), 3, Quality.Uncertain),
            ],
            CancellationToken.None);

        var bucket = Assert.Single(await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(5), Minute, CancellationToken.None));

        Assert.Equal(3, bucket.Count);
        Assert.Null(bucket.Low);
        Assert.Null(bucket.High);
    }

    [RequiresDatabaseFact]
    public async Task A_stretch_nothing_was_measured_in_is_absent_and_the_counts_still_add_up()
    {
        // **Nothing is invented for a window nothing was measured in**, which is how a gap is drawn
        // ever since Phase 1's walk found a straight line through a Gateway outage. The middle bucket
        // here holds nothing, and the list simply skips it — a gap is a missing bucket, not a zero.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, At.AddSeconds(1), 3.4),
                Reading(seeded.TagId, At.AddSeconds(2), 3.5),
                Reading(seeded.TagId, At.AddMinutes(2).AddSeconds(1), 3.6),
            ],
            CancellationToken.None);

        var buckets = await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(3), Minute, CancellationToken.None);

        Assert.Equal([At, At.AddMinutes(2)], buckets.Select(bucket => bucket.StartUtc));
        Assert.Equal(3, buckets.Sum(bucket => bucket.Count));

        // And the same window read raw holds exactly those readings: the reduction groups, it does
        // not lose.
        var raw = await historian.ReadAsync(seeded.TagId, At, At.AddMinutes(3), CancellationToken.None);
        Assert.Equal(raw.Count, buckets.Sum(bucket => bucket.Count));
    }

    [RequiresDatabaseFact]
    public async Task The_grid_starts_at_the_window_the_caller_asked_for()
    {
        // date_bin's origin is the request's own `from` (ADR-0029 §5), which is what makes the first
        // bucket begin exactly at the window's edge and the same request return the same grid.
        // Anchored to the epoch, both ends would be partial and the boundaries would be facts about a
        // calendar rather than about the question asked.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);
        var from = At.AddSeconds(17);   // deliberately not on any round boundary

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, from.AddSeconds(1), 1),
                Reading(seeded.TagId, from.AddSeconds(61), 2),
            ],
            CancellationToken.None);

        var buckets = await historian.ReadBucketsAsync(
            seeded.TagId, from, from.AddMinutes(3), Minute, CancellationToken.None);

        Assert.Equal([from, from.AddMinutes(1)], buckets.Select(bucket => bucket.StartUtc));
    }

    [RequiresDatabaseFact]
    public async Task The_last_reading_time_is_a_reading_time_and_not_the_bucket_edge()
    {
        // Freshness is decided on this (ADR-0003): a bucket's start is the edge of the stretch, and
        // an age measured from it would call a live trend stale up to one bucket early.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);

        await historian.WriteAsync(
            [
                Reading(seeded.TagId, At.AddSeconds(1), 3.4),
                Reading(seeded.TagId, At.AddSeconds(10), 3.5),
            ],
            CancellationToken.None);

        var bucket = Assert.Single(await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(5), Minute, CancellationToken.None));

        Assert.Equal(At.AddSeconds(10), bucket.LastUtc);
        Assert.NotEqual(At.AddMinutes(1), bucket.LastUtc);
    }

    [RequiresDatabaseFact]
    public async Task A_long_window_comes_back_as_the_points_asked_for_and_not_the_readings_in_it()
    {
        // **The measurement this ADR exists for, in miniature.** An hour of a tag scanned once a
        // second is 3,600 readings; asked for as 60 points it comes back as 60 buckets holding all of
        // them, with the extremes intact — which is the difference between 3,600 rows crossing the
        // wire and 60.
        var seeded = await SeedAsync();
        var historian = new TimescaleHistorian(_database.ApplicationDataSource);
        var readings = Enumerable.Range(0, 3600)
            .Select(second => Reading(seeded.TagId, At.AddSeconds(second), 3.4 + (second % 100) / 100.0))
            .ToList();

        await historian.WriteAsync(readings, CancellationToken.None);

        const int Points = 60;
        var window = TimeSpan.FromMinutes(60);
        var width = TimeSpan.FromMilliseconds(Math.Ceiling(window.TotalMilliseconds / Points));

        var buckets = await historian.ReadBucketsAsync(
            seeded.TagId, At, At.AddMinutes(60), width, CancellationToken.None);

        Assert.Equal(Points, buckets.Count);
        Assert.Equal(3600, buckets.Sum(bucket => bucket.Count));

        // And nothing was flattened away by the reduction: the extremes the readings reached are the
        // extremes the buckets carry.
        Assert.Equal(readings.Min(reading => ((TagValue.Numeric)reading.Value!).Value), buckets.Min(bucket => bucket.Low));
        Assert.Equal(readings.Max(reading => ((TagValue.Numeric)reading.Value!).Value), buckets.Max(bucket => bucket.High));
    }

    private static HistorianSample Reading(Guid tagId, DateTimeOffset at, double value, Quality quality = Quality.Good) =>
        new(tagId, new TagValue.Numeric(value), at, at.AddSeconds(1), quality);

    private async Task<Seeded> SeedAsync()
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'History tenant');
            INSERT INTO site (id, tenant_id, name) VALUES ('{seeded.SiteId}', '{tenantId}', 'History site');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES ('{seeded.DeviceId}', '{seeded.SiteId}', 'History device', 'modbus-tcp', '{"{}"}', 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES ('{seeded.TagId}', '{seeded.DeviceId}', 'History tag', 0, '40001', false);
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        // Each test gets its own tag and its own readings; the window in every test is 2026-09-25,
        // which no other test in this assembly writes to, so the buckets hold what the test wrote.
        return seeded;
    }

    private sealed record Seeded(Guid SiteId, Guid DeviceId, Guid TagId);
}
