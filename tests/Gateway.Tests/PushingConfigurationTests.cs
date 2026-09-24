using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0016 in configuration and on the wire: a pushing device has no scan interval, and a
/// pushing tag that never receives anything reads Bad as "no data since", never as a value or
/// a measured time.
/// </summary>
public sealed class PushingConfigurationTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public PushingConfigurationTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_scan_interval_for_a_pushing_device_is_refused_and_nothing_is_created()
    {
        using var admin = await AdminAsync();
        var name = Unique("Edge");

        using var response = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices",
            Device(name, FakePushingDriverFactory.Key, scanIntervalMs: 1000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("has no scan interval", error, StringComparison.Ordinal);
        Assert.Equal(0, await CountDevicesAsync(name));
    }

    [RequiresDatabaseFact]
    public async Task A_pushing_device_without_one_is_created_and_shows_none()
    {
        using var admin = await AdminAsync();

        using var response = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices",
            Device(Unique("Edge"), FakePushingDriverFactory.Key, scanIntervalMs: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = await response.Content.ReadFromJsonAsync<Guid>();

        // Not the stored default: a field that exists but does nothing is not shown.
        var device = await admin.GetFromJsonAsync<JsonElement>($"/api/devices/{id}");
        Assert.Equal(JsonValueKind.Null, device.GetProperty("scanIntervalMs").ValueKind);
    }

    [RequiresDatabaseFact]
    public async Task A_polled_device_still_needs_its_scan_interval()
    {
        // The control: the rule is about pushing devices, not about scan intervals in general.
        using var admin = await AdminAsync();
        var name = Unique("Pump");

        using var response = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices",
            Device(name, FakeDriverFactory.Key, scanIntervalMs: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountDevicesAsync(name));

        using var withOne = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices",
            Device(name, FakeDriverFactory.Key, scanIntervalMs: 500));
        Assert.Equal(HttpStatusCode.Created, withOne.StatusCode);
        var device = await admin.GetFromJsonAsync<JsonElement>($"/api/devices/{await withOne.Content.ReadFromJsonAsync<Guid>()}");
        Assert.Equal(500, device.GetProperty("scanIntervalMs").GetInt32());
    }

    [RequiresDatabaseFact]
    public async Task A_scan_interval_is_refused_for_a_pushing_device_made_from_a_template_too()
    {
        var token = await _host.LoginAsAdminAsync();
        using var admin = _host.CreateClient(token);
        var template = await _host.CreateTemplateAsync(token, Unique("Edge template"));
        var name = Unique("Edge");

        using var response = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices/from-template",
            new InstantiateDeviceRequest(template, name, FakePushingDriverFactory.Key, new Dictionary<string, string>(), 1000, FolderId: null, new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountDevicesAsync(name));
    }

    [RequiresDatabaseFact]
    public async Task The_driver_list_says_which_drivers_push()
    {
        using var admin = await AdminAsync();

        var drivers = (await admin.GetFromJsonAsync<JsonElement>("/api/drivers"))
            .EnumerateArray()
            .ToDictionary(d => d.GetProperty("key").GetString()!, d => d.GetProperty("pushing").GetBoolean());

        Assert.True(drivers[FakePushingDriverFactory.Key]);
        Assert.False(drivers[FakeDriverFactory.Key]);
    }

    [RequiresDatabaseFact]
    public async Task A_pushing_tag_that_never_receives_anything_reads_Bad_with_no_data_since()
    {
        using var admin = await AdminAsync();
        var before = DateTimeOffset.UtcNow;

        using var created = await admin.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices",
            Device(Unique("Never"), SilentPushingDriverFactory.Key, scanIntervalMs: null));
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<Guid>();

        using var tagResponse = await admin.PostAsJsonAsync(
            $"/api/devices/{device}/tags",
            new SaveTagRequest("Pressure", "Numeric", Unit: null, "pressure", IsWritable: false));
        tagResponse.EnsureSuccessStatusCode();
        var tagId = await tagResponse.Content.ReadFromJsonAsync<Guid>();

        JsonElement current = default;
        await GatewayTestHost.WaitUntilAsync(
            async () =>
            {
                using var response = await admin.GetAsync($"/api/tags/{tagId}");
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                current = await response.Content.ReadFromJsonAsync<JsonElement>();
                return current.GetProperty("noDataSinceUtc").ValueKind != JsonValueKind.Null;
            },
            "the never-heard tag to read 'no data since'",
            TimeSpan.FromSeconds(10));

        // Bad, with no value and no measured time — there was no measurement to give one.
        Assert.Equal("Bad", current.GetProperty("quality").GetString());
        Assert.Equal("none", current.GetProperty("value").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("sourceTimestampUtc").ValueKind);

        // The observed time: when the Gateway began listening, which was after the device was made.
        var since = current.GetProperty("noDataSinceUtc").GetDateTimeOffset();
        Assert.InRange(since, before, DateTimeOffset.UtcNow);

        // And nothing in history, because nothing was measured.
        var from = Uri.EscapeDataString(before.AddMinutes(-1).ToString("O"));
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/tags/{tagId}/history?from={from}");
        Assert.Empty(history.GetProperty("samples").EnumerateArray());
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static SaveDeviceRequest Device(string name, string driverKey, int? scanIntervalMs) =>
        new(name, driverKey, new Dictionary<string, string>(), scanIntervalMs, FolderId: null);

    private async Task<HttpClient> AdminAsync() => _host.CreateClient(await _host.LoginAsAdminAsync());

    private async Task<long> CountDevicesAsync(string name)
    {
        await using var command = _host.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("SELECT count(*) FROM device WHERE name = @name");
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
