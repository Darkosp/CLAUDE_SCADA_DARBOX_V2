namespace ScadaDarbox.Core.Alarms;

/// <summary>Which limit a value breached.</summary>
public enum AlarmLimit
{
    High,
    Low,
}

/// <summary>
/// Where an alarm sits in its lifecycle (ADR-0002 names the states this engine owns).
/// </summary>
public enum AlarmState
{
    /// <summary>Out of range now, and nobody has acknowledged it.</summary>
    Active,

    /// <summary>Still out of range, but an operator has seen it.</summary>
    Acknowledged,

    /// <summary>
    /// Back in range while still unacknowledged.
    /// </summary>
    /// <remarks>
    /// Kept rather than dropped. An excursion that corrected itself is exactly what an
    /// operator who stepped away needs to know happened; silently removing it would lose
    /// the one record that it did.
    /// </remarks>
    Cleared,

    /// <summary>Suppressed by an operator until <see cref="Alarm.ShelvedUntilUtc"/>, and left out of the banner.</summary>
    Shelved,
}

/// <summary>
/// One alarm as it currently stands.
/// </summary>
/// <param name="OccurrenceId">
/// This alarm, as distinct from earlier and later alarms on the same definition. Every
/// journal event about it carries this id (ADR-0013).
/// </param>
/// <param name="DefinitionId">
/// The condition that raised it. One definition holds at most one standing alarm at a
/// time, so this is also what an acknowledgement addresses.
/// </param>
/// <param name="SiteId">
/// The Site it belongs to, fixed when it was raised — so it stays placeable even after its
/// tag is deleted (ADR-0011, ADR-0013).
/// </param>
/// <param name="TagPath">Derived display path (ADR-0001) — for reading, never for binding.</param>
/// <param name="ValueAtRaise">The reading that raised it, kept even after the value moves on.</param>
public sealed record Alarm(
    Guid OccurrenceId,
    Guid DefinitionId,
    Guid TagId,
    Guid SiteId,
    string TagPath,
    AlarmLimit Limit,
    double LimitValue,
    double ValueAtRaise,
    string? UnitSymbol,
    DateTimeOffset RaisedAtUtc,
    AlarmState State,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ClearedAtUtc)
{
    /// <summary>Who acknowledged it, as their name read at the time.</summary>
    public AlarmActor? AcknowledgedBy { get; init; }

    /// <summary>When a shelf ends. Set only while <see cref="State"/> is <see cref="AlarmState.Shelved"/>.</summary>
    public DateTimeOffset? ShelvedUntilUtc { get; init; }

    /// <summary>
    /// Raised on the first evaluation after a restart: the breach was found then, and very
    /// likely began while nothing was watching.
    /// </summary>
    public bool DetectedAfterRestart { get; init; }
}
