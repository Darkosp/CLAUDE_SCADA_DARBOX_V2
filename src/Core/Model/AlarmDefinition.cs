using ScadaDarbox.Core.Alarms;

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

    /// <summary>
    /// How urgently this alarm needs an operator, or null for <b>not yet rationalised</b> (ADR-0034).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is a state, not an absence, and it is ISA-18.2's own.</b> It means nobody has assessed
    /// this alarm's consequence and the time an operator has to respond — which is a real stage of the
    /// standard's lifecycle, and is why the migration that added this column backfilled nothing.
    /// </para>
    /// <para>
    /// <b>Every available default would have been a lie.</b> High makes a system 100% high priority,
    /// which the standard says is the same as having no priorities at all; Low silently downgrades
    /// something that may matter; Medium asserts a middling consequence for an alarm nobody assessed.
    /// All three are the product deciding what only the plant can.
    /// </para>
    /// <para>
    /// <b>It never changes with the alarm's state.</b> Priority is a statement about consequence, and
    /// consequence does not change because an alarm has been standing a while, or because somebody
    /// acknowledged it. ADR-0025 owns when an alarm raises and clears; ADR-0013 owns its states.
    /// </para>
    /// </remarks>
    public AlarmPriority? Priority { get; set; }
}
