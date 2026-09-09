namespace ScadaDarbox.Core.Model;

/// <summary>
/// One observation of a tag, as produced by a driver (ADR-0003).
/// </summary>
/// <param name="TagId">The tag's stable identity (ADR-0001) — never its display path.</param>
/// <param name="Value">The typed value.</param>
/// <param name="SourceTimestampUtc">
/// When the originating device captured the value, in UTC. This is not the time the
/// value reached the server; a value delayed by a reconnect keeps its true source time
/// so it lands at the right point in history.
/// </param>
/// <param name="Quality">Trustworthiness of the value.</param>
public sealed record TagReading(
    Guid TagId,
    TagValue Value,
    DateTimeOffset SourceTimestampUtc,
    Quality Quality);
