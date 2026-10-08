using System.Net;
using System.Net.Http.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// A unit that cannot convert is refused at the API, as a refusal and not as a crash (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UnitRefusalTests"/> in Core covers the rule. This covers **the path the defect actually
/// travelled**: a request arrived, a <see cref="UnitDto"/> bound, <c>ToDomain</c> built a unit, and
/// nothing between the wire and the database asked whether it could convert.
/// </para>
/// <para>
/// **The last test is the one that explains how it happened.** `tools/seed-demo-screen.mjs` sent the
/// factor under the name <c>siFactor</c>, which the DTO does not have. It bound to nothing, defaulted
/// to zero, and three tags were stored carrying a unit whose conversion was "multiply by zero".
/// Nothing in the request was *wrong* in a way anything checked — the field simply was not there.
/// </para>
/// </remarks>
public sealed class UnitRefusalApiTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public UnitRefusalApiTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_unit_whose_factor_is_zero_is_refused_rather_than_stored()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Zero factor probe");

        using var response = await client.PutAsJsonAsync(
            $"/api/devices/{device.DeviceId}/tags/{device.TagId}",
            new SaveTagRequest(
                "Zero factor probe",
                "Numeric",
                new UnitDto("%", "Dimensionless", FactorToSi: 0, OffsetToSi: 0),
                "holding:0",
                IsWritable: false));

        // A refusal, not a 500: the message has to reach whoever sent it, because they are the only
        // one who can fix it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ADR-0005", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task A_unit_that_converts_is_still_accepted()
    {
        // The control. A refusal that rejected every unit would satisfy the test above and make the
        // product unusable -- and this project has been caught by a test that could not fail before.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Good unit probe");

        using var response = await client.PutAsJsonAsync(
            $"/api/devices/{device.DeviceId}/tags/{device.TagId}",
            new SaveTagRequest(
                "Good unit probe",
                "Numeric",
                new UnitDto("bar", "Pressure", FactorToSi: 100_000, OffsetToSi: 0),
                "holding:0",
                IsWritable: false));

        response.EnsureSuccessStatusCode();
    }

    [RequiresDatabaseFact]
    public async Task A_unit_sent_without_its_factor_at_all_is_refused_too()
    {
        // **How the defect actually happened.** The seeder sent `siFactor`, which the DTO does not
        // have; it bound to nothing and defaulted to zero. The request was not malformed and no field
        // held a wrong value -- the field was absent, and absence had the same effect as writing zero.
        //
        // Sent as raw JSON rather than through the DTO, because the DTO cannot express the mistake:
        // that is the point of it.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Missing factor probe");

        using var content = new StringContent(
            """
            {
              "name": "Missing factor probe",
              "valueKind": "Numeric",
              "sourceAddress": "holding:0",
              "isWritable": false,
              "unit": { "symbol": "%", "dimension": "Dimensionless", "siFactor": 1 }
            }
            """,
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await client.PutAsync(
            $"/api/devices/{device.DeviceId}/tags/{device.TagId}",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
