using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using Xunit;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// How the browse tree behaves when the hierarchy is not in the shape it should be.
/// </summary>
/// <remarks>
/// Soft delete (ADR-0009) makes an inconsistent hierarchy reachable in a way it was not
/// before — a folder can go away while something still points at it — so the tree has to
/// have an answer other than dropping rows on the floor.
/// </remarks>
public class SiteTreeBuilderTests
{
    private static readonly Guid TenantId = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid SiteId = new("bbbbbbbb-0000-4000-8000-000000000001");

    [Fact]
    public void A_device_whose_folder_is_missing_is_shown_at_the_site_root()
    {
        // A device pointing at a folder that is not in the catalogue — deleted underneath
        // it. Grouped by folder alone it would land under a key nothing emits and vanish
        // from the tree, while the live tag list kept reporting its values. Misplaced is
        // recoverable; invisible is not.
        var orphan = Device(name: "Stranded Pump", folderId: Guid.NewGuid());
        var tree = SiteTreeBuilder.Build(Catalog([], [orphan], []), SiteId);

        Assert.NotNull(tree);
        Assert.Contains(tree!.Devices, device => device.Id == orphan.Id);
    }

    [Fact]
    public void A_device_in_a_folder_caught_in_a_cycle_is_shown_at_the_site_root()
    {
        // A cycle makes its folders unreachable from the root, so they are never rendered
        // — the device inside would otherwise be lost with them.
        var first = Folder("A");
        var second = Folder("B", parent: first.Id);
        first.ParentFolderId = second.Id;

        var device = Device(name: "Inside the cycle", folderId: second.Id);
        var tree = SiteTreeBuilder.Build(Catalog([first, second], [device], []), SiteId);

        Assert.NotNull(tree);
        Assert.Empty(tree!.Folders);
        Assert.Contains(tree.Devices, rendered => rendered.Id == device.Id);
    }

    [Fact]
    public void A_device_in_a_real_folder_is_not_moved_to_the_root()
    {
        var folder = Folder("Water Works");
        var device = Device(name: "Pump House", folderId: folder.Id);

        var tree = SiteTreeBuilder.Build(Catalog([folder], [device], []), SiteId);

        Assert.NotNull(tree);
        Assert.Empty(tree!.Devices);
        Assert.Contains(Assert.Single(tree.Folders).Devices, rendered => rendered.Id == device.Id);
    }

    private static Folder Folder(string name, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = SiteId,
        ParentFolderId = parent,
        Name = name,
    };

    private static Device Device(string name, Guid? folderId) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = SiteId,
        FolderId = folderId,
        Name = name,
        DriverKey = "modbus-tcp",
    };

    private static TagCatalog Catalog(
        IReadOnlyList<Folder> folders,
        IReadOnlyList<Device> devices,
        IReadOnlyList<Tag> tags) =>
        new(
            new Tenant { Id = TenantId, Name = "Darbo" },
            [new Site { Id = SiteId, TenantId = TenantId, Name = "Skopje" }],
            folders,
            devices,
            tags);
}
