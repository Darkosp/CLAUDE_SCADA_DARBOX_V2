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

    /// <summary>Reads one tag's samples over a time range, ordered by source timestamp.</summary>
    Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
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
