using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0015: a device, tag or folder name is unique within its parent among live rows,
/// ignoring case, and the API says so with 409.
/// </summary>
/// <remarks>
/// Through the real Gateway, connected as the application role (ADR-0011), so the index is
/// what is being tested and not a check the API happens to make first. Every refusal is also
/// counted in the table itself: a 409 that let a second row through would not be a refusal.
/// Each test uses names of its own, since the class shares one database.
/// </remarks>
public sealed class UniqueNameTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public UniqueNameTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task The_same_device_twice_directly_under_a_site_leaves_one_device_and_a_409()
    {
        // The null parent: folder_id IS NULL. Under NULLS DISTINCT this is the case the index
        // would silently let through.
        var name = Unique("Booster");
        using var admin = await AdminAsync();

        using var first = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folderId: null));
        using var second = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folderId: null));

        Assert.Equal(1, await CountAsync("device", name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictNamingAsync(second, name);
    }

    [RequiresDatabaseFact]
    public async Task The_same_device_twice_in_one_folder_leaves_one_device_and_a_409()
    {
        var name = Unique("Booster");
        using var admin = await AdminAsync();
        var folder = await CreateFolderAsync(admin, Skopje, Unique("Hall"), parent: null);

        using var first = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folder));
        using var second = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folder));

        Assert.Equal(1, await CountAsync("device", name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictNamingAsync(second, name);
    }

    [RequiresDatabaseFact]
    public async Task The_same_root_folder_twice_leaves_one_folder_and_a_409()
    {
        // The other null parent: parent_folder_id IS NULL.
        var name = Unique("Water Works");
        using var admin = await AdminAsync();

        using var first = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/folders", new CreateFolderRequest(name, ParentFolderId: null));
        using var second = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/folders", new CreateFolderRequest(name, ParentFolderId: null));

        Assert.Equal(1, await CountAsync("folder", name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictNamingAsync(second, name);
    }

    [RequiresDatabaseFact]
    public async Task The_same_subfolder_twice_leaves_one_folder_and_a_409()
    {
        var name = Unique("Basement");
        using var admin = await AdminAsync();
        var parent = await CreateFolderAsync(admin, Skopje, Unique("Building"), parent: null);

        using var first = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/folders", new CreateFolderRequest(name, parent));
        using var second = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/folders", new CreateFolderRequest(name, parent));

        Assert.Equal(1, await CountAsync("folder", name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictNamingAsync(second, name);
    }

    [RequiresDatabaseFact]
    public async Task The_same_tag_twice_on_one_device_leaves_one_tag_and_a_409()
    {
        var name = Unique("Discharge");
        using var admin = await AdminAsync();
        var device = await CreateDeviceAsync(admin, Skopje, Unique("Pump"), folderId: null);

        using var first = await admin.PostAsJsonAsync($"/api/devices/{device}/tags", Tag(name));
        using var second = await admin.PostAsJsonAsync($"/api/devices/{device}/tags", Tag(name));

        Assert.Equal(1, await CountAsync("tag", name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictNamingAsync(second, name);
    }

    [RequiresDatabaseFact]
    public async Task Names_that_differ_only_in_case_collide()
    {
        var name = Unique("Pump House");
        using var admin = await AdminAsync();

        using var first = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folderId: null));
        using var second = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name.ToUpperInvariant(), folderId: null));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountAsync("device", name));
    }

    [RequiresDatabaseFact]
    public async Task The_same_name_under_a_different_parent_is_allowed()
    {
        var name = Unique("Booster");
        using var admin = await AdminAsync();
        var folder = await CreateFolderAsync(admin, Skopje, Unique("Hall"), parent: null);

        // Directly under Skopje, in a folder of Skopje, and directly under Bitola: three parents.
        var underSite = await CreateDeviceAsync(admin, Skopje, name, folderId: null);
        await CreateDeviceAsync(admin, Skopje, name, folder);
        var otherSite = await CreateDeviceAsync(admin, Bitola, name, folderId: null);

        // And one tag name on two devices.
        using var tagOnOne = await admin.PostAsJsonAsync($"/api/devices/{underSite}/tags", Tag("Pressure"));
        using var tagOnOther = await admin.PostAsJsonAsync($"/api/devices/{otherSite}/tags", Tag("Pressure"));

        Assert.Equal(3, await CountAsync("device", name));
        Assert.Equal(HttpStatusCode.Created, tagOnOne.StatusCode);
        Assert.Equal(HttpStatusCode.Created, tagOnOther.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_name_freed_by_a_soft_delete_can_be_used_again()
    {
        var name = Unique("Booster");
        using var admin = await AdminAsync();
        var original = await CreateDeviceAsync(admin, Skopje, name, folderId: null);

        using var deleted = await admin.DeleteAsync($"/api/sites/{Skopje}/devices/{original}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var again = await admin.PostAsJsonAsync($"/api/sites/{Skopje}/devices", Device(name, folderId: null));

        // Two rows, one of them deleted: ADR-0009 keeps the row, ADR-0015 does not keep the name.
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(2, await CountAsync("device", name, includeDeleted: true));
        Assert.Equal(1, await CountAsync("device", name));
    }

    [RequiresDatabaseFact]
    public async Task Renaming_onto_a_name_already_taken_is_refused()
    {
        var taken = Unique("Booster");
        using var admin = await AdminAsync();
        await CreateDeviceAsync(admin, Skopje, taken, folderId: null);
        var other = await CreateDeviceAsync(admin, Skopje, Unique("Spare"), folderId: null);

        using var rename = await admin.PutAsJsonAsync($"/api/sites/{Skopje}/devices/{other}", Device(taken, folderId: null));

        await AssertConflictNamingAsync(rename, taken);
        Assert.Equal(1, await CountAsync("device", taken));
    }

    [RequiresDatabaseFact]
    public async Task A_device_from_a_template_under_a_taken_name_is_refused_whole()
    {
        var name = Unique("Booster");
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);
        await CreateDeviceAsync(client, Skopje, name, folderId: null);

        var template = await _host.CreateTemplateAsync(admin, Unique("Booster template"));
        using var templateTag = await client.PostAsJsonAsync(
            $"/api/templates/{template}/tags",
            new SaveTemplateTagRequest("Pressure", "Numeric", Unit: null, "holding:0", IsWritable: false));
        templateTag.EnsureSuccessStatusCode();

        using var instance = await client.PostAsJsonAsync(
            $"/api/sites/{Skopje}/devices/from-template",
            new InstantiateDeviceRequest(template, name, "modbus-tcp", new Dictionary<string, string>(), 1000, FolderId: null, new Dictionary<string, string>()));

        await AssertConflictNamingAsync(instance, name);

        // Rolled back together: no second device, and no orphaned tag from the template.
        Assert.Equal(1, await CountAsync("device", name));
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM tag t JOIN device_template_tag tt ON tt.id = t.template_tag_id WHERE tt.template_id = @template",
            ("template", template)));
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static SaveDeviceRequest Device(string name, Guid? folderId) =>
        new(name, "modbus-tcp", new Dictionary<string, string> { ["host"] = "127.0.0.1" }, 1000, folderId);

    private static SaveTagRequest Tag(string name) => new(name, "Numeric", Unit: null, "holding:0", IsWritable: false);

    private async Task<HttpClient> AdminAsync() => _host.CreateClient(await _host.LoginAsAdminAsync());

    private static async Task<Guid> CreateDeviceAsync(HttpClient admin, Guid siteId, string name, Guid? folderId)
    {
        using var response = await admin.PostAsJsonAsync($"/api/sites/{siteId}/devices", Device(name, folderId));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private static async Task<Guid> CreateFolderAsync(HttpClient admin, Guid siteId, string name, Guid? parent)
    {
        using var response = await admin.PostAsJsonAsync($"/api/sites/{siteId}/folders", new CreateFolderRequest(name, parent));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private static async Task AssertConflictNamingAsync(HttpResponseMessage response, string name)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error").GetString();
        Assert.Contains($"'{name}'", error, StringComparison.Ordinal);
        Assert.Contains("already exists", error, StringComparison.Ordinal);
    }

    /// <summary>Rows in <paramref name="table"/> with exactly this name, read over the Gateway's own connection.</summary>
    private Task<long> CountAsync(string table, string name, bool includeDeleted = false) =>
        ScalarAsync(
            $"SELECT count(*) FROM {table} WHERE lower(name) = lower(@name){(includeDeleted ? "" : " AND deleted_at IS NULL")}",
            ("name", name));

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _host.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
