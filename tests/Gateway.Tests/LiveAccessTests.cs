using System.Net;
using ScadaDarbox.Gateway.RealTime;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0011's live-connection criteria: access changes reach a SignalR connection that is
/// already open, not only the next one.
/// </summary>
/// <remarks>
/// The criterion most likely to pass on paper. Filtering once when a connection opens looks
/// correct in every test that connects after the change; only a connection that was already
/// open and already receiving can show whether a revocation actually reaches it.
/// </remarks>
public sealed class LiveAccessTests : IClassFixture<GatewayTestHost>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Messages already on the wire when a group change completes may still land; what the
    /// criterion is about is everything sent after it. The stand-in driver scans every
    /// 200 ms, so this is several scans' worth of margin.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    /// <summary>At least ten scans of the probe tag.</summary>
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(2);

    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public LiveAccessTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task Removing_a_site_role_stops_values_arriving_on_the_same_open_connection()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveTagAsync(admin, Bitola, "Revocation probe");
        var viewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));

        await using var watched = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token);
        await using var control = await HubTestClient.ConnectAsync(_host.BaseAddress, admin);

        // Receiving first. Without this, "nothing arrived afterwards" could only mean that
        // nothing was ever sent.
        await watched.WaitForTagAsync(probe, Timeout);

        using (var client = _host.CreateClient(admin))
        {
            using var revoked = await client.DeleteAsync($"/api/users/{viewer.Id}/sites/{Bitola}");
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        await watched.WaitForAsync(TagHub.AccessChangedMethod, Timeout);
        await Task.Delay(Settle);
        watched.Clear();
        control.Clear();

        await Task.Delay(ObservationWindow);

        // Still being broadcast — so silence on the watched connection is the revocation,
        // not a scan that stopped.
        Assert.Contains(probe, control.TagIdsReceived());
        Assert.DoesNotContain(probe, watched.TagIdsReceived());

        // The same connection, still open and still answering: not a reconnect that happened
        // to pick up the new rules.
        Assert.False(watched.Closed.IsCompleted);
        var current = await watched.InvokeAsync(nameof(TagHub.GetCurrentValues), Timeout);
        Assert.DoesNotContain(current.EnumerateArray(), snapshot => snapshot.GetProperty("tagId").GetGuid() == probe);
    }

    [RequiresDatabaseFact]
    public async Task A_connection_never_receives_values_from_a_site_it_has_no_role_on()
    {
        var admin = await _host.LoginAsAdminAsync();
        var elsewhere = await _host.CreateLiveTagAsync(admin, Bitola, "Other site probe");
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        await using var watched = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token);
        await using var control = await HubTestClient.ConnectAsync(_host.BaseAddress, admin);

        // Its own Site's values do arrive, and the other Site's are being sent to someone.
        await watched.WaitForTagAsync(DemoConfigurationSeeder.DischargePressureTagId, Timeout);
        await control.WaitForTagAsync(elsewhere, Timeout);

        await Task.Delay(ObservationWindow);

        Assert.DoesNotContain(elsewhere, watched.TagIdsReceived());

        var current = await watched.InvokeAsync(nameof(TagHub.GetCurrentValues), Timeout);
        var visible = current.EnumerateArray().Select(snapshot => snapshot.GetProperty("tagId").GetGuid()).ToList();
        Assert.Contains(DemoConfigurationSeeder.DischargePressureTagId, visible);
        Assert.DoesNotContain(elsewhere, visible);
    }

    [RequiresDatabaseFact]
    public async Task A_connection_never_receives_alarms_from_a_site_it_has_no_role_on()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        await using var watched = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token);
        await using var control = await HubTestClient.ConnectAsync(_host.BaseAddress, admin);

        // Raised after both connections are open, so its push is observed rather than missed.
        var elsewhere = await _host.CreateLiveTagAsync(admin, Bitola, "Other site alarm probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, elsewhere);

        // The alarm is being pushed to someone, and alarm pushes do reach the watched
        // connection — its own Site's list — so its silence about Bitola is not silence
        // about everything.
        await WaitForAlarmAsync(control, definitionId);
        await watched.WaitForAsync(TagHub.AlarmsMethod, Timeout);

        await Task.Delay(Settle);

        var pushedToWatched = watched.Received(TagHub.AlarmsMethod);
        Assert.DoesNotContain(pushedToWatched, message => message.Arguments[0].GetGuid() == Bitola);
        Assert.DoesNotContain(definitionId, AlarmDefinitionIds(pushedToWatched));

        // The snapshot a connection asks for on arrival is filtered the same way.
        var current = await watched.InvokeAsync(nameof(TagHub.GetCurrentAlarms), Timeout);
        Assert.DoesNotContain(
            definitionId,
            current.EnumerateArray().Select(alarm => alarm.GetProperty("definitionId").GetGuid()));

        var controlCurrent = await control.InvokeAsync(nameof(TagHub.GetCurrentAlarms), Timeout);
        Assert.Contains(
            definitionId,
            controlCurrent.EnumerateArray().Select(alarm => alarm.GetProperty("definitionId").GetGuid()));
    }

    private static IEnumerable<Guid> AlarmDefinitionIds(IEnumerable<HubMessage> messages) =>
        messages
            .SelectMany(message => message.Arguments[1].EnumerateArray())
            .Select(alarm => alarm.GetProperty("definitionId").GetGuid());

    private static async Task WaitForAlarmAsync(HubTestClient client, Guid definitionId)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!AlarmDefinitionIds(client.Received(TagHub.AlarmsMethod)).Contains(definitionId))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No push carrying alarm {definitionId} arrived within {Timeout}.");
            }

            await Task.Delay(50);
        }
    }

    [RequiresDatabaseFact]
    public async Task Deactivating_a_user_closes_their_open_connection_and_refuses_their_next_request()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        await using var watched = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token);
        await watched.WaitForTagAsync(DemoConfigurationSeeder.DischargePressureTagId, Timeout);

        using (var client = _host.CreateClient(admin))
        {
            using var deactivated = await client.DeleteAsync($"/api/users/{viewer.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
        }

        await watched.Closed.WaitAsync(Timeout);

        using var asViewer = _host.CreateClient(viewer.Token);
        using var refused = await asViewer.GetAsync("/api/sites");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Logging_out_ends_that_session_and_its_connection_while_other_sessions_keep_working()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));
        var otherSession = await _host.LoginAsync(viewer.Username, GatewayTestHost.UserPassword);

        await using var watched = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token);

        using (var client = _host.CreateClient(viewer.Token))
        {
            using var loggedOut = await client.PostAsync("/api/auth/logout", content: null);
            Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

            using var afterLogout = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        }

        await watched.Closed.WaitAsync(Timeout);

        using var other = _host.CreateClient(otherSession);
        using var stillWorking = await other.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillWorking.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_query_string_token_works_only_on_the_hub_and_never_reaches_a_log()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        // Accepted on the hub: this client puts the token in the WebSocket URL, as a browser does.
        await using (var hub = await HubTestClient.ConnectAsync(_host.BaseAddress, viewer.Token))
        {
            await hub.WaitForTagAsync(DemoConfigurationSeeder.DischargePressureTagId, Timeout);
        }

        // Refused anywhere else.
        using (var anonymous = _host.CreateClient())
        {
            using var refused = await anonymous.GetAsync($"/api/sites?access_token={Uri.EscapeDataString(viewer.Token)}");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // The hub requests were logged — with their other query parameters — so the absence
        // of the token below is the scrubbing, not an absence of logging.
        Assert.Contains(_host.Logs.Entries, entry => entry.Contains("/hubs/tags?id=", StringComparison.Ordinal));
        Assert.Contains(_host.Logs.Entries, entry => entry.Contains("/api/sites", StringComparison.Ordinal));

        Assert.DoesNotContain(_host.Logs.Entries, entry => entry.Contains(viewer.Token, StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Logs.Entries, entry => entry.Contains("access_token", StringComparison.OrdinalIgnoreCase));
    }
}
