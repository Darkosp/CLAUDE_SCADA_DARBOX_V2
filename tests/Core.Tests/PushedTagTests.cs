using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// ADR-0016: a pushing driver is never asked what a value is now. What arrives goes to
/// history; a tag's current value moves only forward in source time; and silence past the
/// driver's limit reads as loss, never as "unchanged".
/// </summary>
public sealed class PushedTagTests
{
    private static readonly Guid TagId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid TenantId = new("aaaaaaaa-2222-4222-8222-222222222222");
    private static readonly Guid SiteId = new("bbbbbbbb-2222-4222-8222-222222222222");
    private static readonly Guid DeviceId = new("cccccccc-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_pushed_tag_silent_past_its_limit_reads_Bad_with_its_last_real_time_and_no_value()
    {
        var (engine, clock, historian, subscriber) = Build();
        var measuredAt = Start.AddSeconds(-2);

        await engine.AcceptPushedAsync([Sample(4.2, measuredAt)], CancellationToken.None);
        Assert.Equal(Quality.Good, engine.GetCurrent(TagId)!.Quality);

        // Then nothing, for longer than the limit.
        clock.Now = Start + Limit + TimeSpan.FromSeconds(1);
        await engine.MarkSilentTagsAsync([TagId], Limit, CancellationToken.None);

        var current = engine.GetCurrent(TagId)!;

        // Loss, not "unchanged". Without the silence rule the tag is still Good here.
        Assert.Equal(Quality.Bad, current.Quality);

        // With the time of the last thing actually measured — not the time of the sweep, and
        // not a time nobody measured.
        Assert.Equal(measuredAt, current.SourceTimestampUtc);

        // No value carried forward and none made up.
        Assert.Null(current.Value);

        // History holds the one real sample and nothing for the silence: it is a gap there.
        var stored = Assert.Single(historian.Written);
        Assert.Equal(measuredAt, stored.SourceTimestampUtc);
        Assert.Equal(new TagValue.Numeric(4.2), stored.Value);

        // Readers and alarms were told, once.
        Assert.Equal([Quality.Good, Quality.Bad], subscriber.Received.Select(snapshot => snapshot.Quality));
    }

    [Fact]
    public async Task Within_its_limit_a_quiet_pushed_tag_stays_as_it_was()
    {
        // The control for the test above: the rule is "silent past the limit", not "Bad
        // whenever a sweep runs".
        var (engine, clock, _, subscriber) = Build();
        await engine.AcceptPushedAsync([Sample(4.2, Start)], CancellationToken.None);

        clock.Now = Start + Limit;
        await engine.MarkSilentTagsAsync([TagId], Limit, CancellationToken.None);

        var current = engine.GetCurrent(TagId)!;
        Assert.Equal(Quality.Good, current.Quality);
        Assert.Equal(new TagValue.Numeric(4.2), current.Value);
        Assert.Single(subscriber.Received);
    }

    [Fact]
    public async Task Silence_is_reported_once_and_ends_only_when_something_new_arrives()
    {
        var (engine, clock, _, subscriber) = Build();
        await engine.AcceptPushedAsync([Sample(4.2, Start)], CancellationToken.None);

        clock.Now = Start + Limit + TimeSpan.FromSeconds(1);
        await engine.MarkSilentTagsAsync([TagId], Limit, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(5);
        await engine.MarkSilentTagsAsync([TagId], Limit, CancellationToken.None);

        // Reported once, not on every sweep.
        Assert.Equal(1, subscriber.Received.Count(snapshot => snapshot.Quality == Quality.Bad));

        // A replay of the sample the loss was reported against does not end it.
        await engine.AcceptPushedAsync([Sample(4.2, Start)], CancellationToken.None);
        Assert.Equal(Quality.Bad, engine.GetCurrent(TagId)!.Quality);

        // Something newer does.
        var fresh = Start.AddMinutes(6);
        await engine.AcceptPushedAsync([Sample(4.4, fresh)], CancellationToken.None);
        var current = engine.GetCurrent(TagId)!;
        Assert.Equal(Quality.Good, current.Quality);
        Assert.Equal(new TagValue.Numeric(4.4), current.Value);
        Assert.Equal(fresh, current.SourceTimestampUtc);
    }

    [Fact]
    public async Task A_late_batch_goes_into_history_without_moving_the_current_value_back()
    {
        var (engine, clock, historian, subscriber) = Build();
        var newest = Start;
        await engine.AcceptPushedAsync([Sample(5.0, newest)], CancellationToken.None);

        // Minutes later a buffer drains: samples from before the one already current.
        clock.Now = Start.AddMinutes(3);
        var older = Start.AddMinutes(-2);
        var oldest = Start.AddMinutes(-4);
        await engine.AcceptPushedAsync([Sample(3.0, older), Sample(2.0, oldest)], CancellationToken.None);

        // Late is not wrong: all three are in history, at their own source times.
        Assert.Equal(
            [newest, oldest, older],
            historian.Written.Select(sample => sample.SourceTimestampUtc));

        // But the current value is still the newest measurement, not the last to arrive.
        var current = engine.GetCurrent(TagId)!;
        Assert.Equal(new TagValue.Numeric(5.0), current.Value);
        Assert.Equal(newest, current.SourceTimestampUtc);

        // And no reader or alarm was shown the past as if it were now.
        var shown = Assert.Single(subscriber.Received);
        Assert.Equal(newest, shown.SourceTimestampUtc);
    }

    [Fact]
    public async Task A_shuffled_batch_is_stored_in_source_order_and_leaves_its_newest_sample_current()
    {
        var (engine, _, historian, subscriber) = Build();
        var first = Start.AddSeconds(-30);
        var second = Start.AddSeconds(-20);
        var third = Start.AddSeconds(-10);

        await engine.AcceptPushedAsync(
            [Sample(3.0, third), Sample(1.0, first), Sample(2.0, second)],
            CancellationToken.None);

        Assert.Equal([first, second, third], historian.Written.Select(sample => sample.SourceTimestampUtc));
        Assert.Equal(new TagValue.Numeric(3.0), engine.GetCurrent(TagId)!.Value);
        Assert.Equal([first, second, third], subscriber.Received.Select(snapshot => snapshot.SourceTimestampUtc));
    }

    [Fact]
    public async Task A_polled_reading_still_becomes_current_whatever_its_source_time()
    {
        // Why the forward-only rule is for pushed samples and not for polled ones: a polled
        // driver stamps a Bad reading with the Gateway's clock and a Good one with the device's
        // (OPC UA). With the device clock a minute ahead, "never backwards" would keep the last
        // Good value on screen while the device is unreachable (ADR-0003).
        var (engine, _, _, _) = Build();

        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(4.2), Start.AddMinutes(1), Quality.Good)],
            CancellationToken.None);
        await engine.IngestAsync(
            [new TagReading(TagId, null, Start, Quality.Bad)],
            CancellationToken.None);

        var current = engine.GetCurrent(TagId)!;
        Assert.Equal(Quality.Bad, current.Quality);
        Assert.Null(current.Value);
    }

    private static TagReading Sample(double value, DateTimeOffset measuredAt) =>
        new(TagId, new TagValue.Numeric(value), measuredAt, Quality.Good);

    private static (TagEngine Engine, StubTimeProvider Clock, RecordingHistorian Historian, RecordingSubscriber Subscriber) Build()
    {
        var clock = new StubTimeProvider(Start);
        var historian = new RecordingHistorian();
        var subscriber = new RecordingSubscriber();
        var engine = new TagEngine(new TagCatalogSource(Catalog()), historian, [subscriber], clock);
        return (engine, clock, historian, subscriber);
    }

    private static TagCatalog Catalog()
    {
        var tenant = new Tenant { Id = TenantId, Name = "Darbo" };
        var site = new Site { Id = SiteId, TenantId = TenantId, Name = "Skopje" };
        var device = new Device { Id = DeviceId, SiteId = SiteId, Name = "Edge", DriverKey = "pushing-test" };
        var tag = new Tag
        {
            Id = TagId,
            DeviceId = DeviceId,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            Unit = new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
            SourceAddress = "pressure",
        };

        return new TagCatalog(tenant, [site], [], [device], [tag]);
    }
}
