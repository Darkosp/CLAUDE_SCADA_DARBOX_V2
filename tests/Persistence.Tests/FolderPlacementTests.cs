using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Proves that cross-site placement is refused by the database itself, not by
/// repository code. The distinction is the point: a check in application code is
/// bypassable by any future write path, and this one guards a security boundary.
/// </summary>
public sealed class FolderPlacementTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public FolderPlacementTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_folder_cannot_be_nested_under_a_parent_from_another_site()
    {
        var world = await TwoSitesAsync();
        var folders = new FolderRepository(_database.DataSource);

        var trespasser = new Folder
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            ParentFolderId = world.FolderInSiteTwo,
            Name = "Nested across sites",
        };

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => folders.AddAsync(trespasser, CancellationToken.None));

        Assert.Equal("23503", exception.SqlState); // foreign_key_violation
        Assert.Equal("fk_folder_parent_same_site", exception.ConstraintName);
    }

    [RequiresDatabaseFact]
    public async Task A_device_cannot_be_placed_in_a_folder_from_another_site()
    {
        var world = await TwoSitesAsync();
        var devices = new DeviceRepository(_database.DataSource);

        var trespasser = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            FolderId = world.FolderInSiteTwo,
            Name = "Device across sites",
            DriverKey = "modbus-tcp",
        };

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => devices.AddAsync(trespasser, CancellationToken.None));

        Assert.Equal("23503", exception.SqlState);
        Assert.Equal("fk_device_folder_same_site", exception.ConstraintName);
    }

    [RequiresDatabaseFact]
    public async Task Placement_within_one_site_is_allowed_at_every_level()
    {
        var world = await TwoSitesAsync();
        var folders = new FolderRepository(_database.DataSource);
        var devices = new DeviceRepository(_database.DataSource);

        var child = new Folder
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            ParentFolderId = world.FolderInSiteOne,
            Name = "Line 2",
        };
        await folders.AddAsync(child, CancellationToken.None);

        var device = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            FolderId = child.Id,
            Name = "Pump 1",
            DriverKey = "modbus-tcp",
        };
        await devices.AddAsync(device, CancellationToken.None);

        var stored = await devices.FindAsync(device.Id, CancellationToken.None);
        Assert.Equal(child.Id, stored!.FolderId);
    }

    [RequiresDatabaseFact]
    public async Task A_root_folder_and_a_device_with_no_folder_remain_legal()
    {
        // The composite keys must not make the null cases impossible: a root folder and
        // a device sitting directly under its site are both normal, and every device
        // predating the folder migration is the latter.
        var world = await TwoSitesAsync();
        var devices = new DeviceRepository(_database.DataSource);

        var device = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            FolderId = null,
            Name = "Unfiled device",
            DriverKey = "modbus-tcp",
        };

        await devices.AddAsync(device, CancellationToken.None);

        var stored = await devices.FindAsync(device.Id, CancellationToken.None);
        Assert.Null(stored!.FolderId);
    }

    [RequiresDatabaseFact]
    public async Task A_folder_cannot_be_moved_under_its_own_descendant()
    {
        // Not a foreign-key matter: the composite keys cannot express "no cycles", so
        // this is the one placement rule the repository has to enforce itself.
        var world = await TwoSitesAsync();
        var folders = new FolderRepository(_database.DataSource);

        var child = new Folder
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteOne,
            ParentFolderId = world.FolderInSiteOne,
            Name = "Child",
        };
        await folders.AddAsync(child, CancellationToken.None);

        var parent = new Folder
        {
            Id = world.FolderInSiteOne,
            SiteId = world.SiteOne,
            ParentFolderId = child.Id,
            Name = "Parent",
        };

        await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => folders.UpdateAsync(parent, CancellationToken.None));
    }

    private sealed record World(Guid SiteOne, Guid SiteTwo, Guid FolderInSiteOne, Guid FolderInSiteTwo);

    /// <summary>Two sites under one tenant, each with a root folder.</summary>
    private async Task<World> TwoSitesAsync()
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tenant (id, name) VALUES (@tenant, 'Test tenant');
            INSERT INTO site (id, tenant_id, name) VALUES (@siteOne, @tenant, 'Site one'),
                                                         (@siteTwo, @tenant, 'Site two');
            INSERT INTO folder (id, site_id, parent_folder_id, name)
                VALUES (@folderOne, @siteOne, NULL, 'Root one'),
                       (@folderTwo, @siteTwo, NULL, 'Root two');
            """;
        command.Parameters.AddWithValue("tenant", Guid.NewGuid());
        command.Parameters.AddWithValue("siteOne", world.SiteOne);
        command.Parameters.AddWithValue("siteTwo", world.SiteTwo);
        command.Parameters.AddWithValue("folderOne", world.FolderInSiteOne);
        command.Parameters.AddWithValue("folderTwo", world.FolderInSiteTwo);
        await command.ExecuteNonQueryAsync();

        return world;
    }
}
