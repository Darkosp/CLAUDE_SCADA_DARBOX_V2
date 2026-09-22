using ScadaDarbox.Core.Historian;
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
    public List<HistorianSample> Written { get; } = [];

    public Task WriteAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken)
    {
        Written.AddRange(samples);
        return Task.CompletedTask;
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

    public Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");

    public Task<DateTimeOffset?> LastIngestedAtAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The historian is unavailable.");
}
