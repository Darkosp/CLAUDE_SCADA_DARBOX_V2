using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0029 over REST: a trend asks for the points it can draw, the server reduces the window in the
/// query and says which width it used, and a request that asks for nothing gets every reading.
/// </summary>
/// <remarks>
/// The persistence tests prove the reduction itself — the extremes, the absent empty bucket, the
/// grid. This proves the contract around it: which of the two answers comes back, that the width is
/// stated, that a reduction is what the caller asked for rather than what the server decided, and
/// that asking for an impossible number of points is refused by name rather than quietly rounded.
/// </remarks>
public sealed class TrendReductionTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private static readonly DateTimeOffset At = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    /// <summary>600 readings one second apart: ten minutes at a scan a second.</summary>
    private const int Readings = 600;

    private readonly GatewayTestHost _host;

    public TrendReductionTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_trend_that_asks_for_points_gets_buckets_and_the_window_read_gets_readings()
    {
        var admin = await _host.LoginAsAdminAsync();
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Trend probe");
        await SeedAsync(device.TagId);

        using var client = _host.CreateClient(admin);
        var from = Uri.EscapeDataString(At.ToString("O"));
        var to = Uri.EscapeDataString(At.AddMinutes(10).ToString("O"));

        var reduced = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tags/{device.TagId}/history?from={from}&to={to}&points=60");

        // One or the other, never a mixture (ADR-0029 §3): a payload holding both would be one whose
        // completeness a reader has to work out.
        Assert.Equal(0, reduced.GetProperty("samples").GetArrayLength());
        Assert.Equal(10000, reduced.GetProperty("bucketMilliseconds").GetInt64());

        var buckets = reduced.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(60, buckets.Count);
        Assert.Equal(Readings, buckets.Sum(bucket => bucket.GetProperty("count").GetInt64()));

        // The stretch the source answered with nothing plottable is a bucket of its own, with its
        // count and no envelope — a hole, rather than a bucket that quietly disappears.
        var unplottable = Assert.Single(buckets, bucket => bucket.GetProperty("low").ValueKind == JsonValueKind.Null);
        Assert.Equal(At.AddMinutes(5), unplottable.GetProperty("startUtc").GetDateTimeOffset());
        Assert.Equal(10, unplottable.GetProperty("count").GetInt64());

        // And every bucket carries the extremes of what was measured in it — never an average, and
        // never a value a reading that a trend does not plot happened to carry.
        var plottable = buckets.Where(bucket => bucket.GetProperty("low").ValueKind != JsonValueKind.Null).ToList();
        Assert.Equal(3.4, plottable.Min(bucket => bucket.GetProperty("low").GetDouble()), 6);
        Assert.Equal(4.39, plottable.Max(bucket => bucket.GetProperty("high").GetDouble()), 6);

        // The same window without `points` is the readings, and no buckets at all: reduction is
        // requested, never applied silently.
        var raw = await client.GetFromJsonAsync<JsonElement>($"/api/tags/{device.TagId}/history?from={from}&to={to}");

        Assert.Equal(Readings, raw.GetProperty("samples").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("bucketMilliseconds").ValueKind);
        Assert.Equal(0, raw.GetProperty("buckets").GetArrayLength());
    }

    [RequiresDatabaseFact]
    public async Task A_number_of_points_that_cannot_be_drawn_is_refused_by_name()
    {
        // Refused rather than clamped, for the reason §2.0f refused an out-of-range response timeout:
        // a caller whose request was quietly coarsened gets a picture it did not ask for, and nothing
        // anywhere says so.
        var admin = await _host.LoginAsAdminAsync();
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Trend points probe");

        using var client = _host.CreateClient(admin);

        foreach (var asked in new[] { 1, 0, -5, GatewayApp.MaximumTrendPoints + 1 })
        {
            using var refused = await client.GetAsync($"/api/tags/{device.TagId}/history?points={asked}");

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
            Assert.Contains($"between {GatewayApp.MinimumTrendPoints} and {GatewayApp.MaximumTrendPoints}", error);
            Assert.Contains($"points; {asked} was asked for", error);
        }
    }

    /// <summary>
    /// The window the tests read: one reading a second for ten minutes, with the ten seconds from
    /// minute five measured but never plottable — the shape a device answering without a value makes,
    /// and one whole bucket of the ten a point covers here.
    /// </summary>
    /// <remarks>
    /// Written through the historian rather than waited for from a device, so the buckets are
    /// arithmetic instead of timing. The device's own live readings land at the present moment and
    /// nowhere near this window, which starts on a fixed date in the past.
    /// </remarks>
    private async Task SeedAsync(Guid tagId)
    {
        var historian = _host.Services.GetRequiredService<IHistorian>();

        await historian.WriteAsync(
            Enumerable.Range(0, Readings)
                .Select(second => new HistorianSample(
                    tagId,
                    new TagValue.Numeric(3.4 + (second % 100) / 100.0),
                    At.AddSeconds(second),
                    At.AddSeconds(second),
                    second is >= 300 and < 310 ? Quality.Bad : Quality.Good))
                .ToList(),
            CancellationToken.None);
    }
}
