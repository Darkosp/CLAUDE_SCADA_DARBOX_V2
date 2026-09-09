using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

public class TagCatalogTests
{
    [Fact]
    public void Renaming_a_site_changes_the_display_path_but_not_the_tag_identity()
    {
        // ADR-0001's central promise: reorganising for readability is never a migration.
        var fixture = new HierarchyFixture();
        var before = fixture.Catalog;

        Assert.Equal("Skopje/Pump House/Discharge Pressure", before.PathOf(fixture.TagId));

        fixture.Site.Name = "Skopje North";
        fixture.Device.Name = "Booster Set 1";
        var after = fixture.Rebuild();

        Assert.Equal("Skopje North/Booster Set 1/Discharge Pressure", after.PathOf(fixture.TagId));
        Assert.Equal(fixture.TagId, after.FindTag(fixture.TagId)!.Id);
    }

    [Fact]
    public void Tenant_is_absent_from_the_display_path()
    {
        // ADR-0004: tenancy is in the model, never in what the operator reads.
        var fixture = new HierarchyFixture();

        Assert.DoesNotContain(fixture.Catalog.Tenant.Name, fixture.Catalog.PathOf(fixture.TagId));
    }

    [Fact]
    public void Folders_between_site_and_device_appear_in_the_display_path()
    {
        var fixture = new HierarchyFixture();
        var area = fixture.AddFolder("Water Works", parent: null);
        var line = fixture.AddFolder("Line 2", parent: area);
        fixture.Device.FolderId = line.Id;

        var catalog = fixture.Rebuild();

        Assert.Equal(
            "Skopje/Water Works/Line 2/Pump House/Discharge Pressure",
            catalog.PathOf(fixture.TagId));
    }

    [Fact]
    public void Moving_a_device_between_folders_changes_only_the_path()
    {
        // ADR-0001 §6: a folder carries no functional behaviour, so reorganising is
        // never a migration and never touches identity.
        var fixture = new HierarchyFixture();
        var first = fixture.AddFolder("Water Works", parent: null);
        var second = fixture.AddFolder("Boiler House", parent: null);

        fixture.Device.FolderId = first.Id;
        var before = fixture.Rebuild().PathOf(fixture.TagId);

        fixture.Device.FolderId = second.Id;
        var after = fixture.Rebuild();

        Assert.Equal("Skopje/Water Works/Pump House/Discharge Pressure", before);
        Assert.Equal("Skopje/Boiler House/Pump House/Discharge Pressure", after.PathOf(fixture.TagId));
        Assert.Equal(fixture.TagId, after.FindTag(fixture.TagId)!.Id);
    }

    [Fact]
    public void A_device_with_no_folder_sits_directly_under_its_site()
    {
        var fixture = new HierarchyFixture();
        fixture.AddFolder("Water Works", parent: null);

        Assert.Null(fixture.Device.FolderId);
        Assert.Equal("Skopje/Pump House/Discharge Pressure", fixture.Rebuild().PathOf(fixture.TagId));
    }

    [Fact]
    public void A_folder_cycle_does_not_hang_path_building()
    {
        // The composite foreign keys stop cross-site placement but cannot express
        // "no cycles", so reading has to survive one that reached the database anyway.
        var fixture = new HierarchyFixture();
        var outer = fixture.AddFolder("A", parent: null);
        var inner = fixture.AddFolder("B", parent: outer);
        outer.ParentFolderId = inner.Id;
        fixture.Device.FolderId = inner.Id;

        var path = fixture.Rebuild().PathOf(fixture.TagId);

        Assert.EndsWith("Pump House/Discharge Pressure", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_are_grouped_by_their_owning_device()
    {
        var fixture = new HierarchyFixture();

        var tags = fixture.Catalog.TagsOfDevice(fixture.Device.Id);

        Assert.Equal([fixture.TagId], tags.Select(t => t.Id));
    }

    private sealed class HierarchyFixture
    {
        internal Guid TagId { get; } = Guid.NewGuid();

        internal Tenant Tenant { get; } = new() { Id = Guid.NewGuid(), Name = "Darbo" };

        internal Site Site { get; }

        internal Device Device { get; }

        internal Tag Tag { get; }

        /// <summary>Folders the next <see cref="Rebuild"/> will be built from.</summary>
        internal List<Folder> Folders { get; } = [];

        internal TagCatalog Catalog { get; private set; }

        internal HierarchyFixture()
        {
            Site = new Site { Id = Guid.NewGuid(), TenantId = Tenant.Id, Name = "Skopje" };
            Device = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = Site.Id,
                Name = "Pump House",
                DriverKey = "modbus-tcp",
            };
            Tag = new Tag
            {
                Id = TagId,
                DeviceId = Device.Id,
                Name = "Discharge Pressure",
                ValueKind = TagValueKind.Numeric,
                Unit = new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
                SourceAddress = "holding:0",
            };

            Catalog = Rebuild();
        }

        /// <summary>Adds a folder to the site and returns it, for the next rebuild.</summary>
        internal Folder AddFolder(string name, Folder? parent)
        {
            var folder = new Folder
            {
                Id = Guid.NewGuid(),
                SiteId = Site.Id,
                ParentFolderId = parent?.Id,
                Name = name,
            };

            Folders.Add(folder);
            return folder;
        }

        internal TagCatalog Rebuild() =>
            Catalog = new TagCatalog(Tenant, [Site], Folders, [Device], [Tag]);
    }
}
