using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>A clock the test moves by hand, so timestamp behaviour is deterministic.</summary>
internal sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class RecordingHistorian : IHistorian
{
    private readonly HashSet<(Guid, DateTimeOffset)> _writtenOnce = [];

    public List<HistorianSample> Written { get; } = [];

    public Task WriteAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken)
    {
        Written.AddRange(samples);
        return Task.CompletedTask;
    }

    /// <summary>The database's rule, kept in memory: one row per (tag, source time) written this way.</summary>
    public Task<int> WriteOnceAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken)
    {
        var stored = 0;
        foreach (var sample in samples)
        {
            if (_writtenOnce.Add((sample.TagId, sample.SourceTimestampUtc)))
            {
                Written.Add(sample);
                stored++;
            }
        }

        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HistorianSample>>(
            Written.Where(s => s.TagId == tagId && s.SourceTimestampUtc >= fromUtc && s.SourceTimestampUtc < toUtc)
                .OrderBy(s => s.SourceTimestampUtc)
                .ToList());

    /// <summary>The database's rule, kept in memory: the same counts, envelope and grid (ADR-0029).</summary>
    public Task<IReadOnlyList<HistorianBucket>> ReadBucketsAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketWidth,
        CancellationToken cancellationToken)
    {
        var inRange = Written
            .Where(s => s.TagId == tagId && s.SourceTimestampUtc >= fromUtc && s.SourceTimestampUtc < toUtc);

        var buckets = inRange
            .GroupBy(s => InBucket(s.SourceTimestampUtc, fromUtc, bucketWidth))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                // Plottable means Good and numeric, and finite — JSON carries no NaN and no
                // infinity, so the raw path sends those as no value and the chart drops them.
                var plot = group
                    .Where(s => s.Quality == Quality.Good && s.Value is TagValue.Numeric { } n && double.IsFinite(n.Value))
                    .Select(s => ((TagValue.Numeric)s.Value!).Value)
                    .ToList();

                return new HistorianBucket(
                    group.Key,
                    group.Max(s => s.SourceTimestampUtc),
                    group.Count(),
                    plot.Count == 0 ? null : plot.Min(),
                    plot.Count == 0 ? null : plot.Max());
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<HistorianBucket>>(buckets);
    }

    /// <summary>Floors to the grid the window set, which is what `date_bin` does with its origin.</summary>
    private static DateTimeOffset InBucket(DateTimeOffset at, DateTimeOffset fromUtc, TimeSpan bucketWidth)
    {
        // Truncating integer division, which floors here because every reading in range is at or
        // after the origin — the same reason the SQL can use date_bin rather than a CASE.
        var steps = (at - fromUtc).Ticks / bucketWidth.Ticks;
        return fromUtc.AddTicks(steps * bucketWidth.Ticks);
    }

    public Task<DateTimeOffset?> LastIngestedAtAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Written.Count == 0 ? (DateTimeOffset?)null : Written.Max(s => s.IngestedAtUtc));
}

internal sealed class RecordingSubscriber : ITagValueSubscriber
{
    public List<TagSnapshot> Received { get; } = [];

    public ValueTask OnTagValuesAsync(IReadOnlyList<TagSnapshot> snapshots, CancellationToken cancellationToken)
    {
        Received.AddRange(snapshots);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A historian whose database is down: every write throws.</summary>
internal sealed class FailingHistorian : IHistorian
{
    public Task WriteAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");

    public Task<int> WriteOnceAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");

    public Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");

    public Task<IReadOnlyList<HistorianBucket>> ReadBucketsAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketWidth,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");

    public Task<DateTimeOffset?> LastIngestedAtAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");
}
