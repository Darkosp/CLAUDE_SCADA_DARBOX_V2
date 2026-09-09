using System.Collections.Concurrent;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// The read path from the Phase 0 architecture: driver readings land here, update the
/// current value, and fan out to (a) the historian and (b) every push subscriber.
/// </summary>
public sealed class TagEngine : ITagEngine
{
    private readonly TagCatalogSource _catalogSource;
    private readonly IHistorian _historian;
    private readonly IReadOnlyList<ITagValueSubscriber> _subscribers;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, TagSnapshot> _current = new();

    public TagEngine(
        TagCatalogSource catalogSource,
        IHistorian historian,
        IEnumerable<ITagValueSubscriber> subscribers,
        TimeProvider timeProvider)
    {
        _catalogSource = catalogSource;
        _historian = historian;
        _subscribers = subscribers.ToList();
        _timeProvider = timeProvider;
    }

    public async Task IngestAsync(IReadOnlyList<TagReading> readings, CancellationToken cancellationToken)
    {
        if (readings.Count == 0)
        {
            return;
        }

        // The ingestion timestamp is stamped once, here, at the boundary where the value
        // enters the server — deliberately separate from the source timestamp the device
        // reported, which may be older if the device was offline (ADR-0003).
        var ingestedAt = _timeProvider.GetUtcNow();

        // Read the catalogue once: a configuration change mid-batch would otherwise
        // resolve some readings against the old hierarchy and some against the new.
        var catalog = _catalogSource.Current;

        var snapshots = new List<TagSnapshot>(readings.Count);
        var samples = new List<HistorianSample>(readings.Count);

        foreach (var reading in readings)
        {
            var tag = catalog.FindTag(reading.TagId);
            if (tag is null)
            {
                // A reading for a tag that is not configured is dropped rather than
                // invented into existence; the driver was asked for it by ID, so this
                // means configuration changed underneath the scan.
                continue;
            }

            var snapshot = new TagSnapshot(
                reading.TagId,
                catalog.PathOf(reading.TagId),
                reading.Value,
                reading.SourceTimestampUtc,
                reading.Quality,
                tag.Unit?.Symbol);

            _current[reading.TagId] = snapshot;
            snapshots.Add(snapshot);

            samples.Add(new HistorianSample(
                reading.TagId,
                reading.Value,
                reading.SourceTimestampUtc,
                ingestedAt,
                reading.Quality));
        }

        if (samples.Count == 0)
        {
            return;
        }

        await _historian.WriteAsync(samples, cancellationToken).ConfigureAwait(false);

        foreach (var subscriber in _subscribers)
        {
            await subscriber.OnTagValuesAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
    }

    public TagSnapshot? GetCurrent(Guid tagId) => _current.GetValueOrDefault(tagId);

    public IReadOnlyList<TagSnapshot> GetAllCurrent() => _current.Values.ToList();
}
