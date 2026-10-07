using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0030 over REST: a tag may declare the range its readings are expected in, a reading outside it
/// keeps its own value and its own quality and is marked beside them, and a range that is not a range is
/// refused by name.
/// </summary>
/// <remarks>
/// The Core tests prove the arithmetic and the schema tests prove the constraints. This proves what a
/// client actually receives: the verdict, the ends it was compared against, the untouched reading, and
/// the **absence** of all three on a tag that has declared nothing — which is the case that would
/// otherwise quietly become "in range".
/// </remarks>
public sealed class DeclaredRangeTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public DeclaredRangeTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_reading_above_its_declared_range_keeps_its_value_and_says_which_side()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Range probe");

        // The stand-in driver counts upwards on every read, so a span of 0–1 is left behind almost at once.
        using var saved = await client.PutAsJsonAsync(
            $"/api/devices/{device.DeviceId}/tags/{device.TagId}",
            new SaveTagRequest("Range probe", "Numeric", Unit: null, "holding:0", IsWritable: true, RangeLow: 0, RangeHigh: 1));
        saved.EnsureSuccessStatusCode();

        JsonElement reading = default;
        await GatewayTestHost.WaitUntilAsync(
            async () => (reading = await CurrentAsync(client, device.TagId)).GetProperty("rangeStatus").ValueKind == JsonValueKind.String,
            "the reading to carry a range status");

        Assert.Equal("AboveRange", reading.GetProperty("rangeStatus").GetString());
        Assert.Equal(0, reading.GetProperty("rangeLow").GetDouble());
        Assert.Equal(1, reading.GetProperty("rangeHigh").GetDouble());

        // **The value is the reading, not a repair of it.** A clamp would have answered 1 here, and a
        // substitution would have answered nothing at all; both are the fabrication ADR-0003 refuses and
        // ADR-0030 §2 forbids. The quality is the driver's own: out of range is not a quality (the same
        // separation OPC UA keeps between a status code and its limit bits).
        Assert.True(
            reading.GetProperty("value").GetProperty("numeric").GetDouble() > 1,
            "the number is the driver's, above the range it was compared with");
        Assert.Equal("Good", reading.GetProperty("quality").GetString());
    }

    [RequiresDatabaseFact]
    public async Task A_tag_that_has_declared_nothing_says_nothing_about_a_range()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Unranged probe");

        JsonElement reading = default;
        await GatewayTestHost.WaitUntilAsync(
            async () => (reading = await CurrentAsync(client, device.TagId)).GetProperty("quality").GetString() == "Good",
            "the tag to read Good");

        // Null, not "inRange" — a deployment that declared no span must not be shown a verdict nobody
        // made (ADR-0030 §3).
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("rangeStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("rangeLow").ValueKind);
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("rangeHigh").ValueKind);
    }

    [RequiresDatabaseFact]
    public async Task The_range_is_readable_and_writable_through_the_tree_the_form_uses()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Ranged form probe");

        using var saved = await client.PutAsJsonAsync(
            $"/api/devices/{device.DeviceId}/tags/{device.TagId}",
            new SaveTagRequest("Ranged form probe", "Numeric", Unit: null, "holding:0", IsWritable: true, RangeLow: -5, RangeHigh: 250));
        saved.EnsureSuccessStatusCode();

        var tree = await client.GetFromJsonAsync<JsonElement>($"/api/sites/{Skopje}/tree");
        var stored = tree.GetProperty("devices").EnumerateArray()
            .SelectMany(d => d.GetProperty("tags").EnumerateArray())
            .Single(t => t.GetProperty("id").GetGuid() == device.TagId);

        // A range an author cannot read back is a range they cannot correct.
        Assert.Equal(-5, stored.GetProperty("rangeLow").GetDouble());
        Assert.Equal(250, stored.GetProperty("rangeHigh").GetDouble());
    }

    [RequiresDatabaseFact]
    public async Task A_range_that_is_not_a_range_is_refused_by_name()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Refusal probe");

        // Half a range, either half.
        await RefusedAsync(client, device, "Numeric", rangeLow: 10, rangeHigh: null, "only the low one");
        await RefusedAsync(client, device, "Numeric", rangeLow: null, rangeHigh: 10, "only the high one");

        // Ends the wrong way round — and equal ends are not a range either.
        await RefusedAsync(client, device, "Numeric", rangeLow: 100, rangeHigh: 0, "below its high end");
        await RefusedAsync(client, device, "Numeric", rangeLow: 5, rangeHigh: 5, "below its high end");

        // A span is a statement about a number, so a tag with no number to compare refuses one.
        await RefusedAsync(client, device, "Boolean", rangeLow: 0, rangeHigh: 1, "Only a numeric tag");
    }

    private static async Task RefusedAsync(
        HttpClient client,
        LiveDevice device,
        string valueKind,
        double? rangeLow,
        double? rangeHigh,
        string expected)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/devices/{device.DeviceId}/tags",
            new SaveTagRequest(
                $"Ranged {Guid.NewGuid():N}"[..16],
                valueKind,
                Unit: null,
                "holding:0",
                IsWritable: false,
                RangeLow: rangeLow,
                RangeHigh: rangeHigh));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(expected, error);
    }

    private static async Task<JsonElement> CurrentAsync(HttpClient client, Guid tagId)
    {
        using var response = await client.GetAsync($"/api/tags/{tagId}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }
}
