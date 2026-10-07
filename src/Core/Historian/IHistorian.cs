using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Historian;

/// <summary>
/// One historized value. Carries both timestamps required by ADR-0003 so a delayed
/// value is stored at its true source time rather than at arrival time.
/// </summary>
/// <param name="TagId">Stable tag identity (ADR-0001) — the only reference the historian keeps.</param>
/// <param name="SourceTimestampUtc">When the device captured the value.</param>
/// <param name="IngestedAtUtc">When the value reached the server.</param>
public sealed record HistorianSample(
    Guid TagId,
    TagValue? Value,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset IngestedAtUtc,
    Quality Quality);

/// <summary>
/// One bucket of a reduced history: what a stretch of the window held, rather than one reading.
/// </summary>
/// <param name="StartUtc">
/// The start of the stretch, on the grid the caller's own window set (ADR-0029 §5).
/// </param>
/// <param name="LastUtc">
/// When the newest reading in it was measured. **The only field that can answer "how fresh is
/// this"**: <paramref name="StartUtc"/> is the edge of the stretch rather than a measurement time,
/// so an age decided on it would name a time nothing was measured at and would call a live trend
/// stale up to one bucket early.
/// </param>
/// <param name="Count">
/// How many readings were read in it, whatever their quality. Never zero — a stretch nothing was
/// measured in produces no bucket at all, which is how a gap is drawn (ADR-0029 §4).
/// </param>
/// <param name="Low">
/// The lowest of the readings a trend plots in this bucket, or null when none of them was
/// plottable. Paired with <paramref name="High"/>, never with a substituted or averaged value.
/// </param>
/// <param name="High">The highest of them, or null for the same reason as <paramref name="Low"/>.</param>
public sealed record HistorianBucket(
    DateTimeOffset StartUtc,
    DateTimeOffset LastUtc,
    int Count,
    double? Low,
    double? High);

/// <summary>
/// Storage and retrieval of historized values, independent of the concrete backend
/// (ADR-0002). PostgreSQL + TimescaleDB is the implementation used today (ADR-0006),
/// but nothing in core may assume it.
/// </summary>
public interface IHistorian
{
    /// <summary>
    /// Appends samples.
    /// </summary>
    /// <remarks>
    /// Out-of-order source timestamps are accepted and stored at their true source
    /// time — never rejected, and never quietly rewritten to arrival time (ADR-0003).
    /// </remarks>
    Task WriteAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken);

    /// <summary>
    /// Appends samples that may have been delivered before: a sample whose (tag, source
    /// timestamp) is already stored this way is not stored again (ADR-0017).
    /// </summary>
    /// <remarks>
    /// For pushed samples, which travel an at-least-once link. The first delivery is the one
    /// kept; a later one with the same key is dropped, not merged, and not treated as an error.
    /// </remarks>
    /// <returns>How many of <paramref name="samples"/> were stored — the rest were already there.</returns>
    Task<int> WriteOnceAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken);

    /// <summary>Reads one tag's samples over a time range, ordered by source timestamp.</summary>
    Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one tag's samples over a time range **reduced to buckets of a chosen width**, so that a
    /// long window costs the caller a bounded answer instead of every reading in it (ADR-0029).
    /// </summary>
    /// <remarks>
    /// **The width is asked for, not the number of buckets.** How many points a caller can draw is a
    /// question about its screen; how wide a bucket is is a question about storage — ADR-0002's
    /// boundary, applied to a parameter.
    ///
    /// Every reading in the range falls in exactly one bucket, and **no bucket is invented for a
    /// stretch nothing was measured in**, so the sum of the counts is the number of readings in the
    /// range and a missing bucket is a gap rather than a zero.
    ///
    /// **Plottable means Good and numeric**, which is the set a trend draws — so a reduced read can
    /// never show a value a <see cref="ReadAsync"/> of the same window would have dropped. A caller
    /// that needs another set of readings, or every one of them, reads them raw.
    /// </remarks>
    Task<IReadOnlyList<HistorianBucket>> ReadBucketsAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketWidth,
        CancellationToken cancellationToken);

    /// <summary>
    /// When the most recent sample reached the server, or null if none is known.
    /// </summary>
    /// <remarks>
    /// The last moment the Gateway is known to have been alive, which bounds an outage
    /// after an unclean stop (ADR-0013) — the historian writes continuously, so it is a
    /// heartbeat that already exists.
    /// </remarks>
    Task<DateTimeOffset?> LastIngestedAtAsync(CancellationToken cancellationToken);
}
