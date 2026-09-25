using System.Collections.Concurrent;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// The read path from the Phase 0 architecture: driver readings land here, update the
/// current value, and fan out to (a) the historian and (b) every push subscriber.
/// </summary>
/// <remarks>
/// Two ways in, one per driver shape (ADR-0016). A polled driver's readings are the device's
/// answer to "what is it now", and each one becomes current as it comes. A pushing driver's
/// samples may arrive late and out of order; they all go to history, but a tag's current value
/// only moves forward in source time — and silence past the driver's limit reads as loss.
/// </remarks>
public sealed class TagEngine : ITagEngine
{
    private readonly TagCatalogSource _catalogSource;
    private readonly IHistorian _historian;
    private readonly IReadOnlyList<ITagValueSubscriber> _subscribers;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, TagSnapshot> _current = new();

    // Pushed tags only (ADR-0016): the source time of the last real sample that became current,
    // when it arrived, and whether its silence has already been reported, so a sweep does not
    // republish the same loss every time it runs.
    private readonly Lock _pushedGate = new();
    private readonly Dictionary<Guid, PushedState> _pushed = [];

    // Pushed tags that have never received anything: when listening began — observed, not
    // measured — and whether "no data" has already been reported for them.
    private readonly Dictionary<Guid, Listening> _listening = [];

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

            // Every polled reading becomes current, in the order it came, whatever its source
            // time. Deliberately not the pushed rule below: a polled driver's Bad reading is
            // stamped with the Gateway's clock, while its Good ones may carry the device's
            // (OPC UA). A device clock ahead of the Gateway's would make "never backwards" keep
            // the last Good value on screen while the device is unreachable (ADR-0003).
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

        await PublishAsync(samples, snapshots, cancellationToken).ConfigureAwait(false);
    }

    public async Task AcceptPushedAsync(IReadOnlyList<TagReading> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var arrivedAt = _timeProvider.GetUtcNow();
        var catalog = _catalogSource.Current;

        var history = new List<HistorianSample>(samples.Count);
        var becameCurrent = new List<TagSnapshot>();

        // In source-timestamp order per tag (ADR-0016), so a batch that arrives shuffled still
        // writes its history in order and leaves each tag's newest sample current.
        var ordered = samples
            .Where(sample => catalog.FindTag(sample.TagId) is not null)
            .OrderBy(sample => sample.TagId)
            .ThenBy(sample => sample.SourceTimestampUtc);

        foreach (var sample in ordered)
        {
            var tag = catalog.FindTag(sample.TagId)!;

            // Late is not wrong: every sample is kept, at its own source time (ADR-0003).
            history.Add(new HistorianSample(
                sample.TagId,
                sample.Value,
                sample.SourceTimestampUtc,
                arrivedAt,
                sample.Quality));

            lock (_pushedGate)
            {
                // Only a sample newer than the last real one moves the current value. Anything
                // else — a late arrival, a replay of the same sample — would move it backwards
                // in time, or undo a loss that silence had already been reported against.
                if (_pushed.TryGetValue(sample.TagId, out var state)
                    && sample.SourceTimestampUtc <= state.LastRealSourceTimestamp)
                {
                    continue;
                }

                _pushed[sample.TagId] = new PushedState(sample.SourceTimestampUtc, arrivedAt, Silenced: false);
                _listening.Remove(sample.TagId);
            }

            var snapshot = new TagSnapshot(
                sample.TagId,
                catalog.PathOf(sample.TagId),
                sample.Value,
                sample.SourceTimestampUtc,
                sample.Quality,
                tag.Unit?.Symbol);

            _current[sample.TagId] = snapshot;
            becameCurrent.Add(snapshot);
        }

        // Readers and alarms see only what became current: an alarm evaluated on a late sample
        // would change state on the strength of the past (ADR-0016). History is written once per
        // (tag, source time): a pushed batch can be delivered twice (ADR-0017), and a replay is
        // neither current nor new.
        await PublishAsync(history, becameCurrent, cancellationToken, once: true).ConfigureAwait(false);
    }

    public async Task MarkSilentTagsAsync(
        IReadOnlyCollection<Guid> tagIds,
        TimeSpan stalenessLimit,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var silenced = new List<TagSnapshot>();

        var catalog = _catalogSource.Current;

        lock (_pushedGate)
        {
            foreach (var tagId in tagIds)
            {
                if (!_pushed.ContainsKey(tagId))
                {
                    if (NeverReceived(tagId, now, stalenessLimit, catalog) is { } nothing)
                    {
                        silenced.Add(nothing);
                    }

                    continue;
                }

                if (!_pushed.TryGetValue(tagId, out var state)
                    || state.Silenced
                    || now - state.ArrivedAt <= stalenessLimit
                    || !_current.TryGetValue(tagId, out var current))
                {
                    continue;
                }

                // Bad, with no value and the time of the last real sample. What is known is when
                // something was last measured, and nothing about what it is now: not "unchanged",
                // not the last value carried forward, and not a timestamp nobody measured.
                var loss = current with
                {
                    Value = null,
                    Quality = Quality.Bad,
                    SourceTimestampUtc = state.LastRealSourceTimestamp,
                };

                _pushed[tagId] = state with { Silenced = true };
                _current[tagId] = loss;
                silenced.Add(loss);
            }
        }

        // Readers and alarms are told; history is not. The silence is a gap there, and the
        // chart already draws a gap as a gap (ADR-0016).
        await PublishAsync([], silenced, cancellationToken).ConfigureAwait(false);
    }

    public void BeginListening(IReadOnlyCollection<Guid> tagIds)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_pushedGate)
        {
            foreach (var tagId in tagIds)
            {
                // The first time only: a driver restarted by a configuration edit has still had
                // no data since it was first listened for, not since the restart.
                if (!_pushed.ContainsKey(tagId))
                {
                    _listening.TryAdd(tagId, new Listening(now, Reported: false));
                }
            }
        }
    }

    public TagSnapshot? GetCurrent(Guid tagId) => _current.GetValueOrDefault(tagId);

    public IReadOnlyList<TagSnapshot> GetAllCurrent() => _current.Values.ToList();

    /// <summary>Hands samples to the historian and snapshots to every push subscriber.</summary>
    /// <param name="once">Write through <see cref="IHistorian.WriteOnceAsync"/>: samples that may arrive again.</param>
    private async Task PublishAsync(
        IReadOnlyList<HistorianSample> samples,
        IReadOnlyList<TagSnapshot> snapshots,
        CancellationToken cancellationToken,
        bool once = false)
    {
        // Recording a value and acting on it are independent (ADR-0013). If a failed historian
        // write — or a failing push to one subscriber — stopped the rest, a database outage
        // would also stop alarm evaluation: nothing watched, exactly when the system still
        // looks healthy. Every step runs; the first failure is raised afterwards, so the scan
        // loop still reports it.
        var failures = new List<Exception>();

        if (samples.Count > 0)
        {
            try
            {
                if (once)
                {
                    await _historian.WriteOnceAsync(samples, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _historian.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
            }
        }

        if (snapshots.Count > 0)
        {
            foreach (var subscriber in _subscribers)
            {
                try
                {
                    await subscriber.OnTagValuesAsync(snapshots, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(exception);
                }
            }
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// A pushed tag that has received nothing since listening began more than the limit ago:
    /// Bad, with no value and no measured time — there was no measurement to take one from —
    /// and "no data since" the moment listening began. Null while there is nothing to report.
    /// Called under <see cref="_pushedGate"/>.
    /// </summary>
    private TagSnapshot? NeverReceived(Guid tagId, DateTimeOffset now, TimeSpan stalenessLimit, TagCatalog catalog)
    {
        if (!_listening.TryGetValue(tagId, out var listening)
            || listening.Reported
            || now - listening.Since <= stalenessLimit
            || catalog.FindTag(tagId) is not { } tag)
        {
            return null;
        }

        var nothing = new TagSnapshot(
            tagId,
            catalog.PathOf(tagId),
            Value: null,
            SourceTimestampUtc: null,
            Quality.Bad,
            tag.Unit?.Symbol,
            NoDataSinceUtc: listening.Since);

        _listening[tagId] = listening with { Reported = true };
        _current[tagId] = nothing;
        return nothing;
    }

    private sealed record PushedState(DateTimeOffset LastRealSourceTimestamp, DateTimeOffset ArrivedAt, bool Silenced);

    private sealed record Listening(DateTimeOffset Since, bool Reported);
}
