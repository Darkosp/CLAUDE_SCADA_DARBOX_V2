using System.Net;
using System.Net.Http.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Screens over the API (ADR-0024): a screen is configuration and the API is the only way one is
/// made, so this is where its refusals and its Site scoping are checked.
/// </summary>
/// <remarks>
/// The decisions worth testing here are the two that are not about storage. A screen saved with a
/// component nobody can draw is refused rather than stored and skipped, because an operator cannot
/// tell a screen that is silently missing something from one that is not. And a binding is checked
/// against what the caller may <i>see</i>, not only against what exists — a screen on one Site must
/// not be able to name a tag on another, and the composite keys in migration 0017 make that
/// unwritable, so what this checks is that it is refused with a sentence rather than a constraint
/// violation.
/// </remarks>
public sealed class ScreenApiTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public ScreenApiTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_new_Site_already_has_a_screen_with_something_on_it()
    {
        // ADR-0024 7, and the lesson Phase 6.5's walk recorded: the first screen a new user saw was
        // blank, and a blank screen is indistinguishable from a broken one.
        //
        // Asserted as "one of them is the seeded one" rather than "there is exactly one screen":
        // this host starts the app per test in parallel against one database, and the seeder's own
        // "is there a tenant yet" probe can be won by two of them at once. That race is real and is
        // recorded in open-work; what this test is about is that a Site comes up with a screen on
        // it, so it checks that and does not fail on the count.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var screens = await client.GetFromJsonAsync<List<ScreenDto>>($"/api/sites/{Bitola}/screens");

        Assert.NotEmpty(screens!);

        var seeded = screens!.First(screen => !string.IsNullOrWhiteSpace(screen.Name));
        Assert.NotEmpty(seeded.Components);

        // Nothing on a seeded screen is bound to a tag, because a Site with no devices has no tags --
        // a bound component would be unreadable from its first moment and would teach a new user
        // that unreadable tiles are normal.
        Assert.All(seeded.Components, component => Assert.Null(component.TagId));
    }

    [RequiresDatabaseFact]
    public async Task A_screen_is_saved_and_read_back_with_its_layout_unchanged()
    {
        var admin = await _host.LoginAsAdminAsync();
        var device = await _host.CreateLiveDeviceAsync(admin, Skopje, "Screen layout probe");
        using var client = _host.CreateClient(admin);

        var name = $"screen-{Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens",
            new SaveScreenRequest(
                name,
                3,
                [
                    new SaveScreenComponentRequest(null, 0, 8, 0, "value", null, device.TagId),
                    new SaveScreenComponentRequest(null, 0, 4, 1, "status", null, device.TagId),
                    new SaveScreenComponentRequest(null, 2, 12, 0, "label", "Wide heading", null),
                ]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var screenId = await created.Content.ReadFromJsonAsync<Guid>();

        var read = await client.GetFromJsonAsync<ScreenDto>($"/api/screens/{screenId}");

        Assert.Equal(name, read!.Name);
        Assert.Equal(3, read.Position);
        Assert.Equal(3, read.Components.Count);

        // The grid is the whole layout model, so the span and the row surviving is the layout
        // surviving (ADR-0024 2).
        var value = Assert.Single(read.Components, component => component.Kind == "value");
        Assert.Equal(0, value.RowIndex);
        Assert.Equal(8, value.ColumnSpan);
        var label = Assert.Single(read.Components, component => component.Kind == "label");
        Assert.Equal(12, label.ColumnSpan);
        Assert.Equal("Wide heading", label.Title);
    }

    [RequiresDatabaseFact]
    public async Task A_component_kind_this_build_does_not_draw_is_refused_by_name()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var name = $"screen-{Guid.NewGuid():N}";
        var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens",
            new SaveScreenRequest(
                name,
                0,
                [new SaveScreenComponentRequest(null, 0, 12, 0, "gauge", null, null)]));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("gauge", body, StringComparison.Ordinal);
        Assert.Contains("value", body, StringComparison.Ordinal);

        // And nothing was stored: a screen saved with something nobody can draw is a screen that is
        // silently missing something, which is what refusing the whole request prevents.
        var screens = await client.GetFromJsonAsync<List<ScreenDto>>($"/api/sites/{Skopje}/screens");
        Assert.DoesNotContain(screens!, screen => screen.Name == name);
    }

    [RequiresDatabaseFact]
    public async Task A_kind_that_reads_a_tag_is_refused_without_one()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens",
            new SaveScreenRequest(
                $"screen-{Guid.NewGuid():N}",
                0,
                [new SaveScreenComponentRequest(null, 0, 12, 0, "value", null, null)]));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("needs one", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task A_screen_on_a_Site_the_caller_cannot_see_is_not_found()
    {
        // ADR-0011: an id that exists on a Site the caller may not see is indistinguishable from one
        // that does not exist, and that is the answer rather than 403.
        var admin = await _host.LoginAsAdminAsync();
        var bitolaOperator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        using var client = _host.CreateClient(bitolaOperator.Token);

        using var asked = await client.GetAsync($"/api/sites/{Skopje}/screens");

        Assert.Equal(HttpStatusCode.NotFound, asked.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Editing_a_screen_needs_the_Operator_role()
    {
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));
        using var client = _host.CreateClient(viewer.Token);

        using var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens",
            new SaveScreenRequest($"screen-{Guid.NewGuid():N}", 0, []));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_duplicate_screen_name_on_one_Site_is_a_conflict_and_on_another_it_is_not()
    {
        // ADR-0015 and ADR-0024 6: a screen's name is unique within its Site, not across the
        // deployment.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var name = $"screen-{Guid.NewGuid():N}";
        using var first = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens", new SaveScreenRequest(name, 0, []));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens", new SaveScreenRequest(name, 1, []));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(name, await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The same name on another Site is a different name.
        using var elsewhere = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/screens", new SaveScreenRequest(name, 0, []));
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_binding_to_a_tag_on_another_Site_is_refused_by_the_API()
    {
        // The composite keys in migration 0017 would refuse this row anyway; what is checked here is
        // that it is refused with a sentence rather than as a constraint violation, because the
        // person who typed it needs to know which of the two things they did wrong.
        var admin = await _host.LoginAsAdminAsync();
        var bitolaOperator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        var skopjeDevice = await _host.CreateLiveDeviceAsync(admin, Skopje, "Cross-site binding probe");

        using var client = _host.CreateClient(bitolaOperator.Token);

        using var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/screens",
            new SaveScreenRequest(
                $"screen-{Guid.NewGuid():N}",
                0,
                [new SaveScreenComponentRequest(null, 0, 12, 0, "value", null, skopjeDevice.TagId)]));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(
            "cannot see",
            await refused.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task A_binding_whose_tag_has_gone_is_readable_false_and_the_row_is_still_there()
    {
        // ADR-0024 5, decided by the server so that every renderer gives the same answer. Hiding the
        // component would make a screen look complete while showing less than it was built to show,
        // and an operator cannot know a tile is missing from a screen they did not author.
        //
        // The way a *stored* binding becomes one a reader cannot see, without the cross-Site keys
        // refusing it at save time, is the tag going away. That is also the case that happens on a
        // real deployment, and the one worth pinning.
        var admin = await _host.LoginAsAdminAsync();
        var device = await _host.CreateLiveDeviceAsync(admin, Bitola, "Readability probe");
        using var client = _host.CreateClient(admin);

        var created = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/screens",
            new SaveScreenRequest(
                $"screen-{Guid.NewGuid():N}",
                0,
                [
                    new SaveScreenComponentRequest(null, 0, 12, 0, "label", "Heading", null),
                    new SaveScreenComponentRequest(null, 1, 12, 0, "value", null, device.TagId),
                ]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var screenId = await created.Content.ReadFromJsonAsync<Guid>();

        // Readable while the tag is there, which is the control: without it, the assertion below
        // would pass just as well on a server that never answered true.
        var before = await client.GetFromJsonAsync<ScreenDto>($"/api/screens/{screenId}");
        Assert.True(Assert.Single(before!.Components, c => c.Kind == "value").Readable);

        using var removed = await client.DeleteAsync($"/api/devices/{device.DeviceId}/tags/{device.TagId}");
        Assert.True(removed.IsSuccessStatusCode, $"the tag could not be removed: {removed.StatusCode}");

        var after = await client.GetFromJsonAsync<ScreenDto>($"/api/screens/{screenId}");

        // Both components are still there, and the one whose tag went says it cannot be read rather
        // than disappearing or rendering as zero.
        Assert.Equal(2, after!.Components.Count);
        Assert.False(Assert.Single(after.Components, c => c.Kind == "value").Readable);
        Assert.True(Assert.Single(after.Components, c => c.Kind == "label").Readable);
    }

    [RequiresDatabaseFact]
    public async Task Replacing_a_screen_drops_the_component_that_is_missing_from_the_request()
    {
        // Replace rather than merge: a client that failed to send a component must not be
        // indistinguishable from an author who deleted it.
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var created = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens",
            new SaveScreenRequest(
                $"screen-{Guid.NewGuid():N}",
                0,
                [
                    new SaveScreenComponentRequest(null, 0, 6, 0, "label", "First", null),
                    new SaveScreenComponentRequest(null, 0, 6, 1, "label", "Second", null),
                ]));
        var screenId = await created.Content.ReadFromJsonAsync<Guid>();

        var read = await client.GetFromJsonAsync<ScreenDto>($"/api/screens/{screenId}");
        var kept = Assert.Single(read!.Components, component => component.Title == "First");

        using var updated = await client.PutAsJsonAsync(
            $"/api/screens/{screenId}",
            new SaveScreenRequest(
                read.Name,
                0,
                [new SaveScreenComponentRequest(kept.Id, 0, 12, 0, "label", "First", null)]));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        var after = await client.GetFromJsonAsync<ScreenDto>($"/api/screens/{screenId}");

        // One component, the one that was sent, and it kept its id -- so an audit entry about
        // editing it still points at the same thing.
        var only = Assert.Single(after!.Components);
        Assert.Equal(kept.Id, only.Id);
        Assert.Equal("First", only.Title);
        Assert.Equal(12, only.ColumnSpan);
    }

    [RequiresDatabaseFact]
    public async Task A_deleted_screen_is_gone_from_the_list_and_from_its_own_path()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        var name = $"screen-{Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/screens", new SaveScreenRequest(name, 0, []));
        var screenId = await created.Content.ReadFromJsonAsync<Guid>();

        using var deleted = await client.DeleteAsync($"/api/screens/{screenId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var screens = await client.GetFromJsonAsync<List<ScreenDto>>($"/api/sites/{Skopje}/screens");
        Assert.DoesNotContain(screens!, screen => screen.Name == name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/screens/{screenId}")).StatusCode);
    }
}
