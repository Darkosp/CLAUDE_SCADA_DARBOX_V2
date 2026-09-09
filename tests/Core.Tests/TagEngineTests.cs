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

    [Fact]
    public async Task Picks_up_a_replacement_catalogue_without_being_rebuilt()
    {
        // What makes adding a device through the UI take effect without a restart: the
        // engine reads the catalogue in force at ingest time, not one captured at
        // construction.
        var (engine, _, subscriber, source) = BuildWithSource();

        var renamed = Rebuild(siteName: "Skopje North");
        source.Set(renamed);

        await engine.IngestAsync(
            [new TagReading(TagId, new TagValue.Numeric(4.2), Now, Quality.Good)],
            CancellationToken.None);

        Assert.Equal(
            "Skopje North/Pump House/Discharge Pressure",
            Assert.Single(subscriber.Received).Path);
    }

    private static readonly Guid TenantId = new("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid SiteId = new("bbbbbbbb-1111-4111-8111-111111111111");
    private static readonly Guid DeviceId = new("cccccccc-1111-4111-8111-111111111111");

    private static (TagEngine Engine, RecordingHistorian Historian, RecordingSubscriber Subscriber) Build()
    {
        var (engine, historian, subscriber, _) = BuildWithSource();
        return (engine, historian, subscriber);
    }

    private static (TagEngine Engine, RecordingHistorian Historian, RecordingSubscriber Subscriber, TagCatalogSource Source)
        BuildWithSource()
    {
        var historian = new RecordingHistorian();
        var subscriber = new RecordingSubscriber();
        var source = new TagCatalogSource(Rebuild(siteName: "Skopje"));

        var engine = new TagEngine(source, historian, [subscriber], new StubTimeProvider(Now));

        return (engine, historian, subscriber, source);
    }

    /// <summary>
    /// A catalogue over the same identities, so a rebuild is the same hierarchy under a
    /// different name rather than a different hierarchy (ADR-0001).
    /// </summary>
    private static TagCatalog Rebuild(string siteName)
    {
        var tenant = new Tenant { Id = TenantId, Name = "Darbo" };
        var site = new Site { Id = SiteId, TenantId = TenantId, Name = siteName };
        var device = new Device
        {
            Id = DeviceId,
            SiteId = SiteId,
            Name = "Pump House",
            DriverKey = "modbus-tcp",
        };
        var tag = new Tag
        {
            Id = TagId,
            DeviceId = DeviceId,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            Unit = new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
            SourceAddress = "holding:0",
        };

        return new TagCatalog(tenant, [site], [], [device], [tag]);
    }
}
