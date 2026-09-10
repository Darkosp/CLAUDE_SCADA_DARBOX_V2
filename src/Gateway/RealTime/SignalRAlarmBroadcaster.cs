using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// Pushes the alarm list to connected browsers whenever it changes.
/// </summary>
/// <remarks>
/// The engine only publishes on an actual change, so this does not fire once per scan
/// while a value sits out of range.
/// </remarks>
public sealed class SignalRAlarmBroadcaster : IAlarmSubscriber
{
    private readonly IHubContext<TagHub> _hubContext;

    public SignalRAlarmBroadcaster(IHubContext<TagHub> hubContext) => _hubContext = hubContext;

    public async ValueTask OnAlarmsChangedAsync(
        IReadOnlyList<Alarm> alarms,
        CancellationToken cancellationToken)
    {
        var payload = alarms.Select(AlarmDto.From).ToList();

        await _hubContext.Clients.All
            .SendAsync(TagHub.AlarmsMethod, payload, cancellationToken)
            .ConfigureAwait(false);
    }
}
