namespace ScadaDarbox.Core.Alarms;

/// <summary>
/// How urgently an alarm needs an operator, from rationalisation (ADR-0034).
/// </summary>
/// <remarks>
/// <para>
/// **Three named values rather than a number**, which is the standards' shape and not a simplification.
/// ISA-18.2 and IEC 62682 make priority a product of rationalisation — set from the consequence of
/// ignoring an alarm and the time an operator has to respond — and the one concrete, checkable thing
/// they offer is a recommended **annunciated distribution of roughly 5% High, 15% Medium, 80% Low**.
/// That is a statement about countable groups; a free integer cannot be measured against it without
/// somebody inventing the groups afterwards.
/// </para>
/// <para>
/// The reason the distribution matters is the reason this enum is small: **if too many alarms are high
/// priority, effectively none of them stand out.**
/// </para>
/// <para>
/// **Absence is modelled as a null `AlarmPriority?`, not as a member here**, and deliberately: *not yet
/// rationalised* is a state of the **lifecycle**, not a degree of urgency, and putting it in this enum
/// would let it be compared with High and Low as though it were one. See ADR-0034 §2.
/// </para>
/// <para>
/// **A fourth tier is a later ADR and costs an enum member and a sort.** Some sites put *Emergency*
/// above High. It is not here because nothing has asked for it, and because three is the set the
/// published distribution target is stated for.
/// </para>
/// </remarks>
public enum AlarmPriority
{
    /// <summary>Act now. Targeted at roughly 5% of what an operator actually sees.</summary>
    High,

    /// <summary>Act soon. Roughly 15%.</summary>
    Medium,

    /// <summary>Be aware. Roughly 80%, and that is the healthy shape rather than a failure.</summary>
    Low,
}

/// <summary>Ordering, in the one place that decides it.</summary>
public static class AlarmPriorityOrder
{
    /// <summary>
    /// Where a priority sorts, most urgent first, with **not yet rationalised last**.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **Unrationalised sorts last and not among the Lows** (ADR-0034 §2). It is not a low-priority
    /// alarm; it is an alarm nobody has assessed, and the two must not be mixed, because working
    /// through the unassessed ones is a job somebody has to be able to see.
    /// </para>
    /// <para>
    /// A gap is left above High so a fourth tier can be added without renumbering anything that reads
    /// this — the same reason ADR-0034 §5 leaves gaps in the OPC UA severity mapping.
    /// </para>
    /// </remarks>
    public static int RankOf(AlarmPriority? priority) => priority switch
    {
        AlarmPriority.High => 10,
        AlarmPriority.Medium => 20,
        AlarmPriority.Low => 30,
        _ => int.MaxValue,
    };

    /// <summary>
    /// The OPC UA Part 9 `Severity` this priority maps to, for anything that has to speak it.
    /// </summary>
    /// <remarks>
    /// **A mapping, never a second stored field** (ADR-0034 §5). Part 9 carries a numeric severity in
    /// the range 1–1000; this product's model is the named set, because that is what an operator
    /// configures and what the distribution target is stated for. Two stored representations of one
    /// fact drift, which is the defect shape this repository knows as *a rule and every place that
    /// applies it have to move together*.
    /// <para>
    /// Nothing uses this yet. It exists so that the first feature that needs it does not decide it
    /// alone — and the gaps between the values are so a fourth tier fits without moving the others.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The order a reader sees standing alarms in: priority first, newest first within a priority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **A named function rather than a `OrderBy` inside the engine**, so the rule has one home and a
    /// test can reach it with more than one alarm in hand. The engine's snapshot is built from a
    /// private dictionary under a lock; testing the ordering through it would mean driving several
    /// tags through several raises to assert a comparison, and a test that expensive is a test that
    /// gets written once and never extended.
    /// </para>
    /// <para>
    /// **Not yet rationalised sorts last**, not among the Lows (ADR-0034 §2), and within any one group
    /// the newest is first — which is what the engine did for everything before priority existed, and
    /// is therefore exactly what a deployment that rationalises nothing still gets.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Alarm> ForDisplay(IEnumerable<Alarm> alarms) =>
        [.. alarms
            .OrderBy(alarm => RankOf(alarm.Priority))
            .ThenByDescending(alarm => alarm.RaisedAtUtc)];

    public static ushort? SeverityOf(AlarmPriority? priority) => priority switch
    {
        AlarmPriority.High => 700,
        AlarmPriority.Medium => 500,
        AlarmPriority.Low => 300,

        // **Not a severity of zero.** Part 9's range starts at 1, and a server that published 0 would
        // be making a claim about urgency where this product has none to make.
        _ => null,
    };
}
