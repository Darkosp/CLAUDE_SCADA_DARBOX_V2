using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0011's Site rule over REST: no data for a Site without a role, and no way to tell a
/// hidden resource from a missing one.
/// </summary>
public sealed class SiteScopingTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public SiteScopingTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_user_with_no_role_on_a_site_gets_nothing_for_it_from_any_listed_rest_path()
    {
        var admin = await _host.LoginAsAdminAsync();
        var elsewhere = await _host.CreateLiveDeviceAsync(admin, Bitola, "Scoping probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, elsewhere.TagId);
        await _host.WaitForHistoryAsync(admin, elsewhere.TagId);
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        using var asAdmin = _host.CreateClient(admin);
        using var asViewer = _host.CreateClient(viewer.Token);

        var byId = new[]
        {
            $"/api/tags/{elsewhere.TagId}",
            $"/api/tags/{elsewhere.TagId}/alarms",
            $"/api/tags/{elsewhere.TagId}/history",
            // The reduced read is the same read: asking for points must not become a way around the
            // Site rule the path without it enforces (ADR-0029).
            $"/api/tags/{elsewhere.TagId}/history?points=60",
            $"/api/devices/{elsewhere.DeviceId}",
            $"/api/sites/{Bitola}/tree",
        };

        foreach (var path in byId)
        {
            // Each one first as Admin, so every refusal below hides something really there.
            using (var shown = await asAdmin.GetAsync(path))
            {
                Assert.True(shown.StatusCode == HttpStatusCode.OK, $"Admin: {path} answered {(int)shown.StatusCode}.");
            }

            using var hidden = await asViewer.GetAsync(path);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"Viewer: {path} answered {(int)hidden.StatusCode}.");
        }

        var sites = await IdsAsync(asViewer, "/api/sites", "id");
        Assert.Contains(Skopje, sites);
        Assert.DoesNotContain(Bitola, sites);
        Assert.Contains(Bitola, await IdsAsync(asAdmin, "/api/sites", "id"));

        var tags = await IdsAsync(asViewer, "/api/tags", "tagId");
        Assert.Contains(DemoConfigurationSeeder.DischargePressureTagId, tags);
        Assert.DoesNotContain(elsewhere.TagId, tags);
        Assert.Contains(elsewhere.TagId, await IdsAsync(asAdmin, "/api/tags", "tagId"));

        Assert.DoesNotContain(definitionId, await IdsAsync(asViewer, "/api/alarms", "definitionId"));
        Assert.Contains(definitionId, await IdsAsync(asAdmin, "/api/alarms", "definitionId"));
    }

    [RequiresDatabaseFact]
    public async Task An_id_on_another_site_answers_exactly_as_an_id_that_does_not_exist()
    {
        var admin = await _host.LoginAsAdminAsync();
        var elsewhere = await _host.CreateLiveDeviceAsync(admin, Bitola, "Enumeration probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, elsewhere.TagId);

        // An Operator, not a Viewer: on their own Site these writes would succeed, so a 404
        // here is the Site rule and cannot be a role refusal in disguise.
        var @operator = await _host.CreateUserAsync(admin, (Skopje, "Operator"));
        using var client = _host.CreateClient(@operator.Token);

        var missing = Guid.NewGuid();

        var cases = new (string Label, Func<Guid, Task<HttpResponseMessage>> Send, Guid Existing)[]
        {
            ("tag", id => client.GetAsync($"/api/tags/{id}"), elsewhere.TagId),
            ("tag alarms", id => client.GetAsync($"/api/tags/{id}/alarms"), elsewhere.TagId),
            ("tag history", id => client.GetAsync($"/api/tags/{id}/history"), elsewhere.TagId),
            ("device", id => client.GetAsync($"/api/devices/{id}"), elsewhere.DeviceId),
            ("site tree", id => client.GetAsync($"/api/sites/{id}/tree"), Bitola),
            ("tag write", id => client.PostAsJsonAsync($"/api/tags/{id}/value", new { value = 1 }), elsewhere.TagId),
            ("acknowledge", id => client.PostAsync($"/api/alarms/{id}/acknowledge", content: null), definitionId),
            ("shelve", id => client.PostAsJsonAsync($"/api/alarms/{id}/shelve", new { durationMinutes = 60 }), definitionId),
        };

        foreach (var (label, send, existing) in cases)
        {
            var hidden = await DescribeAsync(send(existing));
            var absent = await DescribeAsync(send(missing));

            Assert.True(hidden.Status == (int)HttpStatusCode.NotFound, $"{label}: an id on another Site answered {hidden}.");
            Assert.True(hidden == absent, $"{label}: another Site's id answered {hidden}, a missing id answered {absent}.");
        }

        // Still standing and untouched by the refused acknowledge and shelve.
        Assert.Equal("Active", await _host.AlarmStateAsync(admin, definitionId));
    }

    [RequiresDatabaseFact]
    public async Task History_of_a_deleted_tag_is_still_authorised_by_its_own_site()
    {
        var admin = await _host.LoginAsAdminAsync();
        var retired = await _host.CreateLiveDeviceAsync(admin, Bitola, "Retired probe");
        await _host.WaitForHistoryAsync(admin, retired.TagId);

        using (var asAdmin = _host.CreateClient(admin))
        using (var deleted = await asAdmin.DeleteAsync($"/api/devices/{retired.DeviceId}/tags/{retired.TagId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var bitolaViewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));
        var skopjeViewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));
        var path = $"/api/tags/{retired.TagId}/history?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}";

        // Gone from the live catalogue, so only the base tables still know its Site. Its own
        // Site's viewer keeps the history; "no longer configured" must not make it everyone's.
        using (var own = _host.CreateClient(bitolaViewer.Token))
        {
            var history = await own.GetFromJsonAsync<JsonElement>(path);
            Assert.True(history.GetProperty("isDeleted").GetBoolean());
            Assert.True(history.GetProperty("samples").GetArrayLength() > 0);
        }

        using var other = _host.CreateClient(skopjeViewer.Token);
        using var refused = await other.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    private static async Task<IReadOnlyList<Guid>> IdsAsync(HttpClient client, string path, string property)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(path);
        return body.EnumerateArray().Select(item => item.GetProperty(property).GetGuid()).ToList();
    }

    private static async Task<Response> DescribeAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return new Response(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>Everything a caller can observe about an answer, for comparing two of them.</summary>
    private sealed record Response(int Status, string? ContentType, string Body);
}
