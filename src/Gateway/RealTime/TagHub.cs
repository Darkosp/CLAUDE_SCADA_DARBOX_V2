using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// The real-time push channel to web clients (ADR-0002, ADR-0006). Phase 1 pushes every
/// tag to every connected client; per-client subscriptions arrive with the browse tree.
/// </summary>
public sealed class TagHub : Hub
{
    /// <summary>Name of the client-side method the server invokes with new values.</summary>
    public const string TagValuesMethod = "tagValues";

    /// <summary>Name of the client-side method the server invokes with the alarm list.</summary>
    public const string AlarmsMethod = "alarms";

    private readonly ITagEngine _tagEngine;
    private readonly IAlarmEngine _alarmEngine;

    public TagHub(ITagEngine tagEngine, IAlarmEngine alarmEngine)
    {
        _tagEngine = tagEngine;
        _alarmEngine = alarmEngine;
    }

    /// <summary>
    /// Sends the current value of every known tag to the caller, so a client that
    /// connects between scans renders immediately instead of waiting for the next change.
    /// </summary>
    public Task<IReadOnlyList<TagSnapshotDto>> GetCurrentValues() =>
        Task.FromResult<IReadOnlyList<TagSnapshotDto>>(
            _tagEngine.GetAllCurrent().Select(TagSnapshotDto.From).ToList());

    /// <summary>
    /// Sends the standing alarms to the caller, so a client that connects long after an
    /// alarm was raised still shows it rather than waiting for the next change.
    /// </summary>
    public Task<IReadOnlyList<AlarmDto>> GetCurrentAlarms() =>
        Task.FromResult<IReadOnlyList<AlarmDto>>(
            _alarmEngine.GetCurrent().Select(AlarmDto.From).ToList());
}
