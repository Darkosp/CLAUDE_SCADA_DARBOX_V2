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

    /// <summary>The last known state of one tag, or null if it has never reported.</summary>
    TagSnapshot? GetCurrent(Guid tagId);

    /// <summary>The last known state of every tag that has reported at least once.</summary>
    IReadOnlyList<TagSnapshot> GetAllCurrent();
}
