using Dapper;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Templates;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0010's review criteria, as tests: instantiation materialises real per-instance
/// tags at distinct addresses, and a template edit reaches every instance.
/// </summary>
public sealed class DeviceTemplateTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public DeviceTemplateTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task Instantiating_materialises_a_real_tag_with_its_own_identity()
    {
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        var device = await InstantiateAsync(templates, world, name: "Pump 1", offset: "0");

        var materialised = Assert.Single(await tags.GetByDeviceAsync(device.Id, CancellationToken.None));
        Assert.Equal("Discharge Pressure", materialised.Name);
        Assert.Equal(world.PressureTemplateTagId, materialised.TemplateTagId);
        Assert.NotEqual(world.PressureTemplateTagId, materialised.Id);
        Assert.Equal("bar", materialised.Unit!.Symbol);
    }

    [RequiresDatabaseFact]
    public async Task Two_instances_with_different_parameters_get_different_addresses()
    {
        // Without this, "three devices from one template" would be three devices reading
        // the same register — the gate would pass on paper and mean nothing.
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        var first = await InstantiateAsync(templates, world, name: "Pump 1", offset: "0");
        var second = await InstantiateAsync(templates, world, name: "Pump 2", offset: "10");

        Assert.Equal(
            "holding:0?scale=0.01",
            Assert.Single(await tags.GetByDeviceAsync(first.Id, CancellationToken.None)).SourceAddress);
        Assert.Equal(
            "holding:10?scale=0.01",
            Assert.Single(await tags.GetByDeviceAsync(second.Id, CancellationToken.None)).SourceAddress);
    }

    [RequiresDatabaseFact]
    public async Task Three_instances_of_one_template_all_scan_at_distinct_addresses()
    {
        // This phase's own test gate, at the persistence level.
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        var devices = new List<Device>();
        foreach (var (name, offset) in new[] { ("Pump 1", "0"), ("Pump 2", "10"), ("Pump 3", "20") })
        {
            devices.Add(await InstantiateAsync(templates, world, name, offset));
        }

        var addresses = new List<string>();
        foreach (var device in devices)
        {
            addresses.Add(Assert.Single(await tags.GetByDeviceAsync(device.Id, CancellationToken.None)).SourceAddress);
        }

        Assert.Equal(3, addresses.Distinct().Count());
    }

    [RequiresDatabaseFact]
    public async Task Adding_a_tag_to_a_template_materialises_it_on_every_instance()
    {
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        var first = await InstantiateAsync(templates, world, name: "Pump 1", offset: "0");
        var second = await InstantiateAsync(templates, world, name: "Pump 2", offset: "10");

        var added = await templates.AddTagAsync(
            new DeviceTemplateTag
            {
                Id = Guid.NewGuid(),
                TemplateId = world.TemplateId,
                Name = "Running",
                ValueKind = TagValueKind.Boolean,
                AddressTemplate = "coil:{offset}",
            },
            CancellationToken.None);

        Assert.Equal(2, added);

        // Each instance resolves the new address from its own parameters, not the
        // template's text.
        Assert.Contains(
            await tags.GetByDeviceAsync(first.Id, CancellationToken.None),
            tag => tag.Name == "Running" && tag.SourceAddress == "coil:0");
        Assert.Contains(
            await tags.GetByDeviceAsync(second.Id, CancellationToken.None),
            tag => tag.Name == "Running" && tag.SourceAddress == "coil:10");
    }

    [RequiresDatabaseFact]
    public async Task Removing_a_tag_from_a_template_soft_deletes_it_on_every_instance()
    {
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);
        var tags = new TagRepository(_database.DataSource);

        var first = await InstantiateAsync(templates, world, name: "Pump 1", offset: "0");
        var second = await InstantiateAsync(templates, world, name: "Pump 2", offset: "10");

        var materialisedId = Assert.Single(await tags.GetByDeviceAsync(first.Id, CancellationToken.None)).Id;

        var affected = await templates.RemoveTagAsync(world.PressureTemplateTagId, CancellationToken.None);

        Assert.Equal(2, affected);
        Assert.Empty(await tags.GetByDeviceAsync(first.Id, CancellationToken.None));
        Assert.Empty(await tags.GetByDeviceAsync(second.Id, CancellationToken.None));

        // Soft-deleted, not erased: history stays reachable and still resolves a name
        // (ADR-0009).
        var identity = await tags.FindIdentityIncludingDeletedAsync(materialisedId, CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal("Discharge Pressure", identity!.TagName);
        Assert.True(identity.IsDeleted);
    }

    [RequiresDatabaseFact]
    public async Task An_instance_missing_a_parameter_is_refused_before_anything_is_written()
    {
        // Resolving a missing parameter to an empty string would give the device an
        // address that looks valid and reads the wrong place.
        var world = await SeedAsync();
        var templates = new DeviceTemplateRepository(_database.DataSource);

        var device = Device(world, "Pump without offset", parameters: new Dictionary<string, string>());

        await Assert.ThrowsAsync<TemplateParameterMissingException>(
            () => templates.InstantiateAsync(device, CancellationToken.None));

        var devices = new DeviceRepository(_database.DataSource);
        Assert.Null(await devices.FindAsync(device.Id, CancellationToken.None));
    }

    private static async Task<Device> InstantiateAsync(
        DeviceTemplateRepository templates,
        World world,
        string name,
        string offset)
    {
        var device = Device(world, name, new Dictionary<string, string> { ["offset"] = offset });
        await templates.InstantiateAsync(device, CancellationToken.None);
        return device;
    }

    private static Device Device(World world, string name, IReadOnlyDictionary<string, string> parameters) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = world.SiteId,
        Name = name,
        DriverKey = "modbus-tcp",
        TemplateId = world.TemplateId,
        TemplateParameters = parameters,
    };

    private sealed record World(Guid SiteId, Guid TemplateId, Guid PressureTemplateTagId);

    /// <summary>One site and one template carrying a single parameterised tag.</summary>
    private async Task<World> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO tenant (id, name) VALUES (@tenant, 'Test tenant');
            INSERT INTO site (id, tenant_id, name) VALUES (@site, @tenant, 'Site');
            INSERT INTO device_template (id, tenant_id, name) VALUES (@template, @tenant, 'Booster Pump');
            INSERT INTO device_template_tag
                (id, template_id, name, value_kind, unit_symbol, unit_dimension,
                 unit_factor_to_si, unit_offset_to_si, address_template, is_writable)
            VALUES (@templateTag, @template, 'Discharge Pressure', 0, 'bar', 1,
                    100000, 0, 'holding:{offset}?scale=0.01', false);
            """,
            new
            {
                tenant = tenantId,
                site = world.SiteId,
                template = world.TemplateId,
                templateTag = world.PressureTemplateTagId,
            });

        return world;
    }
}
