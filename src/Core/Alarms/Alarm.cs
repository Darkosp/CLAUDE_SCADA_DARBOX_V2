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

    /// <summary>Deliberately suppressed by an operator, and left out of the banner.</summary>
    Shelved,
}

/// <summary>
/// One alarm as it currently stands.
/// </summary>
/// <param name="DefinitionId">
/// The alarm's identity. One definition holds at most one alarm at a time, so this is
/// also what an acknowledgement addresses.
/// </param>
/// <param name="TagPath">Derived display path (ADR-0001) — for reading, never for binding.</param>
/// <param name="ValueAtRaise">The reading that raised it, kept even after the value moves on.</param>
public sealed record Alarm(
    Guid DefinitionId,
    Guid TagId,
    string TagPath,
    AlarmLimit Limit,
    double LimitValue,
    double ValueAtRaise,
    string? UnitSymbol,
    DateTimeOffset RaisedAtUtc,
    AlarmState State,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ClearedAtUtc);
