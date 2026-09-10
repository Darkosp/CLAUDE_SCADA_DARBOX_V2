using Dapper;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The review criteria from ADR-0009, as tests: a deleted row must be gone from every
/// browsing path, a folder must not delete its contents out from under an operator, a
/// device must take its tags with it, and history must still resolve a name afterwards.
/// </summary>
public sealed class SoftDeleteTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public SoftDeleteTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_deleted_tag_disappears_from_every_read_path()
    {
        var world = await SeedAsync();
        var tags = new TagRepository(_database.DataSource);
        var store = new PostgresConfigurationStore(_database.DataSource);

        await tags.DeleteAsync(world.TagId, CancellationToken.None);

        Assert.DoesNotContain(
            await tags.GetByDeviceAsync(world.DeviceId, CancellationToken.None),
            tag => tag.Id == world.TagId);

        // The store is what the tree API and the tag catalogue are built from, so this
        // covers browsing and hot-add as well as the repository's own listing.
        Assert.DoesNotContain(
            await store.GetTagsAsync(CancellationToken.None),
            tag => tag.Id == world.TagId);
    }

    [RequiresDatabaseFact]
    public async Task A_deleted_device_disappears_from_every_read_path()
    {
        var world = await SeedAsync();
        var devices = new DeviceRepository(_database.DataSource);
        var store = new PostgresConfigurationStore(_database.DataSource);

        await devices.DeleteAsync(world.DeviceId, CancellationToken.None);

        Assert.Null(await devices.FindAsync(world.DeviceId, CancellationToken.None));
        Assert.DoesNotContain(
            await devices.GetBySiteAsync(world.SiteId, CancellationToken.None),
            device => device.Id == world.DeviceId);
        Assert.DoesNotContain(
            await store.GetDevicesAsync(CancellationToken.None),
            device => device.Id == world.DeviceId);
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_device_takes_its_tags_with_it()
    {
        var world = await SeedAsync();
        var devices = new DeviceRepository(_database.DataSource);
        var store = new PostgresConfigurationStore(_database.DataSource);

        await devices.DeleteAsync(world.DeviceId, CancellationToken.None);

        Assert.DoesNotContain(
            await store.GetTagsAsync(CancellationToken.None),
            tag => tag.Id == world.TagId);
    }

    [RequiresDatabaseFact]
    public async Task A_folder_holding_a_device_cannot_be_deleted_and_nothing_changes()
    {
        var world = await SeedAsync(deviceInFolder: true);
        var folders = new FolderRepository(_database.DataSource);
        var store = new PostgresConfigurationStore(_database.DataSource);

        var exception = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => folders.DeleteAsync(world.FolderId, CancellationToken.None));

        Assert.Contains("still contains", exception.Message, StringComparison.OrdinalIgnoreCase);

        // No partial side effect: the refusal must leave both the folder and the device
        // exactly as they were, not delete one of them on the way to failing.
        Assert.Contains(
            await store.GetFoldersAsync(CancellationToken.None),
            folder => folder.Id == world.FolderId);
        Assert.Contains(
            await store.GetDevicesAsync(CancellationToken.None),
            device => device.Id == world.DeviceId);
    }

    [RequiresDatabaseFact]
    public async Task A_folder_holding_a_child_folder_cannot_be_deleted()
    {
        var world = await SeedAsync();
        var folders = new FolderRepository(_database.DataSource);

        var child = new Folder
        {
            Id = Guid.NewGuid(),
            SiteId = world.SiteId,
            ParentFolderId = world.FolderId,
            Name = "Child",
        };
        await folders.AddAsync(child, CancellationToken.None);

        await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => folders.DeleteAsync(world.FolderId, CancellationToken.None));
    }

    [RequiresDatabaseFact]
    public async Task An_emptied_folder_can_then_be_deleted()
    {
        // The other half of the rule: refusing to cascade is only reasonable if emptying
        // the folder by hand actually unblocks the delete.
        var world = await SeedAsync(deviceInFolder: true);
        var folders = new FolderRepository(_database.DataSource);
        var devices = new DeviceRepository(_database.DataSource);
        var store = new PostgresConfigurationStore(_database.DataSource);

        await devices.DeleteAsync(world.DeviceId, CancellationToken.None);
        await folders.DeleteAsync(world.FolderId, CancellationToken.None);

        Assert.DoesNotContain(
            await store.GetFoldersAsync(CancellationToken.None),
            folder => folder.Id == world.FolderId);
    }

    [RequiresDatabaseFact]
    public async Task History_still_resolves_a_name_after_the_device_is_deleted()
    {
        var world = await SeedAsync();
        var devices = new DeviceRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        await devices.DeleteAsync(world.DeviceId, CancellationToken.None);

        var identity = await tags.FindIdentityIncludingDeletedAsync(world.TagId, CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("Discharge Pressure", identity!.TagName);
        Assert.Equal("Pump House", identity.DeviceName);
        Assert.True(identity.IsDeleted);
    }

    [RequiresDatabaseFact]
    public async Task A_deleted_tag_cannot_be_edited_back_into_existence()
    {
        var world = await SeedAsync();
        var tags = new TagRepository(_database.DataSource);

        await tags.DeleteAsync(world.TagId, CancellationToken.None);

        var revived = new Tag
        {
            Id = world.TagId,
            DeviceId = world.DeviceId,
            Name = "Renamed",
            ValueKind = TagValueKind.Numeric,
            SourceAddress = "holding:0",
        };

        await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => tags.UpdateAsync(revived, CancellationToken.None));
    }

    [RequiresDatabaseFact]
    public async Task A_tag_is_never_live_when_its_device_is_deleted_even_without_the_cascade()
    {
        // Deleting a device cascades to its tags in one transaction, so a crash between
        // the two statements commits nothing. What a transaction cannot cover is a tag
        // inserted by another transaction committing after the cascade's UPDATE has run:
        // that row is never seen, and would survive as a live tag on a dead device.
        //
        // This reproduces that end state directly — the device row soft-deleted, the tag
        // untouched — and asserts the view refuses to hand it out anyway.
        var world = await SeedAsync();
        var store = new PostgresConfigurationStore(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        await using (var connection = await _database.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE device SET deleted_at = now() WHERE id = @deviceId",
                new { deviceId = world.DeviceId });
        }

        Assert.DoesNotContain(
            await store.GetTagsAsync(CancellationToken.None),
            tag => tag.Id == world.TagId);
        Assert.Empty(await tags.GetByDeviceAsync(world.DeviceId, CancellationToken.None));
    }

    private sealed record World(Guid SiteId, Guid FolderId, Guid DeviceId, Guid TagId);

    /// <summary>One site with a folder, a device and a tag, all live.</summary>
    private async Task<World> SeedAsync(bool deviceInFolder = false)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO tenant (id, name) VALUES (@tenant, 'Test tenant');
            INSERT INTO site (id, tenant_id, name) VALUES (@site, @tenant, 'Site');
            INSERT INTO folder (id, site_id, parent_folder_id, name)
                VALUES (@folder, @site, NULL, 'Water Works');
            INSERT INTO device (id, site_id, folder_id, name, driver_key)
                VALUES (@device, @site, @folderId, 'Pump House', 'modbus-tcp');
            INSERT INTO tag (id, device_id, name, value_kind, source_address)
                VALUES (@tag, @device, 'Discharge Pressure', 0, 'holding:0');
            """,
            new
            {
                tenant = Guid.NewGuid(),
                site = world.SiteId,
                folder = world.FolderId,
                device = world.DeviceId,
                tag = world.TagId,
                folderId = deviceInFolder ? world.FolderId : (Guid?)null,
            });

        return world;
    }
}
