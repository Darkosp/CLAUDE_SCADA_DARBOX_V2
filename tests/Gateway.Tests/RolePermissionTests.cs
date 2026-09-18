using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>ADR-0011's role criteria: what Viewer, Operator and Admin may each do.</summary>
public sealed class RolePermissionTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public RolePermissionTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_viewer_cannot_write_a_tag_or_acknowledge_or_shelve_an_alarm()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Viewer probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var viewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));

        using var asViewer = _host.CreateClient(viewer.Token);

        // The viewer can see all of it, so the refusals below are the role at work — a Site
        // they could not see would answer 404 instead.
        await AssertStatusAsync(HttpStatusCode.OK, asViewer.GetAsync($"/api/tags/{probe.TagId}/alarms"));

        await AssertStatusAsync(HttpStatusCode.Forbidden, asViewer.PostAsJsonAsync($"/api/tags/{probe.TagId}/value", new { value = 42 }));
        await AssertStatusAsync(HttpStatusCode.Forbidden, asViewer.PostAsync($"/api/alarms/{definitionId}/acknowledge", content: null));
        await AssertStatusAsync(HttpStatusCode.Forbidden, asViewer.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { durationMinutes = 60 }));

        // Refused, not merely answered differently: nothing reached the device, the alarm is
        // untouched, and nothing was recorded as done.
        Assert.DoesNotContain(_host.Drivers.Writes, write => write.Tag.TagId == probe.TagId);
        Assert.Equal("Active", await _host.AlarmStateAsync(admin, definitionId));
        Assert.Empty(await _host.AuditActorsAsync("alarm.acknowledge", definitionId));
        Assert.Empty(await _host.AuditActorsAsync("alarm.shelve", definitionId));
    }

    [RequiresDatabaseFact]
    public async Task An_operator_can_write_acknowledge_and_shelve_within_a_permitted_site()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Operator action probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));

        using var asOperator = _host.CreateClient(@operator.Token);

        await AssertStatusAsync(HttpStatusCode.NoContent, asOperator.PostAsJsonAsync($"/api/tags/{probe.TagId}/value", new { value = 42 }));
        var write = Assert.Single(_host.Drivers.Writes, recorded => recorded.Tag.TagId == probe.TagId);
        Assert.Equal(probe.DeviceId, write.DeviceId);
        Assert.Equal(42, Assert.IsType<TagValue.Numeric>(write.Value).Value);

        await AssertStatusAsync(HttpStatusCode.NoContent, asOperator.PostAsync($"/api/alarms/{definitionId}/acknowledge", content: null));
        Assert.Equal("Acknowledged", await _host.AlarmStateAsync(admin, definitionId));

        // Exactly one entry, naming the operator — the "who acknowledged" gap Phase 3 left open.
        Assert.Equal(@operator.Id, Assert.Single(await _host.AuditActorsAsync("alarm.acknowledge", definitionId)));

        await AssertStatusAsync(HttpStatusCode.NoContent, asOperator.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { durationMinutes = 60 }));
        Assert.Equal("Shelved", await _host.AlarmStateAsync(admin, definitionId));
        Assert.Equal(@operator.Id, Assert.Single(await _host.AuditActorsAsync("alarm.shelve", definitionId)));
    }

    [RequiresDatabaseFact]
    public async Task An_operator_cannot_change_configuration_even_on_their_own_site()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Configuration probe");
        var definitionId = await _host.AddThresholdAsync(admin, probe.TagId, highLimit: 1_000_000_000);
        var templateId = await _host.CreateTemplateAsync(admin, "Configuration probe template");
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));

        using var asOperator = _host.CreateClient(@operator.Token);

        var device = new SaveDeviceRequest("Renamed by operator", FakeDriverFactory.Key, new Dictionary<string, string>(), 500, FolderId: null);
        var tag = new SaveTagRequest("Renamed by operator", "Numeric", Unit: null, "holding:1", IsWritable: false);

        var attempts = new (string Label, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("create folder", () => asOperator.PostAsJsonAsync($"/api/sites/{Bitola}/folders", new { name = "Not allowed", parentFolderId = (Guid?)null })),
            ("create device", () => asOperator.PostAsJsonAsync($"/api/sites/{Bitola}/devices", device)),
            ("edit device", () => asOperator.PutAsJsonAsync($"/api/sites/{Bitola}/devices/{probe.DeviceId}", device)),
            ("delete device", () => asOperator.DeleteAsync($"/api/sites/{Bitola}/devices/{probe.DeviceId}")),
            ("create tag", () => asOperator.PostAsJsonAsync($"/api/devices/{probe.DeviceId}/tags", tag)),
            ("edit tag", () => asOperator.PutAsJsonAsync($"/api/devices/{probe.DeviceId}/tags/{probe.TagId}", tag)),
            ("delete tag", () => asOperator.DeleteAsync($"/api/devices/{probe.DeviceId}/tags/{probe.TagId}")),
            ("create threshold", () => asOperator.PostAsJsonAsync($"/api/tags/{probe.TagId}/alarms", new { highLimit = 5.0 })),
            ("move threshold", () => asOperator.PutAsJsonAsync($"/api/tags/{probe.TagId}/alarms/{definitionId}", new { highLimit = 5.0 })),
            ("delete threshold", () => asOperator.DeleteAsync($"/api/tags/{probe.TagId}/alarms/{definitionId}")),
            ("create template", () => asOperator.PostAsJsonAsync("/api/templates", new { name = "Not allowed" })),
            ("add template tag", () => asOperator.PostAsJsonAsync(
                $"/api/templates/{templateId}/tags",
                new { name = "Not allowed", valueKind = "Numeric", unit = (object?)null, addressTemplate = "holding:{offset}", isWritable = false })),
            ("remove template tag", () => asOperator.DeleteAsync($"/api/templates/{templateId}/tags/{Guid.NewGuid()}")),
            ("instantiate template", () => asOperator.PostAsJsonAsync(
                $"/api/sites/{Bitola}/devices/from-template",
                new
                {
                    templateId,
                    name = "Not allowed",
                    driverKey = FakeDriverFactory.Key,
                    connectionSettings = new Dictionary<string, string>(),
                    scanIntervalMs = 500,
                    folderId = (Guid?)null,
                    parameters = new Dictionary<string, string> { ["offset"] = "1" },
                })),
            ("create a user", () => asOperator.PostAsJsonAsync("/api/users", new { username = "sneaky", password = GatewayTestHost.UserPassword, isAdmin = true })),
            ("make themselves Admin", () => asOperator.PutAsJsonAsync($"/api/users/{@operator.Id}/admin", new { isAdmin = true })),
        };

        foreach (var (label, send) in attempts)
        {
            using var response = await send();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{label} answered {(int)response.StatusCode}, expected 403.");
        }

        // And nothing moved.
        using var asAdmin = _host.CreateClient(admin);

        var deviceNow = await asAdmin.GetFromJsonAsync<JsonElement>($"/api/devices/{probe.DeviceId}");
        Assert.Equal("Configuration probe device", deviceNow.GetProperty("name").GetString());
        Assert.Contains(deviceNow.GetProperty("tags").EnumerateArray(), existing => existing.GetProperty("id").GetGuid() == probe.TagId);

        var threshold = Assert.Single((await asAdmin.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms")).EnumerateArray());
        Assert.Equal(1_000_000_000, threshold.GetProperty("highLimit").GetDouble());

        var users = await asAdmin.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.DoesNotContain(users.EnumerateArray(), user => user.GetProperty("username").GetString() == "sneaky");
        Assert.False(users.EnumerateArray().Single(user => user.GetProperty("userId").GetGuid() == @operator.Id).GetProperty("isAdmin").GetBoolean());
    }

    [RequiresDatabaseFact]
    public async Task Viewers_and_operators_are_refused_both_template_reads()
    {
        var admin = await _host.LoginAsAdminAsync();
        var templateId = await _host.CreateTemplateAsync(admin, "Read probe template");
        var viewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));

        foreach (var token in new[] { viewer.Token, @operator.Token })
        {
            using var client = _host.CreateClient(token);
            await AssertStatusAsync(HttpStatusCode.Forbidden, client.GetAsync("/api/templates"));
            await AssertStatusAsync(HttpStatusCode.Forbidden, client.GetAsync($"/api/templates/{templateId}/tags"));
        }

        using var asAdmin = _host.CreateClient(admin);
        await AssertStatusAsync(HttpStatusCode.OK, asAdmin.GetAsync("/api/templates"));
        await AssertStatusAsync(HttpStatusCode.OK, asAdmin.GetAsync($"/api/templates/{templateId}/tags"));
    }

    [RequiresDatabaseFact]
    public async Task A_write_reaches_the_driver_that_owns_the_tags_device()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Other driver probe", FakeDriverFactory.OtherKey);
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));

        using var asOperator = _host.CreateClient(@operator.Token);
        await AssertStatusAsync(HttpStatusCode.NoContent, asOperator.PostAsJsonAsync($"/api/tags/{probe.TagId}/value", new { value = 7 }));

        var write = Assert.Single(_host.OtherDrivers.Writes, recorded => recorded.Tag.TagId == probe.TagId);
        Assert.Equal(probe.DeviceId, write.DeviceId);
        Assert.Equal("holding:0", write.Tag.SourceAddress);
        Assert.Equal(7, Assert.IsType<TagValue.Numeric>(write.Value).Value);

        // Not also handed to the other protocol's driver.
        Assert.DoesNotContain(_host.Drivers.Writes, recorded => recorded.Tag.TagId == probe.TagId);
    }

    [RequiresDatabaseFact]
    public async Task A_password_under_twelve_characters_is_refused_and_twelve_plain_letters_are_accepted()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var asAdmin = _host.CreateClient(admin);

        var tooShort = $"short-{Guid.NewGuid():N}"[..16];
        await AssertStatusAsync(
            HttpStatusCode.BadRequest,
            asAdmin.PostAsJsonAsync("/api/users", new { username = tooShort, password = "abcdefghijk", isAdmin = false }));

        // Length only: no capitals, digits or symbols required (ADR-0011).
        var plain = $"plain-{Guid.NewGuid():N}"[..16];
        await AssertStatusAsync(
            HttpStatusCode.Created,
            asAdmin.PostAsJsonAsync("/api/users", new { username = plain, password = "abcdefghijkl", isAdmin = false }));

        Assert.False(string.IsNullOrEmpty(await _host.LoginAsync(plain, "abcdefghijkl")));

        using var anonymous = _host.CreateClient();
        await AssertStatusAsync(
            HttpStatusCode.Unauthorized,
            anonymous.PostAsJsonAsync("/api/auth/login", new { username = tooShort, password = "abcdefghijk" }));
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.True(
            response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered " +
            $"{(int)response.StatusCode}, expected {(int)expected}.");
    }
}
