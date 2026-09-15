using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// The real Gateway — the same composition and startup checks as production — running on a
/// loopback port against a scratch database, with stand-in drivers in place of devices.
/// </summary>
public sealed class GatewayTestHost : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "correct horse battery staple";
    public const string UserPassword = "a long enough passphrase";

    private ScratchDatabase? _database;
    private WebApplication? _app;

    /// <summary>Serves the seeded demo device and every device a test creates.</summary>
    public FakeDriverFactory Drivers { get; } = new(FakeDriverFactory.Key);

    /// <summary>Everything the Gateway logged, at every level.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public Uri BaseAddress { get; private set; } = null!;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        if (!ScratchDatabase.IsAvailable)
        {
            return;
        }

        _database = await ScratchDatabase.CreateMigratedAsync();

        _app = await GatewayApp.BuildAsync(
            _database.ApplicationArgs(
                "--Sessions:HubSweepInterval=00:00:01",
                $"--initial-admin-username={AdminUsername}",
                $"--initial-admin-password={AdminPassword}"),
            builder =>
            {
                builder.Services.RemoveAll<IDeviceDriverFactory>();
                builder.Services.AddSingleton<IDeviceDriverFactory>(Drivers);

                // Every level, for this provider only: the query-token test must hold even
                // against the most verbose logging someone could switch on.
                builder.Logging.AddProvider(Logs);
                builder.Logging.AddFilter<CapturingLoggerProvider>(category: null, LogLevel.Trace);
            });

        await _app.StartAsync();
        BaseAddress = new Uri(_app.Urls.First());
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    public HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public async Task<string> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    public Task<string> LoginAsAdminAsync() => LoginAsync(AdminUsername, AdminPassword);

    /// <summary>Creates a non-Admin user holding the given Site roles, and logs them in.</summary>
    public async Task<TestUser> CreateUserAsync(string adminToken, params (Guid SiteId, string Role)[] roles)
    {
        var username = $"user-{Guid.NewGuid():N}"[..20];
        using var client = CreateClient(adminToken);

        using var created = await client.PostAsJsonAsync("/api/users", new { username, password = UserPassword, isAdmin = false });
        created.EnsureSuccessStatusCode();
        var userId = await created.Content.ReadFromJsonAsync<Guid>();

        foreach (var (siteId, role) in roles)
        {
            using var granted = await client.PutAsJsonAsync($"/api/users/{userId}/sites/{siteId}", new { role });
            granted.EnsureSuccessStatusCode();
        }

        return new TestUser(userId, username, await LoginAsync(username, UserPassword));
    }

    /// <summary>
    /// Creates a device with one writable numeric tag on <paramref name="siteId"/>, scanned
    /// every 200 ms by the stand-in driver, and returns the tag's id.
    /// </summary>
    public async Task<Guid> CreateLiveTagAsync(string adminToken, Guid siteId, string name)
    {
        using var client = CreateClient(adminToken);

        using var device = await client.PostAsJsonAsync(
            $"/api/sites/{siteId}/devices",
            new SaveDeviceRequest($"{name} device", FakeDriverFactory.Key, new Dictionary<string, string>(), 200, FolderId: null));
        device.EnsureSuccessStatusCode();
        var deviceId = await device.Content.ReadFromJsonAsync<Guid>();

        using var tag = await client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/tags",
            new SaveTagRequest(name, "Numeric", Unit: null, "holding:0", IsWritable: true));
        tag.EnsureSuccessStatusCode();

        return await tag.Content.ReadFromJsonAsync<Guid>();
    }
}

public sealed record TestUser(Guid Id, string Username, string Token);
