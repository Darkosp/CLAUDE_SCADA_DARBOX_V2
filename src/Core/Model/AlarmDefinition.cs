namespace ScadaDarbox.Core.Model;

/// <summary>
/// The condition under which a tag's value raises an alarm.
/// </summary>
/// <remarks>
/// Its own entity rather than columns on <see cref="Tag"/>: a tag may later carry more
/// than one condition (HighHigh/High/Low/LowLow), and a separate table extends without
/// reshaping the tag.
///
/// Phase 3 deliberately had no hysteresis or deadband, and said so here. ADR-0025 is the
/// "later" that remark anticipated: <see cref="OnDelaySeconds"/> and <see cref="Deadband"/> are
/// the two standard remedies for a value oscillating across a limit, and they are separate
/// settings because they are separate decisions — a wait trades speed for quiet, and a deadband
/// trades the position of a limit for quiet.
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

    /// <summary>
    /// How long the condition must hold before the alarm is raised, or null for none (ADR-0025 §2).
    /// </summary>
    /// <remarks>
    /// A condition that stops holding inside the window raises nothing at all — no journal row and
    /// nothing an operator has to dismiss again. **The cost is stated rather than hidden: the alarm is
    /// late by this much**, which is why it is nullable rather than defaulted. A deployment that needs
    /// the alarm the instant a limit is crossed leaves it null and accepts the chatter.
    /// </remarks>
    public TimeSpan? OnDelaySeconds { get; set; }

    /// <summary>
    /// How far a value must come back past the limit before the alarm clears, or null for none
    /// (ADR-0025 §3).
    /// </summary>
    /// <remarks>
    /// <b>It applies to clearing only, never to raising.</b> An alarm still raises at exactly its
    /// limit, so an operator reading "High limit 4.50 bar" is reading the truth about when it will go
    /// off. The opposite arrangement would make the configured limit a number the alarm does not
    /// actually use, which is why it is not built and not configurable.
    /// </remarks>
    public double? Deadband { get; set; }
}
