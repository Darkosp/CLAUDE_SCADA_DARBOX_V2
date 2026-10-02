using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// A device an edge cannot read is refused at the save that assigns it (ADR-0019 §8). The edge's
/// own declaration is the only list that can answer the question — the Gateway's drivers are a
/// different list — and an edge that has declared nothing yet is accepted and shown as such,
/// because "nobody has told us" must never read as "we checked and it is fine".
/// </summary>
public sealed class EdgeDriverRefusalTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeDriverRefusalTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_driver_the_edge_has_declared_it_does_not_have_is_refused_at_the_save_that_assigns_it()
    {
        var admin = await _host.LoginAsAdminAsync();
        var link = await _host.CreateLiveDeviceAsync(admin, Bitola, "Declaring link", FakePushingDriverFactory.Key, scanIntervalMs: null);
        var edgeId = await CreateEdgeAsync(admin, "Declaring edge", link.DeviceId);

        // The edge says, over the link, which drivers its build has: one of this deployment's two.
        await DeclareAsync(edgeId, [FakeDriverFactory.Key]);

        using var client = _host.CreateClient(admin);

        // The driver it declared it has: accepted, and the edge reads the device.
        using var accepted = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices", Save("Readable device", FakeDriverFactory.Key, edgeId));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // The driver it said it does not have: refused, by name, with the keys it does have — rather
        // than accepted, derived, published, and refused only by the edge.
        using var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices", Save("Unreadable device", FakeDriverFactory.OtherKey, edgeId));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains("Declaring edge", error, StringComparison.Ordinal);
        Assert.Contains(FakeDriverFactory.Key, error, StringComparison.Ordinal);

        // The same driver with no edge is untouched: the Gateway reads that device itself.
        using var polled = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices", Save("Polled here", FakeDriverFactory.OtherKey, edgeId: null));
        Assert.Equal(HttpStatusCode.Created, polled.StatusCode);

        // And what the API reports is the edge's own declaration, never the Gateway's driver list.
        var edge = await EdgeAsync(client, edgeId);
        Assert.Equal(
            [FakeDriverFactory.Key],
            edge.GetProperty("declaredDriverKeys").EnumerateArray().Select(key => key.GetString()));
        Assert.NotEqual(JsonValueKind.Null, edge.GetProperty("driversDeclaredAtUtc").ValueKind);
    }

    [RequiresDatabaseFact]
    public async Task An_edge_that_has_declared_nothing_yet_is_accepted_and_says_so()
    {
        var admin = await _host.LoginAsAdminAsync();
        var link = await _host.CreateLiveDeviceAsync(admin, Bitola, "Silent link", FakePushingDriverFactory.Key, scanIntervalMs: null);
        var edgeId = await CreateEdgeAsync(admin, "Silent edge", link.DeviceId);

        using var client = _host.CreateClient(admin);

        // The ordinary order: a plant's devices are configured before its edge is switched on.
        using var created = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices", Save("Configured early", FakeDriverFactory.OtherKey, edgeId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var deviceId = await created.Content.ReadFromJsonAsync<Guid>();

        // The edge reports that nothing has been declared — null, which is not the same answer as
        // "it has none", and is never filled in from the Gateway's own drivers.
        var before = await EdgeAsync(client, edgeId);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("declaredDriverKeys").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("driversDeclaredAtUtc").ValueKind);

        // The edge then declares, without the driver that device uses. The assignment is left as it
        // is — what an edge reads must not change because the edge answered — and the cloud records
        // that it cannot read it.
        await DeclareAsync(edgeId, [FakeDriverFactory.Key]);

        var after = await EdgeAsync(client, edgeId);
        Assert.Equal(
            [FakeDriverFactory.Key],
            after.GetProperty("declaredDriverKeys").EnumerateArray().Select(key => key.GetString()));
        Assert.Contains(deviceId, after.GetProperty("deviceIds").EnumerateArray().Select(id => id.GetGuid()));

        // The declaration's own audit entry is the arriving link's, and is asserted where that link
        // is exercised (EdgeDriverDeclarationsTests): this test writes the declaration the way the
        // link does, through the repository, and the repository records no audit of its own.
    }

    /// <summary>
    /// Makes the declaration the link makes: written through the repository, then read back into the
    /// catalogue, which is what an arriving declaration does (ADR-0019 §8).
    /// </summary>
    private async Task DeclareAsync(Guid edgeId, IReadOnlyList<string> driverKeys)
    {
        await _host.Services.GetRequiredService<IEdgeRepository>()
            .RecordDriversAsync(edgeId, driverKeys, null, DateTimeOffset.UtcNow, CancellationToken.None);
        await _host.Services.GetRequiredService<ConfigurationReloader>().ReloadAsync(CancellationToken.None);
    }

    private async Task<Guid> CreateEdgeAsync(string adminToken, string name, Guid linkDeviceId)
    {
        using var client = _host.CreateClient(adminToken);
        using var created = await client.PostAsJsonAsync("/api/edges", new { name, linkDeviceId });
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private static object Save(string name, string driverKey, Guid? edgeId) => new
    {
        name,
        driverKey,
        connectionSettings = new Dictionary<string, string>(),
        scanIntervalMs = 200,
        folderId = (Guid?)null,
        edgeId,
    };

    private static async Task<JsonElement> EdgeAsync(HttpClient client, Guid edgeId)
    {
        var edges = await client.GetFromJsonAsync<JsonElement>("/api/edges");
        return edges.EnumerateArray().First(edge => edge.GetProperty("id").GetGuid() == edgeId);
    }
}
