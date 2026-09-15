using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>What a Gateway started the production way looks like from outside.</summary>
public sealed class HostCompositionTests : IClassFixture<GatewayTestHost>
{
    private readonly GatewayTestHost _host;

    public HostCompositionTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task It_runs_as_the_application_role()
    {
        await using var command = _host.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT current_user");
        Assert.Equal(ApplicationRole.Name, await command.ExecuteScalarAsync());
    }

    [RequiresDatabaseFact]
    public async Task Only_health_and_login_answer_without_a_session()
    {
        using var anonymous = _host.CreateClient();

        using (var health = await anonymous.GetAsync("/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        foreach (var path in new[] { "/api/sites", "/api/tags", "/api/alarms", "/api/templates", "/api/users", "/api/auth/me" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{path} answered {response.StatusCode}.");
        }

        using var negotiate = await anonymous.PostAsync("/hubs/tags/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task The_only_account_on_a_fresh_database_is_the_explicitly_supplied_admin()
    {
        // No default credentials: nothing but the account passed on the command line.
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());
        var users = await client.GetFromJsonAsync<JsonElement>("/api/users");

        var only = Assert.Single(users.EnumerateArray());
        Assert.Equal(GatewayTestHost.AdminUsername, only.GetProperty("username").GetString());
        Assert.True(only.GetProperty("isAdmin").GetBoolean());
    }
}
