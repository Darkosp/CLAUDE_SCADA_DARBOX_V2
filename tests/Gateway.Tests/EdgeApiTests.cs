using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The edges of the tenant and which devices they read, through the API (ADR-0019). An edge is
/// tenant-scoped rather than Site-scoped — its name is the identity in its certificate — so the
/// resource is Admin-only, and assigning a device is an ordinary edit of that device.
/// </summary>
public sealed class EdgeApiTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeApiTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task An_edge_is_created_listed_renamed_and_deleted_and_every_change_is_journalled()
    {
        var admin = await _host.LoginAsAdminAsync();

        using var client = _host.CreateClient(admin);

        // Nothing about the link is sent: the Gateway derives it from the edge and the deployment
        // (ADR-0022), and the request no longer has a say.
        using var created = await client.PostAsJsonAsync("/api/edges", new { name = "Boiler House" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        // Listed with the link the Gateway derived for it, whose topic is the edge's own name — so
        // an operator can see what is not typed here, and cannot type it wrong.
        var listed = await EdgeAsync(client, edgeId);
        Assert.Equal("Boiler House", listed.GetProperty("name").GetString());
        var linkId = listed.GetProperty("linkDeviceId").GetGuid();
        Assert.NotEqual(Guid.Empty, linkId);

        var link = await client.GetFromJsonAsync<JsonElement>($"/api/devices/{linkId}");
        Assert.Equal("mqtt", link.GetProperty("driverKey").GetString());
        Assert.Equal(
            "scada/edge/Boiler House/samples",
            link.GetProperty("connectionSettings").GetProperty("topic").GetString());

        using var renamed = await client.PutAsJsonAsync($"/api/edges/{edgeId}", new { name = "Boiler House North" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        var afterRename = await EdgeAsync(client, edgeId);
        Assert.Equal("Boiler House North", afterRename.GetProperty("name").GetString());
        // The link is carried through a rename rather than replaced: the broker keys its session by
        // that device's id, so replacing it would drop the queue held for the edge (ADR-0022 §4).
        Assert.Equal(linkId, afterRename.GetProperty("linkDeviceId").GetGuid());

        using var deleted = await client.DeleteAsync($"/api/edges/{edgeId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(JsonValueKind.Undefined, (await EdgeOrNullAsync(client, edgeId)).ValueKind);

        // Every one of the three is a configuration change, so every one is recorded (ADR-0011).
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.create", edgeId)));
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.update", edgeId)));
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.delete", edgeId)));
    }

    [RequiresDatabaseFact]
    public async Task An_edges_topic_follows_its_name_and_a_link_it_already_has_is_not_replaced()
    {
        // ADR-0022 §1 and §4. The derivation is what makes a mistyped topic impossible, so the
        // topic is the edge's name; and an edge that already has a link keeps the same device,
        // because the broker holds the queue under that device's id.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync("/api/edges", new { name = "derive-b" });
        created.EnsureSuccessStatusCode();
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        var edge = await EdgeAsync(client, edgeId);
        var linkId = edge.GetProperty("linkDeviceId").GetGuid();

        var link = await client.GetFromJsonAsync<JsonElement>($"/api/devices/{linkId}");
        var settings = link.GetProperty("connectionSettings");
        Assert.Equal("scada/edge/derive-b/samples", settings.GetProperty("topic").GetString());
        Assert.Equal("mqtt", link.GetProperty("driverKey").GetString());

        // A second pass — which is what the background service does — writes nothing more.
        var provisioner = _host.Services.GetRequiredService<LinkDeviceProvisioner>();
        await provisioner.ProvisionAsync(CancellationToken.None);

        Assert.Equal(linkId, (await EdgeAsync(client, edgeId)).GetProperty("linkDeviceId").GetGuid());
    }

    [RequiresDatabaseFact]
    public async Task An_edges_link_and_limits_are_its_own_settings_and_a_request_cannot_move_the_link()
    {
        // ADR-0022 §3 and §6. The two limits are the only thing about a link an operator chooses;
        // where it points is not, and a request that tries is ignored rather than obeyed.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var other = await _host.CreateLiveDeviceAsync(admin, Bitola, "A device to point at", FakePushingDriverFactory.Key, scanIntervalMs: null);

        using var created = await client.PostAsJsonAsync(
            "/api/edges",
            new { name = "limits-b", linkDeviceId = other.DeviceId, linkStalenessSeconds = 15, linkSessionExpiryHours = 48 });
        created.EnsureSuccessStatusCode();
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        var edge = await EdgeAsync(client, edgeId);
        // Not the device that was sent: the Gateway's own derivation stands.
        Assert.NotEqual(other.DeviceId, edge.GetProperty("linkDeviceId").GetGuid());
        Assert.Equal(15, edge.GetProperty("linkStalenessSeconds").GetInt32());
        Assert.Equal(48, edge.GetProperty("linkSessionExpiryHours").GetInt32());

        // And the derived device carries them, because the link is where the limit lives.
        var link = await client.GetFromJsonAsync<JsonElement>($"/api/devices/{edge.GetProperty("linkDeviceId").GetGuid()}");
        var settings = link.GetProperty("connectionSettings");
        Assert.Equal("15", settings.GetProperty("stalenessSeconds").GetString());
        Assert.Equal("48", settings.GetProperty("sessionExpiryHours").GetString());

        // A limit that cannot be one is refused by name rather than becoming a device that will not
        // build.
        using var bad = await client.PostAsJsonAsync("/api/edges", new { name = "bad-limit", linkStalenessSeconds = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("positive", await bad.Content.ReadAsStringAsync());
    }

    [RequiresDatabaseFact]
    public async Task An_edge_that_is_created_has_its_link_before_the_answer_arrives()
    {
        // ADR-0022's whole point, seen from the operator's side: there is no window in which an edge
        // exists without a link, so the refusal this replaces — "The edge '...' has no link device
        // yet" — cannot be reached by getting the order wrong.
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "linked-on-create");
        var deviceId = await CreateDeviceAsync(admin, Bitola, "Assignable immediately");

        using var client = _host.CreateClient(admin);
        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assignable immediately", edgeId: edgeId));

        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        Assert.True(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task Assigning_a_device_to_an_edge_and_releasing_it_are_edits_of_that_device()
    {
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "Assignment edge");
        var deviceId = await CreateDeviceAsync(admin, Bitola, "Assigned pump");

        // The edge reports what it reads, and a device it has never been given is not among them.
        Assert.False(await IsReadByEdgeAsync(admin, edgeId, deviceId));

        using var client = _host.CreateClient(admin);
        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assigned pump", edgeId: edgeId));
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        Assert.True(await IsReadByEdgeAsync(admin, edgeId, deviceId));

        // Releasing is the same edit with no edge, and puts the device back in the Gateway's hands.
        using var released = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assigned pump", edgeId: null));
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.False(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task An_edge_is_created_with_its_link_so_no_assignment_is_refused_for_a_missing_one()
    {
        // This test replaces one that asserted the opposite — "An assignment is refused by name while
        // the edge has no link device" — and it is kept beside its old name deliberately, because
        // the refusal it described was real and correct: the Gateway stops polling a device an edge
        // reads (ADR-0019), so an assignment with no link would leave its tags with no source at all.
        // What changed is that the state is no longer reachable (ADR-0022): an edge is created with
        // the link its samples arrive through, so an operator never gets the order wrong.
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "Linked from the start");
        var deviceId = await CreateDeviceAsync(admin, Bitola, "Assignable pump");

        using var client = _host.CreateClient(admin);
        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assignable pump", edgeId: edgeId));

        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        Assert.True(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task A_device_created_already_assigned_is_accepted_the_same_way()
    {
        // Creating and assigning in one request is the same state as assigning afterwards, so it is
        // the same path — and the link that makes it possible was already there (ADR-0022).
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "Assigned create edge");

        using var client = _host.CreateClient(admin);
        using var created = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices",
            new
            {
                name = "Born assigned pump",
                driverKey = FakeDriverFactory.Key,
                connectionSettings = new Dictionary<string, string>(),
                scanIntervalMs = 200,
                folderId = (Guid?)null,
                edgeId,
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var deviceId = await created.Content.ReadFromJsonAsync<Guid>();
        Assert.True(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task The_edges_api_is_admin_only()
    {
        // An edge is scoped to the tenant, not a Site, so there is no Site to answer against and
        // no not-found that would tell a lesser caller less than a refusal does (ADR-0011).
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));

        using var asViewer = _host.CreateClient(viewer.Token);

        using var read = await asViewer.GetAsync("/api/edges");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        using var write = await asViewer.PostAsJsonAsync("/api/edges", new { name = "Not mine", linkDeviceId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        using var anonymous = _host.CreateClient();
        using var signedOut = await anonymous.GetAsync("/api/edges");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    /// <summary>Whether the edge reports the device among the ones it reads.</summary>
    private async Task<bool> IsReadByEdgeAsync(string adminToken, Guid edgeId, Guid deviceId)
    {
        using var client = _host.CreateClient(adminToken);
        var edge = await EdgeAsync(client, edgeId);

        return edge.GetProperty("deviceIds").EnumerateArray()
            .Any(candidate => candidate.GetGuid() == deviceId);
    }

    private async Task<Guid> CreateEdgeAsync(string adminToken, string name)
    {
        using var client = _host.CreateClient(adminToken);
        using var created = await client.PostAsJsonAsync("/api/edges", new { name });
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateDeviceAsync(string adminToken, Guid siteId, string name)
    {
        using var client = _host.CreateClient(adminToken);
        using var created = await client.PostAsJsonAsync($"/api/sites/{siteId}/devices", Save(name));
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private static object Save(string name, Guid? edgeId = null) => new
    {
        name,
        driverKey = FakeDriverFactory.Key,
        connectionSettings = new Dictionary<string, string>(),
        scanIntervalMs = 200,
        folderId = (Guid?)null,
        edgeId,
    };

    private static async Task<JsonElement> EdgeAsync(HttpClient client, Guid edgeId)
    {
        var edge = await EdgeOrNullAsync(client, edgeId);
        return edge.ValueKind == JsonValueKind.Undefined
            ? throw new InvalidOperationException($"Edge {edgeId} is not in the list.")
            : edge;
    }

    private static async Task<JsonElement> EdgeOrNullAsync(HttpClient client, Guid edgeId)
    {
        var edges = await client.GetFromJsonAsync<JsonElement>("/api/edges");
        return edges.EnumerateArray()
            .FirstOrDefault(edge => edge.GetProperty("id").GetGuid() == edgeId);
    }
}
