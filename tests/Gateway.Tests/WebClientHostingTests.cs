using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Phase 6: the Gateway serves the web client itself, so the browser talks to one origin and
/// no cross-origin allowance exists.
/// </summary>
/// <remarks>
/// Run against a web root holding a stand-in client rather than a real Angular build: what is
/// under test is the Gateway's hosting, not the client. The image build puts the real one there.
/// </remarks>
public sealed class WebClientHostingTests : IAsyncLifetime
{
    private const string Marker = "scada-darbox-test-client";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"scada-webroot-{Guid.NewGuid():N}");
    private ScratchDatabase? _database;
    private WebApplication? _app;
    private Uri _baseAddress = null!;

    public async Task InitializeAsync()
    {
        if (!ScratchDatabase.IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(_webRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_webRoot, "index.html"),
            $"<!doctype html><html><body>{Marker}</body></html>");

        _database = await ScratchDatabase.CreateMigratedAsync();
        _app = await GatewayApp.BuildAsync(
            _database.ApplicationArgs($"--webroot={_webRoot}"),
            builder =>
            {
                builder.Services.RemoveAll<IDeviceDriverFactory>();
                builder.Services.AddSingleton<IDeviceDriverFactory>(new FakeDriverFactory(FakeDriverFactory.Key));
            });

        await _app.StartAsync();
        _baseAddress = new Uri(_app.Urls.First());
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

        if (Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }

    [RequiresDatabaseFact]
    public async Task The_client_is_served_from_the_gateways_own_origin_before_anyone_signs_in()
    {
        using var client = new HttpClient { BaseAddress = _baseAddress };

        // No token: the sign-in screen has to load before there is a session.
        using var root = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.Contains(Marker, await root.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task The_page_that_names_the_client_bundles_is_never_reused_without_asking()
    {
        // Found after the Phase 6.5 upgrade: with no caching instruction a browser may keep a
        // previous build's index.html — and so the previous client — for hours.
        using var client = new HttpClient { BaseAddress = _baseAddress };

        foreach (var path in new[] { "/", "/index.html" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoCache, $"{path} is served without Cache-Control: no-cache");
        }
    }

    [RequiresDatabaseFact]
    public async Task Serving_the_client_opens_nothing_else_without_a_session()
    {
        // The control for the test above: the files are public, the API and the hub are not.
        using var client = new HttpClient { BaseAddress = _baseAddress };

        using var tags = await client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.Unauthorized, tags.StatusCode);

        using var negotiate = await client.PostAsync("/hubs/tags/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task No_other_origin_is_allowed_to_call_the_api()
    {
        // The origin the Angular dev server used to be allowed from, and one nobody configured.
        foreach (var origin in new[] { "http://localhost:4200", "http://elsewhere.example" })
        {
            using var client = new HttpClient { BaseAddress = _baseAddress };

            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
            using var preflightResponse = await client.SendAsync(preflight);

            using var simple = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            simple.Headers.Add("Origin", origin);
            using var simpleResponse = await client.SendAsync(simple);

            // The simple request is answered, so an absent header below is a refusal and not
            // a request that never arrived.
            Assert.Equal(HttpStatusCode.OK, simpleResponse.StatusCode);

            Assert.False(
                preflightResponse.Headers.Contains("Access-Control-Allow-Origin"),
                $"preflight from {origin} was granted");
            Assert.False(
                simpleResponse.Headers.Contains("Access-Control-Allow-Origin"),
                $"request from {origin} was granted");
        }
    }
}
