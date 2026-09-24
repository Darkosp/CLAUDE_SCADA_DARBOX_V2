using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0016 through the running Gateway: a device whose driver pushes is started rather than
/// polled, and when it falls silent its tags read Bad at the time of the last thing measured.
/// </summary>
/// <remarks>
/// The Core tests prove the rule; this proves the Gateway applies it — that the scanner starts a
/// pushing driver, routes its samples to the pushed path, and keeps checking for silence while
/// the driver is quiet. Removing that check leaves the tag Good for ever, which is what a
/// cached last value would have done.
/// </remarks>
public sealed class PushingDeviceTests : IClassFixture<GatewayTestHost>
{
    private readonly GatewayTestHost _host;

    public PushingDeviceTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_pushing_device_that_falls_silent_reads_Bad_at_its_last_measured_time()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync(
            $"/api/sites/{DemoConfigurationSeeder.SiteId}/devices",
            new SaveDeviceRequest($"Edge {Guid.NewGuid():N}"[..13], FakePushingDriverFactory.Key, new Dictionary<string, string>(), 1000, FolderId: null));
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<Guid>();

        using var tagResponse = await client.PostAsJsonAsync(
            $"/api/devices/{device}/tags",
            new SaveTagRequest("Pressure", "Numeric", Unit: null, "pressure", IsWritable: false));
        tagResponse.EnsureSuccessStatusCode();
        var tagId = await tagResponse.Content.ReadFromJsonAsync<Guid>();

        // The one sample the source sent.
        JsonElement live = default;
        await GatewayTestHost.WaitUntilAsync(
            async () => (live = await CurrentAsync(client, tagId)).ValueKind == JsonValueKind.Object
                        && live.GetProperty("quality").GetString() == "Good",
            "the pushed sample to become current");
        Assert.Equal(FakePushingDriverFactory.FirstValue, live.GetProperty("value").GetProperty("numeric").GetDouble());
        var measuredAt = live.GetProperty("sourceTimestampUtc").GetDateTimeOffset();

        // Then silence, past the source's one-second limit.
        JsonElement lost = default;
        await GatewayTestHost.WaitUntilAsync(
            async () => (lost = await CurrentAsync(client, tagId)).GetProperty("quality").GetString() == "Bad",
            "the silent tag to read Bad",
            TimeSpan.FromSeconds(10));

        Assert.Equal(measuredAt, lost.GetProperty("sourceTimestampUtc").GetDateTimeOffset());
        Assert.Equal("none", lost.GetProperty("value").GetProperty("kind").GetString());

        // History holds what was measured and nothing for the silence.
        await _host.WaitForHistoryAsync(admin, tagId);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/tags/{tagId}/history?from={from}");
        var sample = Assert.Single(history.GetProperty("samples").EnumerateArray());
        Assert.Equal("Good", sample.GetProperty("quality").GetString());
    }

    private static async Task<JsonElement> CurrentAsync(HttpClient client, Guid tagId)
    {
        using var response = await client.GetAsync($"/api/tags/{tagId}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }
}
