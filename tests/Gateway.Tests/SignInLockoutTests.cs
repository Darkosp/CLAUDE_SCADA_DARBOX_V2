using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The sign-in path over REST (ADR-0031): enough wrong passwords shut the account, a shut account is told
/// so, and the response headers close the classes of attack that cost nothing to close.
/// </summary>
/// <remarks>
/// **The account locked here is a purpose-made one, not the Admin.** A lock is a real state in a real
/// database, and locking the Admin would make every other test in this class fail for a reason that has
/// nothing to do with what it is testing — the shape of "rule and its application drifting apart" that this
/// repository keeps finding.
///
/// The lock **window** is not waited out here: fifteen minutes of a test run is not a test. That the window
/// expires, and that an expired window starts a fresh count, is proved in `SignInLockoutTests` where the
/// clock is an argument rather than a thing to wait for.
/// </remarks>
public sealed class SignInLockoutTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private const int DefaultAttempts = 5;

    private readonly GatewayTestHost _host;

    public SignInLockoutTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task Enough_wrong_passwords_shut_the_account_and_it_says_so()
    {
        var admin = await _host.LoginAsAdminAsync();
        var user = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));
        using var client = _host.CreateClient();

        for (var attempt = 1; attempt < DefaultAttempts; attempt++)
        {
            using var refused = await WrongPasswordAsync(client, user.Username);

            // Below the threshold the answer is the one that does not say which half was wrong, and it is
            // the same answer an unknown name gets.
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("Wrong user name or password.", await ErrorAsync(refused));
        }

        // **The failure that reaches the threshold is the one that answers "locked"**, not the one after
        // it: the person who just used their last attempt is told now, rather than being told their password
        // was wrong and discovering the lock on the next try.
        using var locking = await WrongPasswordAsync(client, user.Username);
        Assert.Equal(HttpStatusCode.Locked, locking.StatusCode);

        var message = await ErrorAsync(locking);
        Assert.Contains("Too many failed sign-ins", message);
        Assert.Contains("(UTC)", message);
        Assert.Contains("an Admin can reset the password", message);

        // And a shut account is refused whatever it carries — the right password included.
        using var shut = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = user.Username, password = GatewayTestHost.UserPassword });

        Assert.Equal(HttpStatusCode.Locked, shut.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_wrong_sign_in_against_a_name_that_does_not_exist_still_says_only_that_it_failed()
    {
        // The decoy-hash path (ADR-0011) and the counter meet here: a name with no row has nothing to count
        // against, and it must not become a different answer just because it was tried five times.
        using var client = _host.CreateClient();

        for (var attempt = 0; attempt < DefaultAttempts + 2; attempt++)
        {
            using var refused = await WrongPasswordAsync(client, $"nobody-{Guid.NewGuid():N}"[..20]);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
    }

    [RequiresDatabaseFact]
    public async Task Every_response_carries_the_headers_that_cost_nothing( )
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));

        var policy = Header(response, "Content-Security-Policy") ?? string.Empty;
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("base-uri 'self'", policy);

        // **`script-src` is set, and the inline theme script is allowed by hash rather than by
        // `'unsafe-inline'`** (ADR-0031 §8). The host serves no built client, so there is no inline script
        // of ours here and the source is the bare `'self'` — which is the case that has to be right, because
        // a hash for a script that is not there allows nothing and looks like a policy. What the hashing
        // itself does is pinned in ScriptHashTests, where the expected digest is computed from the script's
        // own text rather than copied from a run.
        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("unsafe-inline", policy);

        Assert.NotNull(Header(response, "Permissions-Policy"));
    }

    private static Task<HttpResponseMessage> WrongPasswordAsync(HttpClient client, string username) =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password = "not the right password at all" });

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
}
