using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// Fans tag values out to connected browsers — the SignalR half of the tag engine's
/// fan-out, alongside the historian write.
/// </summary>
/// <remarks>
/// Each value goes only to its own Site's group (ADR-0011), never to every connection.
/// </remarks>
public sealed class SignalRTagBroadcaster : ITagValueSubscriber
{
    private readonly IHubContext<TagHub> _hubContext;
    private readonly TagCatalogSource _catalogSource;

    public SignalRTagBroadcaster(IHubContext<TagHub> hubContext, TagCatalogSource catalogSource)
    {
        _hubContext = hubContext;
        _catalogSource = catalogSource;
    }

    public async ValueTask OnTagValuesAsync(
        IReadOnlyList<TagSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var catalog = _catalogSource.Current;

        foreach (var site in snapshots.GroupBy(snapshot => catalog.SiteOfTag(snapshot.TagId)))
        {
            // A value whose tag has left the catalogue has no Site, and so goes to nobody
            // rather than to everybody.
            if (site.Key is not { } siteId)
            {
                continue;
            }

            await _hubContext.Clients.Group(HubConnectionRegistry.GroupOf(siteId))
                .SendAsync(TagHub.TagValuesMethod, site.Select(TagSnapshotDto.From).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
