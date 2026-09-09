using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

public class TagEngineTests
{
    private static readonly Guid TagId = new("11111111-1111-4111-8111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Historises_the_source_timestamp_separately_from_the_ingestion_timestamp()
    {
        // ADR-0003: a value delayed by a reconnect must be stored at its true source time.
        var (engine, historian, _) = Build();
        var capturedAt = Now.AddMinutes(-5);

        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(4.2), capturedAt, Quality.Good)],
            CancellationToken.None);

        var sample = Assert.Single(historian.Written);
        Assert.Equal(capturedAt, sample.SourceTimestampUtc);
        Assert.Equal(Now, sample.IngestedAtUtc);
        Assert.NotEqual(sample.SourceTimestampUtc, sample.IngestedAtUtc);
    }

    [Fact]
    public async Task Accepts_out_of_order_source_timestamps_without_reordering_them()
    {
        var (engine, historian, _) = Build();
        var later = Now.AddSeconds(-1);
        var earlier = Now.AddSeconds(-30);

        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(1), later, Quality.Good)],
            CancellationToken.None);
        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(2), earlier, Quality.Good)],
            CancellationToken.None);

        Assert.Equal([later, earlier], historian.Written.Select(s => s.SourceTimestampUtc));
    }

    [Fact]
    public async Task Preserves_bad_quality_and_the_absence_of_a_value()
    {
        // A Bad reading carries no value at all. Historising a placeholder instead would
        // put a number in the record that no device ever reported.
        var (engine, historian, subscriber) = Build();

        await engine.IngestAsync(
            [new TagReading(TagId, null, Now, Quality.Bad)],
            CancellationToken.None);

        var sample = Assert.Single(historian.Written);
        Assert.Equal(Quality.Bad, sample.Quality);
        Assert.Null(sample.Value);

        var snapshot = Assert.Single(subscriber.Received);
        Assert.Equal(Quality.Bad, snapshot.Quality);
        Assert.Null(snapshot.Value);
    }

    [Fact]
    public async Task Publishes_a_snapshot_carrying_the_derived_path_and_unit_symbol()
    {
        var (engine, _, subscriber) = Build();

        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(4.2), Now, Quality.Good)],
            CancellationToken.None);

        var snapshot = Assert.Single(subscriber.Received);
        Assert.Equal("Skopje/Pump House/Discharge Pressure", snapshot.Path);
        Assert.Equal("bar", snapshot.UnitSymbol);
        Assert.Equal(snapshot, engine.GetCurrent(TagId));
    }

    [Fact]
    public async Task Ignores_readings_for_tags_that_are_not_configured()
    {
        var (engine, historian, subscriber) = Build();

        await engine.IngestAsync(
            [new TagReading(Guid.NewGuid(), new TagValue.Numeric(1), Now, Quality.Good)],
            CancellationToken.None);

        Assert.Empty(historian.Written);
        Assert.Empty(subscriber.Received);
    }

    private static (TagEngine Engine, RecordingHistorian Historian, RecordingSubscriber Subscriber) Build()
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Darbo" };
        var site = new Site { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Skopje" };
        var device = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Name = "Pump House",
            DriverKey = "modbus-tcp",
        };
        var tag = new Tag
        {
            Id = TagId,
            DeviceId = device.Id,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            Unit = new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
            SourceAddress = "holding:0",
        };

        var historian = new RecordingHistorian();
        var subscriber = new RecordingSubscriber();
        var engine = new TagEngine(
            new TagCatalog(tenant, [site], [device], [tag]),
            historian,
            [subscriber],
            new StubTimeProvider(Now));

        return (engine, historian, subscriber);
    }
}
