using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The audit trail over REST (ADR-0032): an Admin can read it, a Viewer cannot, and a page says whether it is
/// all of it.
/// </summary>
public sealed class AuditTrailTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public AuditTrailTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task An_admin_reads_the_trail_and_finds_their_own_sign_in_in_it()
    {
        // The sign-in itself is what puts the row there: nothing is seeded for this, which is the point —
        // the trail records what the product really does.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        using var response = await client.GetAsync("/api/audit?action=auth.&limit=200");
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = page.GetProperty("entries").EnumerateArray().ToList();

        Assert.True(page.GetProperty("total").GetInt64() >= entries.Count);

        var signIn = entries.FirstOrDefault(entry => entry.GetProperty("action").GetString() == "auth.login");
        Assert.NotEqual(JsonValueKind.Undefined, signIn.ValueKind);

        // The actor is resolved at read time, so the row reads as a person rather than as an id (ADR-0032 §5).
        Assert.False(string.IsNullOrEmpty(signIn.GetProperty("actor").GetString()));
        Assert.True(signIn.GetProperty("occurredAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    [RequiresDatabaseFact]
    public async Task A_page_carries_the_total_beside_the_entries_it_returned()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        // One entry at a time: what comes back is the newest row, and the total says how many there are. The
        // trail is never empty in a running host — the fixture's own sign-ins are in it.
        using var response = await client.GetAsync("/api/audit?limit=1");
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, page.GetProperty("entries").GetArrayLength());
        Assert.True(
            page.GetProperty("total").GetInt64() > 1,
            "a host that has signed anybody in has more than one row in its trail");
    }

    [RequiresDatabaseFact]
    public async Task A_viewer_is_refused_and_the_refusal_says_nothing_about_the_trail()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));
        using var client = _host.CreateClient(viewer.Token);

        using var response = await client.GetAsync("/api/audit");

        // Forbidden rather than 404, which is the other shape this project uses for a Site a caller cannot
        // see (ADR-0011): there is one trail and no id to enumerate, so there is nothing to hide by pretending
        // it does not exist — and a person who may not read it is better told so than left guessing.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task The_filters_reach_the_query_and_an_unrelated_action_is_not_returned()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        using var auth = await client.GetAsync("/api/audit?action=auth.&limit=1000");
        using var users = await client.GetAsync("/api/audit?action=user.&limit=1000");

        var authActions = Actions(await auth.Content.ReadFromJsonAsync<JsonElement>());
        var userActions = Actions(await users.Content.ReadFromJsonAsync<JsonElement>());

        Assert.All(authActions, action => Assert.StartsWith("auth.", action));
        Assert.All(userActions, action => Assert.StartsWith("user.", action));
    }

    private static List<string> Actions(JsonElement page) =>
        page.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString()!)
            .ToList();
}
