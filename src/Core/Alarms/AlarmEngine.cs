using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Alarms;

/// <summary>Read the standing alarms and take operator action on them.</summary>
public interface IAlarmEngine
{
    /// <summary>Every alarm not yet retired, newest first.</summary>
    IReadOnlyList<Alarm> GetCurrent();

    /// <summary>
    /// Marks an alarm as seen by <paramref name="actor"/>.
    /// </summary>
    /// <remarks>
    /// The engine records who as well as when. Until Phase 5.5 it deliberately knew nothing
    /// of users and left naming the actor to the caller's audit entry; that stopped being
    /// right once the journal had to answer "who acknowledged this" after a restart, from
    /// its own rows (ADR-0013). The audit entry is still written — by the caller, for
    /// security's own record — and the two are not duplicates of each other.
    /// </remarks>
    /// <returns>False when there is no such alarm to acknowledge.</returns>
    /// <exception cref="AlarmJournalUnavailableException">
    /// The acknowledgement could not be recorded, so it did not happen.
    /// </exception>
    Task<bool> AcknowledgeAsync(Guid definitionId, AlarmActor actor, CancellationToken cancellationToken);

    /// <summary>Suppresses an alarm from the banner for <paramref name="duration"/>.</summary>
    /// <exception cref="AlarmJournalUnavailableException">
    /// The shelve could not be recorded, so it did not happen.
    /// </exception>
    Task<ShelveOutcome> ShelveAsync(
        Guid definitionId,
        AlarmActor actor,
        TimeSpan duration,
        CancellationToken cancellationToken);

    /// <summary>The longest an alarm may be shelved for.</summary>
    TimeSpan MaxShelveDuration { get; }
}

public enum ShelveOutcome
{
    Shelved,
    NotFound,

    /// <summary>Already back in range: there is nothing to suppress, only something to acknowledge.</summary>
    NotInAlarm,

    /// <summary>Not positive, or longer than <see cref="IAlarmEngine.MaxShelveDuration"/>.</summary>
    DurationNotAllowed,
}

public sealed class AlarmEngineOptions
{
    /// <summary>
    /// The longest a shelf may last (ADR-0013). A suppression meant to be permanent is a
    /// configuration change, made by an Admin, not a shelf.
    /// </summary>
    public TimeSpan MaxShelveDuration { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>An operator action that could not be recorded in the journal, and so did not happen.</summary>
public sealed class AlarmJournalUnavailableException(Exception inner)
    : Exception("The alarm journal could not record this action, so it was not carried out.", inner);

/// <summary>
/// Raises, clears and tracks alarms as tag values arrive, recording every transition in the
/// alarm journal and rebuilding from it at startup (ADR-0013).
/// </summary>
/// <remarks>
/// Domain-neutral (ADR-0002): it compares a number against limits it is given and owns
/// the state machine, with no knowledge of what is being measured.
///
/// It runs as a subscriber to the tag engine's fan-out, on the same path that already
/// feeds the historian and the browser, so an alarm is evaluated from exactly the value
/// that was recorded. Every transition is serialised through one gate, so the journal's
/// order is the order things happened in.
/// </remarks>
public sealed class AlarmEngine : IAlarmEngine, ITagValueSubscriber
{
    /// <summary>The reason recorded when an alarm is retired because its definition was removed.</summary>
    public const string DefinitionRemovedReason = "definition-removed";

    /// <summary>
    /// The reason recorded when startup finds an occurrence whose ending was recorded but whose
    /// retirement was not — the Gateway stopped between the two writes.
    /// </summary>
    public const string CompletedAtStartupReason = "retirement-completed-at-startup";

    /// <summary>The reason recorded when a newer occurrence on the same definition replaces an older open one.</summary>
    public const string SupersededReason = "superseded";

    private readonly TagCatalogSource _catalogSource;
    private readonly IReadOnlyList<IAlarmSubscriber> _subscribers;
    private readonly IAlarmJournal _journal;
    private readonly TimeProvider _clock;
    private readonly AlarmEngineOptions _options;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Alarm> _byDefinition = [];
    private readonly HashSet<Guid> _awaitingFirstEvaluation = [];
    private IReadOnlyList<Alarm> _snapshot = [];
    private bool _started;

    // A window of failed journal writes, and what it cost. Reported as one JournalGap once
    // writing works again (ADR-0013).
    private DateTimeOffset? _journalFailingSince;
    private int _unrecorded;

    public AlarmEngine(
        TagCatalogSource catalogSource,
        IEnumerable<IAlarmSubscriber> subscribers,
        IAlarmJournal journal,
        TimeProvider clock,
        AlarmEngineOptions options)
    {
        _catalogSource = catalogSource;
        _subscribers = subscribers.ToList();
        _journal = journal;
        _clock = clock;
        _options = options;

        _catalogSource.Changed += OnCatalogChanged;
    }

    public TimeSpan MaxShelveDuration => _options.MaxShelveDuration;

    public IReadOnlyList<Alarm> GetCurrent() => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Rebuilds the live list from the journal and begins evaluating.
    /// </summary>
    /// <param name="lastAliveUtc">
    /// The last moment the Gateway is known to have been alive before this start, recorded as
    /// the start of the gap — after an unclean stop, the only honest bound on it.
    /// </param>
    /// <exception cref="Exception">
    /// The journal could not be read or written. Startup must fail: evaluating without the
    /// standing alarms, or without recording that evaluation began, would be silently wrong.
    /// </exception>
    public async Task StartAsync(DateTimeOffset? lastAliveUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            var rebuilt = Rebuild(await _journal.ReadOpenAsync(cancellationToken).ConfigureAwait(false));

            await _journal.AppendAsync(
                new AlarmEvent
                {
                    Type = AlarmEventType.EvaluationStarted,
                    RecordedAtUtc = now,
                    // A last-alive time later than now is a clock disagreement, not a negative
                    // outage; the gap is then empty rather than inverted.
                    GapFromUtc = lastAliveUtc is { } alive ? (alive < now ? alive : now) : null,
                    GapUntilUtc = now,
                },
                cancellationToken).ConfigureAwait(false);

            // Occurrences whose ending was written but whose retirement was not — the
            // Gateway stopped between the two writes — are closed now, so they do not return
            // at every future start.
            foreach (var (alarm, reason) in rebuilt.ToRetire)
            {
                await _journal.AppendAsync(Event(alarm, AlarmEventType.Retired, now) with { Reason = reason }, cancellationToken)
                    .ConfigureAwait(false);
            }

            var catalog = _catalogSource.Current;
            var liveDefinitions = catalog.Alarms.Select(definition => definition.Id).ToHashSet();

            foreach (var alarm in rebuilt.Open)
            {
                if (liveDefinitions.Contains(alarm.DefinitionId))
                {
                    _byDefinition[alarm.DefinitionId] = alarm;
                }
                else
                {
                    // Removed while the Gateway was down: nothing will ever evaluate it again.
                    await _journal.AppendAsync(
                        Event(alarm, AlarmEventType.Retired, now) with { Reason = DefinitionRemovedReason },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            _awaitingFirstEvaluation.Clear();
            _awaitingFirstEvaluation.UnionWith(liveDefinitions);
            _started = true;
            PublishSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        await PublishAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that evaluation stopped cleanly.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            await RecordAsync(
                new AlarmEvent { Type = AlarmEventType.EvaluationStopped, RecordedAtUtc = _clock.GetUtcNow() },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AcknowledgeAsync(Guid definitionId, AlarmActor actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_byDefinition.TryGetValue(definitionId, out var alarm))
            {
                return false;
            }

            var now = _clock.GetUtcNow();

            // Recorded first: an acknowledgement nobody can later prove happened is worse than
            // a button that reports it did not work (ADR-0013).
            await RecordOrThrowAsync(Event(alarm, AlarmEventType.Acknowledged, now) with { Actor = actor }, cancellationToken)
                .ConfigureAwait(false);

            // An alarm that already went back to normal has nothing left to watch, so
            // acknowledging it is what finally retires it. One still out of range stays
            // listed, now marked as seen.
            if (alarm.State == AlarmState.Cleared)
            {
                _byDefinition.Remove(definitionId);
                await RecordAsync(Event(alarm, AlarmEventType.Retired, now), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _byDefinition[definitionId] = alarm with
                {
                    State = AlarmState.Acknowledged,
                    AcknowledgedAtUtc = now,
                    AcknowledgedBy = actor,
                    ShelvedUntilUtc = null,
                };
            }

            PublishSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        await PublishAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ShelveOutcome> ShelveAsync(
        Guid definitionId,
        AlarmActor actor,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > _options.MaxShelveDuration)
        {
            return ShelveOutcome.DurationNotAllowed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_byDefinition.TryGetValue(definitionId, out var alarm))
            {
                return ShelveOutcome.NotFound;
            }

            if (alarm.State == AlarmState.Cleared)
            {
                return ShelveOutcome.NotInAlarm;
            }

            var now = _clock.GetUtcNow();
            var until = now + duration;

            await RecordOrThrowAsync(
                Event(alarm, AlarmEventType.Shelved, now) with { Actor = actor, ShelvedUntilUtc = until },
                cancellationToken).ConfigureAwait(false);

            _byDefinition[definitionId] = alarm with { State = AlarmState.Shelved, ShelvedUntilUtc = until };
            PublishSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        await PublishAsync(cancellationToken).ConfigureAwait(false);
        return ShelveOutcome.Shelved;
    }

    /// <summary>
    /// Returns every alarm whose shelf has run out to <see cref="AlarmState.Active"/>.
    /// </summary>
    /// <remarks>
    /// Driven by a clock rather than by readings, so an alarm on a device that has gone
    /// offline — no readings at all — still comes back. An expiry that falls while the value
    /// is unknown still unshelves: unknown is not the same as fine (ADR-0013). What the alarm
    /// was before it was shelved is not remembered; one that had been acknowledged needs
    /// acknowledging again, which is the price of having hidden it.
    /// </remarks>
    public async Task ExpireShelvesAsync(CancellationToken cancellationToken)
    {
        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();

            foreach (var alarm in _byDefinition.Values.ToList())
            {
                if (alarm.State != AlarmState.Shelved || alarm.ShelvedUntilUtc is not { } until || until > now)
                {
                    continue;
                }

                _byDefinition[alarm.DefinitionId] = alarm with
                {
                    State = AlarmState.Active,
                    ShelvedUntilUtc = null,
                    AcknowledgedAtUtc = null,
                    AcknowledgedBy = null,
                };
                await RecordAsync(Event(alarm, AlarmEventType.Unshelved, now), cancellationToken).ConfigureAwait(false);
                changed = true;
            }

            if (changed)
            {
                PublishSnapshot();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retires every standing alarm whose definition is no longer configured.
    /// </summary>
    /// <remarks>
    /// Nothing evaluates a removed definition, so such an alarm could never clear by itself;
    /// rebuilt from the journal, it would return at every start, forever (ADR-0013).
    /// </remarks>
    public async Task ReconcileWithCatalogAsync(CancellationToken cancellationToken)
    {
        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            var live = _catalogSource.Current.Alarms.Select(definition => definition.Id).ToHashSet();
            var now = _clock.GetUtcNow();

            foreach (var alarm in _byDefinition.Values.Where(alarm => !live.Contains(alarm.DefinitionId)).ToList())
            {
                _byDefinition.Remove(alarm.DefinitionId);
                await RecordAsync(
                    Event(alarm, AlarmEventType.Retired, now) with { Reason = DefinitionRemovedReason },
                    cancellationToken).ConfigureAwait(false);
                changed = true;
            }

            if (changed)
            {
                PublishSnapshot();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnTagValuesAsync(
        IReadOnlyList<TagSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            var catalog = _catalogSource.Current;

            foreach (var snapshot in snapshots)
            {
                foreach (var definition in catalog.AlarmsOfTag(snapshot.TagId))
                {
                    changed |= await EvaluateAsync(definition, snapshot, catalog, cancellationToken).ConfigureAwait(false);
                }
            }

            if (changed)
            {
                PublishSnapshot();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> EvaluateAsync(
        AlarmDefinition definition,
        TagSnapshot snapshot,
        TagCatalog catalog,
        CancellationToken cancellationToken)
    {
        // A reading that is not Good carries no value at all (ADR-0003), so there is
        // nothing to compare — and it is not an evaluation, so it does not use up the
        // first-evaluation-after-restart mark. Crucially the standing alarm is left exactly
        // as it is: a device going offline must never look like the value returning to
        // normal.
        if (snapshot.Quality != Quality.Good || snapshot.Value is not TagValue.Numeric numeric)
        {
            return false;
        }

        var afterRestart = _awaitingFirstEvaluation.Remove(definition.Id);
        var breach = Breach(definition, numeric.Value);
        _byDefinition.TryGetValue(definition.Id, out var standing);
        var now = _clock.GetUtcNow();

        if (breach is null)
        {
            return standing is not null
                   && await ClearAsync(standing, snapshot, numeric.Value, afterRestart, catalog, now, cancellationToken)
                       .ConfigureAwait(false);
        }

        if (standing is not null)
        {
            // Already raised. A shelved or acknowledged alarm stays as it is rather than
            // re-announcing itself on every scan while the value remains out of range.
            return false;
        }

        if (SiteOf(snapshot.TagId, catalog) is not { } siteId)
        {
            return false;
        }

        var alarm = new Alarm(
            Guid.NewGuid(),
            definition.Id,
            snapshot.TagId,
            siteId,
            catalog.PathOf(snapshot.TagId),
            breach.Value.Limit,
            breach.Value.LimitValue,
            numeric.Value,
            snapshot.UnitSymbol,
            snapshot.SourceTimestampUtc,
            AlarmState.Active,
            AcknowledgedAtUtc: null,
            ClearedAtUtc: null)
        {
            DetectedAfterRestart = afterRestart,
        };

        // The live list first, the journal second: an operator in front of a screen needs
        // to see the alarm more than the database needs to have recorded it (ADR-0013).
        _byDefinition[definition.Id] = alarm;
        await RecordAsync(
            Event(alarm, AlarmEventType.Raised, now) with
            {
                SourceTimeUtc = snapshot.SourceTimestampUtc,
                Value = numeric.Value,
                DetectedAfterRestart = afterRestart,
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> ClearAsync(
        Alarm standing,
        TagSnapshot snapshot,
        double value,
        bool afterRestart,
        TagCatalog catalog,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cleared = Event(standing, AlarmEventType.Cleared, now, catalog) with
        {
            SourceTimeUtc = snapshot.SourceTimestampUtc,
            Value = value,
            DetectedAfterRestart = afterRestart,
        };

        switch (standing.State)
        {
            case AlarmState.Acknowledged:
            case AlarmState.Shelved:
                // Seen by an operator and now back to normal: nothing left to show.
                _byDefinition.Remove(standing.DefinitionId);
                await RecordAsync(cleared, cancellationToken).ConfigureAwait(false);
                await RecordAsync(Event(standing, AlarmEventType.Retired, now, catalog), cancellationToken)
                    .ConfigureAwait(false);
                return true;

            case AlarmState.Active:
                // Never acknowledged. It stays visible so the excursion is not lost simply
                // because the value corrected itself.
                _byDefinition[standing.DefinitionId] = standing with
                {
                    State = AlarmState.Cleared,
                    ClearedAtUtc = snapshot.SourceTimestampUtc,
                };
                await RecordAsync(cleared, cancellationToken).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Records a transition the engine made on its own — raised, cleared, unshelved,
    /// retired. A failed write does not undo the transition; it is counted, and reported as
    /// a <see cref="AlarmEventType.JournalGap"/> once writing works again (ADR-0013).
    /// </summary>
    private async Task RecordAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken)
    {
        try
        {
            await ReportGapAsync(alarmEvent.RecordedAtUtc, cancellationToken).ConfigureAwait(false);
            await _journal.AppendAsync(alarmEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _journalFailingSince ??= alarmEvent.RecordedAtUtc;
            _unrecorded++;
        }
    }

    /// <summary>
    /// Records an operator action before it takes effect. A failure refuses the action rather
    /// than counting it as lost: nothing happened, so nothing went unrecorded.
    /// </summary>
    private async Task RecordOrThrowAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken)
    {
        try
        {
            await ReportGapAsync(alarmEvent.RecordedAtUtc, cancellationToken).ConfigureAwait(false);
            await _journal.AppendAsync(alarmEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AlarmJournalUnavailableException(exception);
        }
    }

    /// <summary>Writes the pending <see cref="AlarmEventType.JournalGap"/>, if journalling had been failing.</summary>
    private async Task ReportGapAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_journalFailingSince is not { } from || _unrecorded == 0)
        {
            return;
        }

        await _journal.AppendAsync(
            new AlarmEvent
            {
                Type = AlarmEventType.JournalGap,
                RecordedAtUtc = now,
                GapFromUtc = from,
                GapUntilUtc = now,
                UnrecordedTransitions = _unrecorded,
            },
            cancellationToken).ConfigureAwait(false);

        _journalFailingSince = null;
        _unrecorded = 0;
    }

    private void OnCatalogChanged(object? sender, TagCatalog catalog) => _ = ReconcileQuietlyAsync();

    private async Task ReconcileQuietlyAsync()
    {
        try
        {
            await ReconcileWithCatalogAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Journal failures are already counted inside; this only guards the push to
            // subscribers, which must not become an unobserved task exception. The next
            // catalogue change or restart reconciles again.
        }
    }

    /// <summary>
    /// Folds the events of open occurrences back into standing alarms (ADR-0013).
    /// </summary>
    internal static RebuildResult Rebuild(IReadOnlyList<AlarmEvent> events)
    {
        var byOccurrence = new Dictionary<Guid, Alarm>();
        var raisedOrder = new List<Guid>();
        var finished = new HashSet<Guid>();

        foreach (var alarmEvent in events)
        {
            if (alarmEvent.OccurrenceId is not { } occurrence)
            {
                continue;
            }

            if (alarmEvent.Type == AlarmEventType.Raised)
            {
                byOccurrence[occurrence] = new Alarm(
                    occurrence,
                    alarmEvent.DefinitionId!.Value,
                    alarmEvent.TagId!.Value,
                    alarmEvent.SiteId!.Value,
                    alarmEvent.TagPath ?? string.Empty,
                    alarmEvent.Limit ?? AlarmLimit.High,
                    alarmEvent.LimitValue ?? 0,
                    alarmEvent.Value ?? 0,
                    alarmEvent.UnitSymbol,
                    alarmEvent.SourceTimeUtc ?? alarmEvent.RecordedAtUtc,
                    AlarmState.Active,
                    AcknowledgedAtUtc: null,
                    ClearedAtUtc: null)
                {
                    DetectedAfterRestart = alarmEvent.DetectedAfterRestart,
                };
                raisedOrder.Add(occurrence);
                continue;
            }

            if (!byOccurrence.TryGetValue(occurrence, out var alarm))
            {
                continue;
            }

            byOccurrence[occurrence] = alarmEvent.Type switch
            {
                AlarmEventType.Acknowledged when alarm.State == AlarmState.Cleared => MarkFinished(alarm),
                AlarmEventType.Acknowledged => alarm with
                {
                    State = AlarmState.Acknowledged,
                    AcknowledgedAtUtc = alarmEvent.RecordedAtUtc,
                    AcknowledgedBy = alarmEvent.Actor,
                    ShelvedUntilUtc = null,
                },
                AlarmEventType.Shelved => alarm with
                {
                    State = AlarmState.Shelved,
                    ShelvedUntilUtc = alarmEvent.ShelvedUntilUtc,
                },
                AlarmEventType.Unshelved => alarm with
                {
                    State = AlarmState.Active,
                    ShelvedUntilUtc = null,
                    AcknowledgedAtUtc = null,
                    AcknowledgedBy = null,
                },
                AlarmEventType.Cleared when alarm.State == AlarmState.Active => alarm with
                {
                    State = AlarmState.Cleared,
                    ClearedAtUtc = alarmEvent.SourceTimeUtc ?? alarmEvent.RecordedAtUtc,
                },
                AlarmEventType.Cleared => MarkFinished(alarm),
                _ => alarm,
            };
        }

        var open = new List<Alarm>();
        var toRetire = new List<(Alarm, string)>();

        foreach (var occurrence in finished)
        {
            toRetire.Add((byOccurrence[occurrence], CompletedAtStartupReason));
        }

        // One definition holds at most one standing alarm. Should the journal ever show two
        // open on the same definition, the newest stands and the older is closed, rather than
        // either silently shadowing the other at every start.
        foreach (var group in raisedOrder
                     .Where(occurrence => !finished.Contains(occurrence))
                     .Select(occurrence => byOccurrence[occurrence])
                     .GroupBy(alarm => alarm.DefinitionId))
        {
            var ordered = group.ToList();
            open.Add(ordered[^1]);
            toRetire.AddRange(ordered.Take(ordered.Count - 1).Select(older => (older, SupersededReason)));
        }

        return new RebuildResult(open, toRetire);

        Alarm MarkFinished(Alarm alarm)
        {
            finished.Add(alarm.OccurrenceId);
            return alarm;
        }
    }

    private AlarmEvent Event(Alarm alarm, AlarmEventType type, DateTimeOffset now, TagCatalog? catalog = null)
    {
        catalog ??= _catalogSource.Current;

        return new AlarmEvent
        {
            Type = type,
            RecordedAtUtc = now,
            OccurrenceId = alarm.OccurrenceId,
            DefinitionId = alarm.DefinitionId,
            TagId = alarm.TagId,
            SiteId = alarm.SiteId,
            Limit = alarm.Limit,
            LimitValue = alarm.LimitValue,
            UnitSymbol = alarm.UnitSymbol,

            // As it reads now, if the tag is still configured — a journal should show what
            // the operator saw at that moment, and a rename since the raise is part of that.
            TagPath = catalog.FindTag(alarm.TagId) is null ? alarm.TagPath : catalog.PathOf(alarm.TagId),
        };
    }

    private static Guid? SiteOf(Guid tagId, TagCatalog catalog) =>
        catalog.FindTag(tagId) is { } tag && catalog.FindDevice(tag.DeviceId) is { } device ? device.SiteId : null;

    private static (AlarmLimit Limit, double LimitValue)? Breach(AlarmDefinition definition, double value)
    {
        if (definition.HighLimit is { } high && value >= high)
        {
            return (AlarmLimit.High, high);
        }

        if (definition.LowLimit is { } low && value <= low)
        {
            return (AlarmLimit.Low, low);
        }

        return null;
    }

    /// <summary>Called with the gate held, after every change to the live list.</summary>
    private void PublishSnapshot() =>
        Volatile.Write(
            ref _snapshot,
            _byDefinition.Values.OrderByDescending(alarm => alarm.RaisedAtUtc).ToList());

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var current = GetCurrent();

        foreach (var subscriber in _subscribers)
        {
            await subscriber.OnAlarmsChangedAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>What startup rebuilt from the journal.</summary>
/// <param name="Open">The alarms still standing.</param>
/// <param name="ToRetire">Occurrences that must be closed now, and why.</param>
internal sealed record RebuildResult(IReadOnlyList<Alarm> Open, IReadOnlyList<(Alarm Alarm, string Reason)> ToRetire);

/// <summary>Receives the alarm list whenever it changes, for push transports.</summary>
public interface IAlarmSubscriber
{
    ValueTask OnAlarmsChangedAsync(IReadOnlyList<Alarm> alarms, CancellationToken cancellationToken);
}
