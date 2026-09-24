using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// Receives fan-out of tag values as they change. The historian is wired in
/// explicitly rather than as a subscriber; this is for push transports such as the
/// SignalR hub.
/// </summary>
public interface ITagValueSubscriber
{
    ValueTask OnTagValuesAsync(IReadOnlyList<TagSnapshot> snapshots, CancellationToken cancellationToken);
}

/// <summary>
/// Holds the live value of every configured tag and fans changes out to the historian
/// and to push subscribers. Drivers feed it; the Web API and the SignalR hub read from it.
/// </summary>
public interface ITagEngine
{
    /// <summary>
    /// Accepts readings from a driver, updates current values, historizes them, and
    /// pushes them to subscribers.
    /// </summary>
    Task IngestAsync(IReadOnlyList<TagReading> readings, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts samples a pushing driver handed over (ADR-0016): every one goes to history, and
    /// a tag's current value moves only forward in source time. A late sample — no newer than
    /// the tag's last real one — is stored, but changes nothing a reader or an alarm sees.
    /// </summary>
    Task AcceptPushedAsync(IReadOnlyList<TagReading> samples, CancellationToken cancellationToken);

    /// <summary>
    /// Reads silence as loss (ADR-0016): each of <paramref name="tagIds"/> whose current value
    /// arrived longer than <paramref name="stalenessLimit"/> ago becomes Bad, carrying no value
    /// and the time of its last real sample. Nothing is written to history. The tag stays Bad
    /// until a newer sample arrives.
    /// </summary>
    Task MarkSilentTagsAsync(IReadOnlyCollection<Guid> tagIds, TimeSpan stalenessLimit, CancellationToken cancellationToken);

    /// <summary>The last known state of one tag, or null if it has never reported.</summary>
    TagSnapshot? GetCurrent(Guid tagId);

    /// <summary>The last known state of every tag that has reported at least once.</summary>
    IReadOnlyList<TagSnapshot> GetAllCurrent();
}
