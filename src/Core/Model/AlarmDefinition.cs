namespace ScadaDarbox.Core.Model;

/// <summary>
/// The condition under which a tag's value raises an alarm.
/// </summary>
/// <remarks>
/// Its own entity rather than columns on <see cref="Tag"/>: a tag may later carry more
/// than one condition (HighHigh/High/Low/LowLow), and a separate table extends without
/// reshaping the tag.
///
/// Phase 3 deliberately has no hysteresis or deadband. A value hovering on a limit will
/// chatter, which is a real shortcoming for a production system and a deliberate one
/// here — the shape below takes a deadband later without changing.
/// </remarks>
public sealed class AlarmDefinition
{
    public required Guid Id { get; init; }

    /// <summary>The tag being watched, by its stable identity (ADR-0001).</summary>
    public required Guid TagId { get; init; }

    /// <summary>Value at or above which the alarm is active, or null for no high limit.</summary>
    public double? HighLimit { get; set; }

    /// <summary>Value at or below which the alarm is active, or null for no low limit.</summary>
    public double? LowLimit { get; set; }
}
