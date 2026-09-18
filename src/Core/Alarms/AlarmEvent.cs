namespace ScadaDarbox.Core.Alarms;

/// <summary>What an entry in the alarm journal records (ADR-0013).</summary>
public enum AlarmEventType
{
    /// <summary>A value breached a limit: starts an occurrence.</summary>
    Raised,

    Acknowledged,

    /// <summary>Suppressed until a stated time; always carries <see cref="AlarmEvent.ShelvedUntilUtc"/>.</summary>
    Shelved,

    /// <summary>A shelf expired and the alarm re-announced itself.</summary>
    Unshelved,

    /// <summary>The value returned inside its limits.</summary>
    Cleared,

    /// <summary>The engine dropped the alarm from the live list: closes the occurrence.</summary>
    Retired,

    /// <summary>The engine began evaluating; belongs to no alarm.</summary>
    EvaluationStarted,

    /// <summary>The engine stopped evaluating cleanly; belongs to no alarm.</summary>
    EvaluationStopped,

    /// <summary>
    /// A closed window during which journal writes failed, and how many transitions went
    /// unrecorded in it. One retrospective row, not half of a pair: the failure could not
    /// be recorded as it began, because recording is what was failing.
    /// </summary>
    JournalGap,
}

/// <summary>
/// Who acted: the stable id, and the name as it read at the time — users are renamed and
/// deactivated, and not every reader can look an id up (ADR-0011, ADR-0013).
/// </summary>
public sealed record AlarmActor(Guid UserId, string Username);

/// <summary>One entry in the append-only alarm journal (ADR-0013).</summary>
/// <remarks>
/// Events about one alarm name its occurrence, definition, tag and Site; events about the
/// engine name none of them.
/// </remarks>
public sealed record AlarmEvent
{
    public required AlarmEventType Type { get; init; }

    /// <summary>When the engine recorded the event.</summary>
    public required DateTimeOffset RecordedAtUtc { get; init; }

    public Guid? OccurrenceId { get; init; }

    public Guid? DefinitionId { get; init; }

    public Guid? TagId { get; init; }

    /// <summary>Denormalised, so history for soft-deleted equipment still has a Site.</summary>
    public Guid? SiteId { get; init; }

    /// <summary>
    /// When the plant produced the reading behind the event (ADR-0003). Null for events
    /// nothing at the plant produced.
    /// </summary>
    public DateTimeOffset? SourceTimeUtc { get; init; }

    public AlarmActor? Actor { get; init; }

    public AlarmLimit? Limit { get; init; }

    public double? LimitValue { get; init; }

    public double? Value { get; init; }

    public string? UnitSymbol { get; init; }

    /// <summary>The display path as it read at that moment — presentation, never identity.</summary>
    public string? TagPath { get; init; }

    /// <summary>First seen on the first evaluation after a restart: observed to be so, not seen happening.</summary>
    public bool DetectedAfterRestart { get; init; }

    public DateTimeOffset? ShelvedUntilUtc { get; init; }

    /// <summary>Why, where the type alone does not say.</summary>
    public string? Reason { get; init; }

    /// <summary>Start of a period nothing was recorded, or null where it is not known.</summary>
    public DateTimeOffset? GapFromUtc { get; init; }

    public DateTimeOffset? GapUntilUtc { get; init; }

    /// <summary>On a <see cref="AlarmEventType.JournalGap"/>, how many transitions went unrecorded.</summary>
    public int? UnrecordedTransitions { get; init; }
}

/// <summary>
/// Where alarm events are kept. The engine's only persistence dependency, expressed here
/// so Core never learns what a database is (ADR-0002).
/// </summary>
public interface IAlarmJournal
{
    /// <summary>Appends one event.</summary>
    /// <exception cref="Exception">The event was not recorded.</exception>
    Task AppendAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken);

    /// <summary>Every event of every occurrence not yet retired, oldest first.</summary>
    Task<IReadOnlyList<AlarmEvent>> ReadOpenAsync(CancellationToken cancellationToken);
}
