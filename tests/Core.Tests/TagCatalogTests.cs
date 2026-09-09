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

        internal TagCatalog Rebuild() => Catalog = new TagCatalog(Tenant, [Site], [Device], [Tag]);
    }
}
