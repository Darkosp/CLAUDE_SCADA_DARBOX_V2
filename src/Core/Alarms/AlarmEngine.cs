using System.Collections.Concurrent;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Alarms;

/// <summary>Read and acknowledge the alarms currently standing.</summary>
public interface IAlarmEngine
{
    /// <summary>Every alarm not yet resolved, newest first.</summary>
    IReadOnlyList<Alarm> GetCurrent();

    /// <summary>
    /// Marks an alarm as seen.
    /// </summary>
    /// <remarks>
    /// The engine records when, not who. The acknowledging user is written to the audit
    /// trail by whoever calls this (ADR-0011), so the engine stays free of any notion of
    /// users and alarm state stays exactly what it was.
    /// </remarks>
    /// <returns>False when there is no such alarm to acknowledge.</returns>
    Task<bool> AcknowledgeAsync(Guid definitionId, DateTimeOffset atUtc, CancellationToken cancellationToken);

    /// <summary>Suppresses an alarm from the banner while leaving it listed.</summary>
    Task<bool> ShelveAsync(Guid definitionId, CancellationToken cancellationToken);
}

/// <summary>
/// Raises, clears and tracks alarms as tag values arrive.
/// </summary>
/// <remarks>
/// Domain-neutral (ADR-0002): it compares a number against limits it is given and owns
/// the state machine, with no knowledge of what is being measured.
///
/// It runs as a subscriber to the tag engine's fan-out, on the same path that already
/// feeds the historian and the browser, so an alarm is evaluated from exactly the value
/// that was recorded.
/// </remarks>
public sealed class AlarmEngine : IAlarmEngine, ITagValueSubscriber
{
    private readonly TagCatalogSource _catalogSource;
    private readonly IReadOnlyList<IAlarmSubscriber> _subscribers;
    private readonly ConcurrentDictionary<Guid, Alarm> _current = new();

    public AlarmEngine(TagCatalogSource catalogSource, IEnumerable<IAlarmSubscriber> subscribers)
    {
        _catalogSource = catalogSource;
        _subscribers = subscribers.ToList();
    }

    public IReadOnlyList<Alarm> GetCurrent() =>
        _current.Values.OrderByDescending(alarm => alarm.RaisedAtUtc).ToList();

    public async Task<bool> AcknowledgeAsync(
        Guid definitionId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        if (!_current.TryGetValue(definitionId, out var alarm))
        {
            return false;
        }

        // An alarm that already went back to normal has nothing left to watch, so
        // acknowledging it is what finally retires it. One still out of range stays
        // listed, now marked as seen.
        if (alarm.State == AlarmState.Cleared)
        {
            _current.TryRemove(definitionId, out _);
        }
        else
        {
            _current[definitionId] = alarm with
            {
                State = AlarmState.Acknowledged,
                AcknowledgedAtUtc = atUtc,
            };
        }

        await PublishAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ShelveAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        if (!_current.TryGetValue(definitionId, out var alarm))
        {
            return false;
        }

        _current[definitionId] = alarm with { State = AlarmState.Shelved };
        await PublishAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask OnTagValuesAsync(
        IReadOnlyList<TagSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var catalog = _catalogSource.Current;
        var changed = false;

        foreach (var snapshot in snapshots)
        {
            foreach (var definition in catalog.AlarmsOfTag(snapshot.TagId))
            {
                changed |= Evaluate(definition, snapshot, catalog);
            }
        }

        if (changed)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool Evaluate(AlarmDefinition definition, TagSnapshot snapshot, TagCatalog catalog)
    {
        // A reading that is not Good carries no value at all (ADR-0003), so there is
        // nothing to compare. Crucially the standing alarm is left exactly as it is: a
        // device going offline must never look like the value returning to normal.
        if (snapshot.Quality != Quality.Good || snapshot.Value is not TagValue.Numeric numeric)
        {
            return false;
        }

        var breach = Breach(definition, numeric.Value);
        var standing = _current.GetValueOrDefault(definition.Id);

        if (breach is null)
        {
            return standing is null ? false : Clear(definition.Id, standing, snapshot.SourceTimestampUtc);
        }

        if (standing is not null)
        {
            // Already raised. A shelved or acknowledged alarm stays as it is rather than
            // re-announcing itself on every scan while the value remains out of range.
            return false;
        }

        _current[definition.Id] = new Alarm(
            definition.Id,
            snapshot.TagId,
            catalog.PathOf(snapshot.TagId),
            breach.Value.Limit,
            breach.Value.LimitValue,
            numeric.Value,
            snapshot.UnitSymbol,
            snapshot.SourceTimestampUtc,
            AlarmState.Active,
            AcknowledgedAtUtc: null,
            ClearedAtUtc: null);

        return true;
    }

    private bool Clear(Guid definitionId, Alarm standing, DateTimeOffset atUtc)
    {
        switch (standing.State)
        {
            case AlarmState.Acknowledged:
            case AlarmState.Shelved:
                // Seen by an operator and now back to normal: nothing left to show.
                _current.TryRemove(definitionId, out _);
                return true;

            case AlarmState.Active:
                // Never acknowledged. It stays visible so the excursion is not lost
                // simply because the value corrected itself.
                _current[definitionId] = standing with
                {
                    State = AlarmState.Cleared,
                    ClearedAtUtc = atUtc,
                };
                return true;

            default:
                return false;
        }
    }

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

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var current = GetCurrent();

        foreach (var subscriber in _subscribers)
        {
            await subscriber.OnAlarmsChangedAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Receives the alarm list whenever it changes, for push transports.</summary>
public interface IAlarmSubscriber
{
    ValueTask OnAlarmsChangedAsync(IReadOnlyList<Alarm> alarms, CancellationToken cancellationToken);
}
