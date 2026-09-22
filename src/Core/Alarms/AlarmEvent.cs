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

    /// <summary>
    /// Appends several events as one unit: either all of them are recorded or none is.
    /// A pair that closes one occurrence and opens its successor has to arrive this way,
    /// or a reader can find two open occurrences on one definition, or a retirement with
    /// no successor (ADR-0013).
    /// </summary>
    /// <exception cref="Exception">The events were not recorded.</exception>
    Task AppendAsync(IReadOnlyList<AlarmEvent> events, CancellationToken cancellationToken);

    /// <summary>Every event of every occurrence not yet retired, oldest first.</summary>
    Task<IReadOnlyList<AlarmEvent>> ReadOpenAsync(CancellationToken cancellationToken);

    /// <summary>History for a reader, newest first, filtered to what they may see.</summary>
    Task<IReadOnlyList<AlarmEvent>> ReadHistoryAsync(AlarmJournalQuery query, CancellationToken cancellationToken);
}

/// <summary>What a reader asks the journal for.</summary>
/// <param name="SiteIds">
/// The Sites the reader may see (ADR-0011). Null means unrestricted — an Admin.
/// An empty list means a reader with no Sites at all, which is not the same thing:
/// they still see the engine's own events, below.
/// </param>
/// <param name="FromUtc">Inclusive lower bound on the recording time; null for no bound.</param>
/// <param name="ToUtc">Exclusive upper bound on the recording time; null for no bound.</param>
/// <param name="Limit">At most this many rows, newest first.</param>
/// <remarks>
/// <para>
/// Events that belong to no Site — <see cref="AlarmEventType.EvaluationStarted"/>,
/// <see cref="AlarmEventType.EvaluationStopped"/> and
/// <see cref="AlarmEventType.JournalGap"/> — are returned to every reader whatever
/// <paramref name="SiteIds"/> says. An evaluation outage applies to the whole Gateway, and
/// a Viewer on one Site still has to know the system was not watching (ADR-0013). A filter
/// written as a bare <c>site_id = ANY(...)</c> drops them, and the outage then reads as a
/// quiet period — the exact wrong answer this journal exists to prevent.
/// </para>
/// <para>
/// This is not the same "no Site" as <see cref="Security.UserAccess.CanViewUnscoped"/>,
/// which is about an alarm whose Site can no longer be resolved and where "no Site" has to
/// mean "not yours". Here the row belongs to no Site by construction, not by loss: an
/// alarm event without a Site is rejected by migration 0009's own CHECK.
/// </para>
/// </remarks>
public sealed record AlarmJournalQuery(
    IReadOnlyList<Guid>? SiteIds,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Limit = 200);
