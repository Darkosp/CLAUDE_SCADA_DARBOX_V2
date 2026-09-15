using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// The real-time push channel to web clients (ADR-0002, ADR-0006).
/// </summary>
/// <remarks>
/// Values and alarms are pushed to one group per Site, and a connection is only ever in the
/// groups of Sites its user may currently see (ADR-0011). Membership is owned by
/// <see cref="HubConnectionRegistry"/>, which changes it on connections that are already
/// open when access changes — filtering once at connect time would look right and let a
/// revoked user keep receiving. The two pull methods apply the same rule, re-read on every
/// call.
/// </remarks>
[Authorize]
public sealed class TagHub : Hub
{
    /// <summary>Name of the client-side method the server invokes with new values.</summary>
    public const string TagValuesMethod = "tagValues";

    /// <summary>
    /// Name of the client-side method the server invokes with <c>(siteId, alarms)</c>: the
    /// complete standing list for that one Site, which replaces the client's previous list
    /// for it.
    /// </summary>
    public const string AlarmsMethod = "alarms";

    /// <summary>
    /// Tells a connection that what it is allowed to see has changed, so the client
    /// reloads its current state instead of keeping values it may no longer see
    /// (ADR-0011).
    /// </summary>
    public const string AccessChangedMethod = "accessChanged";

    private readonly ITagEngine _tagEngine;
    private readonly IAlarmEngine _alarmEngine;
    private readonly TagCatalogSource _catalogSource;
    private readonly UserDirectorySource _users;
    private readonly HubConnectionRegistry _registry;

    public TagHub(
        ITagEngine tagEngine,
        IAlarmEngine alarmEngine,
        TagCatalogSource catalogSource,
        UserDirectorySource users,
        HubConnectionRegistry registry)
    {
        _tagEngine = tagEngine;
        _alarmEngine = alarmEngine;
        _catalogSource = catalogSource;
        _users = users;
        _registry = registry;
    }

    public override async Task OnConnectedAsync()
    {
        if (SessionClaims.UserIdOf(Context.User) is not { } userId
            || SessionClaims.SessionIdOf(Context.User) is not { } sessionId)
        {
            Context.Abort();
            return;
        }

        await _registry.RegisterAsync(Context, userId, sessionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// The current value of every tag the caller may see, so a client that connects between
    /// scans renders immediately instead of waiting for the next change.
    /// </summary>
    public IReadOnlyList<TagSnapshotDto> GetCurrentValues()
    {
        var access = Caller.From(Context.User, _users.Current).Access;
        var catalog = _catalogSource.Current;

        return _tagEngine.GetAllCurrent()
            .Where(snapshot => access.CanSeeTag(catalog, snapshot.TagId))
            .Select(TagSnapshotDto.From)
            .ToList();
    }

    /// <summary>
    /// The standing alarms the caller may see, so a client that connects long after an
    /// alarm was raised still shows it rather than waiting for the next change.
    /// </summary>
    public IReadOnlyList<AlarmDto> GetCurrentAlarms()
    {
        var access = Caller.From(Context.User, _users.Current).Access;
        var catalog = _catalogSource.Current;

        return _alarmEngine.GetCurrent()
            .Where(alarm => access.CanSeeTag(catalog, alarm.TagId))
            .Select(alarm => AlarmDto.From(alarm, catalog.SiteOfTag(alarm.TagId)))
            .ToList();
    }
}
