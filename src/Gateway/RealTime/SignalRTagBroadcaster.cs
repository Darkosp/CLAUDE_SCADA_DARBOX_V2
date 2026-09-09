using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// Fans tag values out to connected browsers — the SignalR half of the tag engine's
/// fan-out, alongside the historian write.
/// </summary>
public sealed class SignalRTagBroadcaster : ITagValueSubscriber
{
    private readonly IHubContext<TagHub> _hubContext;

    public SignalRTagBroadcaster(IHubContext<TagHub> hubContext) => _hubContext = hubContext;

    public async ValueTask OnTagValuesAsync(
        IReadOnlyList<TagSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var payload = snapshots.Select(TagSnapshotDto.From).ToList();

        await _hubContext.Clients.All
            .SendAsync(TagHub.TagValuesMethod, payload, cancellationToken)
            .ConfigureAwait(false);
    }
}
