using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0011's criterion that no password is ever stored or logged in reversible form.
/// </summary>
/// <remarks>
/// Both halves fail silently if they break. A password stored as-is still logs people in;
/// a password written to a log by someone's later "better" request logging raises no error
/// anywhere. Only a test that looks is going to notice.
/// </remarks>
public sealed class CredentialStorageTests : IClassFixture<GatewayTestHost>
{
    private readonly GatewayTestHost _host;

    public CredentialStorageTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_password_is_stored_only_as_a_hash_that_verifies_it()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var asAdmin = _host.CreateClient(admin);

        var username = $"hashed-{Guid.NewGuid():N}"[..20];
        var password = $"stored-{Guid.NewGuid():N}";
        var userId = await CreateUserAsync(asAdmin, username, password);

        AssertStoredOnlyAsHashOf(password, await StoredHashAsync(userId));

        // A reset stores a new hash of the new password — and nothing of either.
        var replacement = $"replaced-{Guid.NewGuid():N}";
        using (var reset = await asAdmin.PutAsJsonAsync($"/api/users/{userId}/password", new { password = replacement }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        var afterReset = await StoredHashAsync(userId);
        AssertStoredOnlyAsHashOf(replacement, afterReset);
        Assert.DoesNotContain(password, afterReset, StringComparison.Ordinal);

        // The initial Admin, which arrived on the command line rather than through the API.
        AssertStoredOnlyAsHashOf(GatewayTestHost.AdminPassword, await StoredHashAsync(GatewayTestHost.AdminUsername));
    }

    [RequiresDatabaseFact]
    public async Task No_password_reaches_a_log_or_the_audit_trail()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var asAdmin = _host.CreateClient(admin);
        using var anonymous = _host.CreateClient();

        var username = $"logged-{Guid.NewGuid():N}"[..20];
        var created = $"created-{Guid.NewGuid():N}";
        var wrong = $"mistyped-{Guid.NewGuid():N}";
        var replacement = $"replaced-{Guid.NewGuid():N}";

        // Every way a password passes through the Gateway: account creation, a successful and
        // a failed login, and a reset followed by a login with the new one.
        var userId = await CreateUserAsync(asAdmin, username, created);
        await _host.LoginAsync(username, created);

        using (var refused = await anonymous.PostAsJsonAsync("/api/auth/login", new { username, password = wrong }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using (var reset = await asAdmin.PutAsJsonAsync($"/api/users/{userId}/password", new { password = replacement }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        await _host.LoginAsync(username, replacement);

        // Those requests really were logged and audited, so finding no password below means
        // it was kept out — not that nothing was written.
        Assert.Contains(_host.Logs.Entries, entry => entry.Contains("/api/auth/login", StringComparison.Ordinal));
        Assert.Contains(_host.Logs.Entries, entry => entry.Contains($"/api/users/{userId}/password", StringComparison.Ordinal));

        var audit = await AuditTrailTextAsync();
        Assert.Contains("user.create", audit, StringComparison.Ordinal);
        Assert.Contains("auth.login_failed", audit, StringComparison.Ordinal);
        Assert.Contains("user.set_password", audit, StringComparison.Ordinal);
        Assert.Contains(username, audit, StringComparison.Ordinal);

        foreach (var secret in new[] { created, wrong, replacement, GatewayTestHost.AdminPassword })
        {
            Assert.DoesNotContain(_host.Logs.Entries, entry => entry.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
    }

    private static void AssertStoredOnlyAsHashOf(string password, string stored)
    {
        Assert.NotEqual(password, stored);
        Assert.DoesNotContain(password, stored, StringComparison.Ordinal);

        // Nor merely encoded: what the stored value decodes to does not hold it either.
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(Convert.FromBase64String(stored)), StringComparison.Ordinal);

        // And a real hash of this password, not just some string that happens not to be it.
        var verified = new PasswordHasher<object>().VerifyHashedPassword(new object(), stored, password);
        Assert.NotEqual(PasswordVerificationResult.Failed, verified);
    }

    private static async Task<Guid> CreateUserAsync(HttpClient asAdmin, string username, string password)
    {
        using var created = await asAdmin.PostAsJsonAsync("/api/users", new { username, password, isAdmin = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private Task<string> StoredHashAsync(Guid userId) =>
        ScalarAsync("SELECT password_hash FROM app_user WHERE id = @key", userId);

    private Task<string> StoredHashAsync(string username) =>
        ScalarAsync("SELECT password_hash FROM app_user WHERE username = @key", username);

    /// <summary>Everything in the audit trail, as text — read over the application's own connection.</summary>
    private Task<string> AuditTrailTextAsync() =>
        ScalarAsync(
            """
            SELECT coalesce(string_agg(
                       concat_ws(' ', action, entity_type, entity_id::text, detail::text), E'\n' ORDER BY id), '')
            FROM audit_log
            """,
            key: null);

    private async Task<string> ScalarAsync(string sql, object? key)
    {
        await using var command = _host.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        if (key is not null)
        {
            command.Parameters.AddWithValue("key", key);
        }

        return (string)(await command.ExecuteScalarAsync())!;
    }
}
