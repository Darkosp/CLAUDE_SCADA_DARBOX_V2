using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Screen storage (ADR-0024): the rows a screen is, and the composite keys that make a cross-Site
/// binding unrepresentable rather than merely refused.
/// </summary>
public sealed class ScreenStorageTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public ScreenStorageTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_screen_round_trips_with_its_components_and_their_layout()
    {
        var (siteId, tenantId, deviceId, tagId) = await SeedAsync("round trip");

        var dataSource = _database.ApplicationDataSource;
        var screens = new ScreenRepository(dataSource);

        var screen = Screen(siteId, tenantId, "Overview", deviceId, tagId);
        await screens.AddAsync(screen, CancellationToken.None);

        var read = await screens.FindAsync(screen.Id, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("Overview", read.Name);
        Assert.Equal(3, read.Position);
        Assert.Equal(2, read.Components.Count);

        var label = Assert.Single(read.Components, component => component.Kind == ScreenComponentKinds.Label);
        Assert.Equal("Heading", label.Title);
        Assert.Null(label.TagId);
        Assert.Null(label.DeviceId);

        var value = Assert.Single(read.Components, component => component.Kind == ScreenComponentKinds.Value);
        Assert.Equal(tagId, value.TagId);
        Assert.Equal(deviceId, value.DeviceId);
        Assert.Equal(8, value.ColumnSpan);
        Assert.Equal(1, value.Position);
    }

    [RequiresDatabaseFact]
    public async Task Updating_a_screen_replaces_its_components_and_keeps_the_one_that_was_resent()
    {
        var (siteId, tenantId, deviceId, tagId) = await SeedAsync("update");

        var dataSource = _database.ApplicationDataSource;
        var screens = new ScreenRepository(dataSource);

        var screen = Screen(siteId, tenantId, "Overview", deviceId, tagId);
        await screens.AddAsync(screen, CancellationToken.None);

        var kept = screen.Components[0];

        await screens.UpdateAsync(
            new Screen
            {
                Id = screen.Id,
                TenantId = tenantId,
                SiteId = siteId,
                Name = "Renamed",
                Position = 7,
                Components =
                [
                    new ScreenComponent
                    {
                        Id = kept.Id,
                        ScreenId = screen.Id,
                        RowIndex = 5,
                        ColumnSpan = 12,
                        Position = 0,
                        Kind = ScreenComponentKinds.Label,
                        Title = "Kept",
                        DeviceId = null,
                        TagId = null,
                    },
                ],
            },
            CancellationToken.None);

        var read = await screens.FindAsync(screen.Id, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("Renamed", read.Name);
        Assert.Equal(7, read.Position);

        // One component, the one that was sent, and it kept its id -- so an audit entry about
        // editing it still points at the same thing.
        var only = Assert.Single(read.Components);
        Assert.Equal(kept.Id, only.Id);
        Assert.Equal("Kept", only.Title);
        Assert.Equal(5, only.RowIndex);
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_screen_takes_its_components_with_it()
    {
        var (siteId, tenantId, deviceId, tagId) = await SeedAsync("delete");

        var dataSource = _database.ApplicationDataSource;
        var screens = new ScreenRepository(dataSource);

        var screen = Screen(siteId, tenantId, "Overview", deviceId, tagId);
        await screens.AddAsync(screen, CancellationToken.None);

        await screens.DeleteAsync(screen.Id, CancellationToken.None);

        Assert.Null(await screens.FindAsync(screen.Id, CancellationToken.None));
        Assert.Empty(await screens.GetBySiteAsync(siteId, CancellationToken.None));

        // The rows survive as soft-deleted, which is what lets an audit entry still resolve a name
        // (ADR-0009) -- and is what the _active views are for.
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var live = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
            connection,
            "SELECT count(*) FROM screen_component_active WHERE screen_id = @screenId",
            new { screenId = screen.Id });
        Assert.Equal(0, live);
    }

    [RequiresDatabaseFact]
    public async Task A_component_naming_a_tag_on_another_Site_is_refused_by_the_database()
    {
        // The key, not the API: a path that writes these tables without going through the endpoint
        // must not be able to make a screen read across a Site boundary (ADR-0011). This is what the
        // two composite keys in migration 0017 are for.
        var (siteId, tenantId, _, _) = await SeedAsync("cross-a");
        var (_, _, otherDeviceId, otherTagId) = await SeedAsync("cross-b");

        var dataSource = _database.ApplicationDataSource;
        var screens = new ScreenRepository(dataSource);

        var screen = new Screen
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SiteId = siteId,
            Name = "Cross-site probe",
            Components =
            [
                new ScreenComponent
                {
                    Id = Guid.NewGuid(),
                    ScreenId = Guid.Empty,
                    RowIndex = 0,
                    ColumnSpan = 12,
                    Position = 0,
                    Kind = ScreenComponentKinds.Value,
                    // A device and a tag that really exist, on the wrong Site.
                    DeviceId = otherDeviceId,
                    TagId = otherTagId,
                },
            ],
        };

        screen.Components = screen.Components
            .Select(component => new ScreenComponent
            {
                Id = component.Id,
                ScreenId = screen.Id,
                RowIndex = component.RowIndex,
                ColumnSpan = component.ColumnSpan,
                Position = component.Position,
                Kind = component.Kind,
                Title = component.Title,
                TagId = component.TagId,
                DeviceId = component.DeviceId,
            })
            .ToList();

        await Assert.ThrowsAnyAsync<Exception>(() => screens.AddAsync(screen, CancellationToken.None));
    }

    [RequiresDatabaseFact]
    public async Task Seeding_a_Site_gives_it_one_screen_and_a_second_call_changes_nothing()
    {
        var (siteId, tenantId, _, _) = await SeedAsync("seed");

        var dataSource = _database.ApplicationDataSource;
        var screens = new ScreenRepository(dataSource);

        await screens.SeedForSiteAsync(tenantId, siteId, "Skopje", CancellationToken.None);
        var first = await screens.GetBySiteAsync(siteId, CancellationToken.None);

        var seeded = Assert.Single(first);
        Assert.NotEmpty(seeded.Components);
        Assert.All(seeded.Components, component => Assert.Null(component.TagId));

        // The seeded screen has to be one the API would also accept. That is what a walk found on
        // 2026-10-05: the seeder writes rows directly, so nothing had ever asked it for the text an
        // `alarms` component was then required to carry, and every Site's screen was born unsaveable.
        // An author opened it, changed anything, and the API answered "A 'alarms' component shows
        // text and needs some".
        Assert.Null(ScreenRules.ProblemWith(seeded));

        // And the part the check above CANNOT catch, which is the part worth testing: every text field
        // is actually filled in. `ScreenRules` now accepts an `alarms` component with no heading
        // (ADR-0024's kinds table gives text as what a `label` shows and says nothing of the sort for
        // `alarms`), so a seed that dropped the heading again would pass the validation and fail the
        // reader -- which is precisely the mutation this assertion was added to catch.
        Assert.All(seeded.Components, component => Assert.False(
            string.IsNullOrWhiteSpace(component.Title),
            $"the seeded '{component.Kind}' component carries no text"));

        // Idempotent by asking rather than by a fixed id: an operator who deleted the seeded screen
        // and built their own must not have it reappear.
        await screens.SeedForSiteAsync(tenantId, siteId, "Skopje", CancellationToken.None);

        Assert.Single(await screens.GetBySiteAsync(siteId, CancellationToken.None));
    }

    private static Screen Screen(Guid siteId, Guid tenantId, string name, Guid deviceId, Guid tagId)
    {
        var screenId = Guid.NewGuid();

        return new Screen
        {
            Id = screenId,
            TenantId = tenantId,
            SiteId = siteId,
            Name = name,
            Position = 3,
            Components =
            [
                new ScreenComponent
                {
                    Id = Guid.NewGuid(),
                    ScreenId = screenId,
                    RowIndex = 0,
                    ColumnSpan = 4,
                    Position = 0,
                    Kind = ScreenComponentKinds.Label,
                    Title = "Heading",
                },
                new ScreenComponent
                {
                    Id = Guid.NewGuid(),
                    ScreenId = screenId,
                    RowIndex = 0,
                    ColumnSpan = 8,
                    Position = 1,
                    Kind = ScreenComponentKinds.Value,
                    TagId = tagId,
                    DeviceId = deviceId,
                },
            ],
        };
    }

    /// <summary>A Site with one device and one tag on it, so a component has something real to name.</summary>
    private async Task<(Guid SiteId, Guid TenantId, Guid DeviceId, Guid TagId)> SeedAsync(string label)
    {
        var tenantId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var suffix = $"{label}-{Guid.NewGuid():N}";

        await using var connection = await _database.DataSource.OpenConnectionAsync(CancellationToken.None);

        // The privileged connection: this is fixture data rather than something a test is asserting
        // about the application role, and the application role cannot create Sites.
        await Dapper.SqlMapper.ExecuteAsync(
            connection,
            """
            INSERT INTO tenant (id, name) VALUES (@tenantId, @tenantName);
            INSERT INTO site (id, tenant_id, name, time_zone_id) VALUES (@siteId, @tenantId, @siteName, 'UTC');
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms)
            VALUES (@deviceId, @siteId, @deviceName, 'modbus-tcp', '{}'::jsonb, 1000);
            INSERT INTO tag (id, device_id, name, value_kind, source_address, is_writable)
            VALUES (@tagId, @deviceId, @tagName, 0, 'holding:0', false);
            """,
            new
            {
                tenantId,
                tenantName = $"Tenant {suffix}",
                siteId,
                siteName = $"Site {suffix}",
                deviceId,
                deviceName = $"Device {suffix}",
                tagId,
                tagName = $"Tag {suffix}",
            });

        return (siteId, tenantId, deviceId, tagId);
    }
}
