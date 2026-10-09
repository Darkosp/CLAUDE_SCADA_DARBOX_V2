using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Priority across the wire (ADR-0034): what an absent one means, and what a wrong one does.
/// </summary>
/// <remarks>
/// **The two cases that look alike and must not behave alike** are an omitted priority and a misspelled
/// one. Omitted is *not yet rationalised*, a real stage of ISA-18.2's lifecycle, and is accepted.
/// Misspelled is refused by name — because falling back to "not yet rationalised" would turn a typed
/// `Hgih` into an alarm that silently sorts last while its author believes they set it, which is the
/// shape of defect that only ever surfaces during a flood.
/// </remarks>
public sealed class AlarmPriorityApiTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public AlarmPriorityApiTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_threshold_saved_without_a_priority_reads_back_as_not_yet_rationalised()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Skopje, "Priority probe");
        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms",
            new { highLimit = 4.5 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var threshold = Assert.Single(
            (await client.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms")).EnumerateArray());

        // **Null travels as null.** A server that substituted "Low" here would be inventing an
        // assessment nobody made, and no client could tell the difference afterwards.
        Assert.Equal(JsonValueKind.Null, threshold.GetProperty("priority").ValueKind);
    }

    [RequiresDatabaseTheory]
    [InlineData("High")]
    [InlineData("Medium")]
    [InlineData("Low")]
    public async Task Each_of_the_three_survives_the_round_trip(string priority)
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Skopje, $"Priority {priority} probe");
        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms",
            new { highLimit = 4.5, priority });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var threshold = Assert.Single(
            (await client.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms")).EnumerateArray());

        Assert.Equal(priority, threshold.GetProperty("priority").GetString());
    }

    [RequiresDatabaseFact]
    public async Task A_priority_that_is_not_one_of_the_three_is_refused_by_name()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Skopje, "Bad priority probe");
        using var client = _host.CreateClient(admin);

        using var refused = await client.PostAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms",
            new { highLimit = 4.5, priority = "Hgih" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var message = await refused.Content.ReadAsStringAsync();

        // Names what was sent and what is allowed. A refusal that does not say what to type leaves the
        // reader guessing, and this is a field they will have typed by hand during rationalisation.
        Assert.Contains("Hgih", message, StringComparison.Ordinal);
        Assert.Contains("High", message, StringComparison.Ordinal);
        Assert.Contains("Low", message, StringComparison.Ordinal);

        // **And nothing was stored.** A refusal that half-saved would be worse than one that accepted.
        var thresholds = await client.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms");

        Assert.Empty(thresholds.EnumerateArray());
    }

    [RequiresDatabaseFact]
    public async Task A_priority_in_the_wrong_case_is_refused_rather_than_guessed()
    {
        // Deliberate, and the opposite of what ADR-0033 does for the OPC UA `security` setting — which
        // an operator types into a free-text box, where refusing `None` for its capital would be a
        // refusal about typography. This is a **fixed list the client renders as a select**, so the
        // only way to send `high` is to bypass that client, and quietly accepting it would mean the
        // stored value and the enumerated set disagree about what is legal.
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Skopje, "Case probe");
        using var client = _host.CreateClient(admin);

        using var refused = await client.PostAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms",
            new { highLimit = 4.5, priority = "high" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_priority_can_be_cleared_back_to_not_yet_rationalised()
    {
        // Rationalisation is a process, and a session legitimately undoes an earlier judgement. If an
        // absent field meant "leave it as it was", this would be impossible — which is why ADR-0034
        // has the client send the field rather than omit it.
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Skopje, "Clearing probe");
        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms",
            new { highLimit = 4.5, priority = "High" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var definitionId = (await client.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms"))
            .EnumerateArray().Single().GetProperty("id").GetGuid();

        using var updated = await client.PutAsJsonAsync(
            $"/api/tags/{probe.TagId}/alarms/{definitionId}",
            new { highLimit = 4.5, priority = (string?)null });

        Assert.True(
            updated.IsSuccessStatusCode,
            $"clearing a priority answered {(int)updated.StatusCode}");

        var threshold = Assert.Single(
            (await client.GetFromJsonAsync<JsonElement>($"/api/tags/{probe.TagId}/alarms")).EnumerateArray());

        Assert.Equal(JsonValueKind.Null, threshold.GetProperty("priority").ValueKind);
    }
}
