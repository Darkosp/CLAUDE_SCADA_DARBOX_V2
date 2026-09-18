using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// Pushes the alarm list to connected browsers whenever it changes.
/// </summary>
/// <remarks>
/// The engine only publishes on an actual change, so this does not fire once per scan
/// while a value sits out of range. The list is split by Site and each part goes only to
/// that Site's group (ADR-0011).
/// </remarks>
public sealed class SignalRAlarmBroadcaster : IAlarmSubscriber
{
    private readonly IHubContext<TagHub> _hubContext;
    private readonly TagCatalogSource _catalogSource;

    public SignalRAlarmBroadcaster(IHubContext<TagHub> hubContext, TagCatalogSource catalogSource)
    {
        _hubContext = hubContext;
        _catalogSource = catalogSource;
    }

    public async ValueTask OnAlarmsChangedAsync(
        IReadOnlyList<Alarm> alarms,
        CancellationToken cancellationToken)
    {
        var catalog = _catalogSource.Current;

        // By the Site fixed when each alarm was raised, not by looking its tag up now: an
        // alarm whose tag has since been deleted still belongs somewhere (ADR-0013).
        var bySite = alarms
            .GroupBy(alarm => alarm.SiteId)
            .ToDictionary(group => group.Key, group => group.Select(AlarmDto.From).ToList());

        // Every Site, not only those with alarms: each message is a Site's complete standing
        // list, so a Site whose last alarm just went away has to be told its list is empty.
        foreach (var site in catalog.Sites)
        {
            await _hubContext.Clients.Group(HubConnectionRegistry.GroupOf(site.Id))
                .SendAsync(TagHub.AlarmsMethod, site.Id, bySite.GetValueOrDefault(site.Id) ?? [], cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
