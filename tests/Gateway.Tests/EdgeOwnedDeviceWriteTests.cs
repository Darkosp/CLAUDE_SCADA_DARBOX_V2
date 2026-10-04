using System.Net;
using System.Net.Http.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Writing a tag whose device an edge reads (ADR-0023). The Gateway cannot dial the plant, so the
/// write goes to the edge over the link the edge already holds and the caller waits for that edge
/// to say what happened — and a caller is never told a write happened when it did not.
/// </summary>
/// <remarks>
/// This file replaces one that asserted the opposite: that such a write is refused 409 by name. It
/// was, and correctly — the link is outbound only, so the alternative was to open a connection that
/// cannot be made and report a device error that never happened (ADR-0003). ADR-0023 routes the
/// write instead, so the refusal it described is now the case where a deployment has turned writing
/// over the link off, which is asserted below as well.
/// </remarks>
public sealed class EdgeOwnedDeviceWriteTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeOwnedDeviceWriteTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_write_to_an_edge_assigned_tag_is_not_confirmed_when_no_edge_answers()
    {
        // The whole of ADR-0023's honesty rule, in the case a test can produce without an edge: the
        // write is published on the link and nothing answers, so the operator is told exactly that
        // — not that it succeeded, and not that it failed.
        var admin = await _host.LoginAsAdminAsync();
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        var edgeOwned = await _host.CreateLiveDeviceAsync(admin, Bitola, "Edge-owned write probe");
        var gatewayOwned = await _host.CreateLiveDeviceAsync(admin, Bitola, "Gateway-owned write probe");

        var edgeName = await AssignToNewEdgeAsync(admin, edgeOwned.DeviceId);

        using var asOperator = _host.CreateClient(@operator.Token);

        using var routed = await asOperator.PostAsJsonAsync($"/api/tags/{edgeOwned.TagId}/value", new { value = 42 });
        Assert.Equal(HttpStatusCode.GatewayTimeout, routed.StatusCode);

        var message = await routed.Content.ReadAsStringAsync();
        Assert.Contains(edgeName, message);
        Assert.Contains("not confirmed", message);

        // Nothing reached a driver here — the work belongs to the edge, and this Gateway never opens
        // a connection to a device an edge reads (ADR-0019 §3).
        Assert.DoesNotContain(_host.Drivers.Writes, write => write.Tag.TagId == edgeOwned.TagId);

        // And the journal says "not confirmed" rather than "written": the two are different facts
        // and ADR-0003 will not have one stand in for the other.
        Assert.Equal(@operator.Id, Assert.Single(await _host.AuditActorsAsync("tag.write_unconfirmed", edgeOwned.TagId)));
        Assert.Empty(await _host.AuditActorsAsync("tag.write", edgeOwned.TagId));
        Assert.Empty(await _host.AuditActorsAsync("tag.write_failed", edgeOwned.TagId));

        // The control shows the routing is the assignment and not the write path: a device the
        // Gateway still owns is written by the Gateway exactly as before.
        using var allowed = await asOperator.PostAsJsonAsync($"/api/tags/{gatewayOwned.TagId}/value", new { value = 7 });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Single(_host.Drivers.Writes, write => write.Tag.TagId == gatewayOwned.TagId);
        Assert.Single(await _host.AuditActorsAsync("tag.write", gatewayOwned.TagId));
    }

    /// <summary>
    /// Assigns a device to a new edge through the API an operator uses, and hands back the edge's
    /// name so the answer can be checked against it.
    /// </summary>
    /// <remarks>
    /// The link device is not named: the Gateway derives it from the edge (ADR-0022), which is also
    /// why this helper no longer creates one by hand.
    /// </remarks>
    private async Task<string> AssignToNewEdgeAsync(string adminToken, Guid deviceId)
    {
        using var client = _host.CreateClient(adminToken);

        var name = $"edge-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/api/edges", new { name });
        created.EnsureSuccessStatusCode();
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        // Assigning is an ordinary edit of the device (ADR-0019 §2), so the device is saved whole
        // with the edge it now belongs to — the same request the device form makes.
        var device = await client.GetFromJsonAsync<TreeDeviceDto>($"/api/devices/{deviceId}")
            ?? throw new InvalidOperationException($"Device {deviceId} was not found.");

        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}",
            new SaveDeviceRequest(
                device.Name, device.DriverKey, device.ConnectionSettings, device.ScanIntervalMs, device.FolderId, edgeId));
        assigned.EnsureSuccessStatusCode();

        return name;
    }
}
