using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Browse-tree and configuration endpoints: everything Phase 2 needs to add and edit a
/// device through the UI, with no code required.
/// </summary>
internal static class ConfigurationEndpoints
{
    internal static void MapConfigurationApi(this WebApplication app)
    {
        app.MapGet("/api/sites", (TagCatalogSource catalogSource) =>
            Results.Ok(catalogSource.Current.Sites
                .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                .Select(site => new SiteDto(site.Id, site.Name, site.TimeZoneId))));

        app.MapGet("/api/sites/{siteId:guid}/tree", (Guid siteId, TagCatalogSource catalogSource) =>
        {
            var tree = SiteTreeBuilder.Build(catalogSource.Current, siteId);
            return tree is null ? Results.NotFound() : Results.Ok(tree);
        });

        MapFolders(app);
        MapDevices(app);
        MapTags(app);
        MapTagDeletion(app);
    }

    private static void MapFolders(WebApplication app)
    {
        app.MapPost("/api/sites/{siteId:guid}/folders", async (
            Guid siteId,
            CreateFolderRequest request,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var folder = new Folder
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                ParentFolderId = request.ParentFolderId,
                Name = request.Name,
            };

            return await SaveAsync(
                () => folders.AddAsync(folder, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/sites/{siteId}/tree", folder.Id));
        });

        app.MapDelete("/api/sites/{siteId:guid}/folders/{folderId:guid}", async (
            Guid folderId,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => folders.DeleteAsync(folderId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent));

        app.MapPut("/api/sites/{siteId:guid}/folders/{folderId:guid}", async (
            Guid siteId,
            Guid folderId,
            UpdateFolderRequest request,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var folder = new Folder
            {
                Id = folderId,
                SiteId = siteId,
                ParentFolderId = request.ParentFolderId,
                Name = request.Name,
            };

            return await SaveAsync(
                () => folders.UpdateAsync(folder, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        });
    }

    private static void MapDevices(WebApplication app)
    {
        app.MapPost("/api/sites/{siteId:guid}/devices", async (
            Guid siteId,
            SaveDeviceRequest request,
            IDeviceRepository devices,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var device = ToDomain(Guid.NewGuid(), siteId, request);

            return await SaveAsync(
                () => devices.AddAsync(device, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/devices/{device.Id}", device.Id));
        });

        app.MapPut("/api/sites/{siteId:guid}/devices/{deviceId:guid}", async (
            Guid siteId,
            Guid deviceId,
            SaveDeviceRequest request,
            IDeviceRepository devices,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => devices.UpdateAsync(ToDomain(deviceId, siteId, request), cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent));

        app.MapDelete("/api/sites/{siteId:guid}/devices/{deviceId:guid}", async (
            Guid deviceId,
            IDeviceRepository devices,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => devices.DeleteAsync(deviceId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent));

        app.MapGet("/api/devices/{deviceId:guid}", (Guid deviceId, TagCatalogSource catalogSource) =>
        {
            var catalog = catalogSource.Current;
            var device = catalog.FindDevice(deviceId);

            return device is null
                ? Results.NotFound()
                : Results.Ok(SiteTreeBuilder.ToDto(device, catalog));
        });
    }

    private static void MapTags(WebApplication app)
    {
        app.MapPost("/api/devices/{deviceId:guid}/tags", async (
            Guid deviceId,
            SaveTagRequest request,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (!TryToDomain(Guid.NewGuid(), deviceId, request, out var tag, out var error))
            {
                return Results.BadRequest(new { error });
            }

            return await SaveAsync(
                () => tags.AddAsync(tag, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/tags/{tag.Id}", tag.Id));
        });

        app.MapPut("/api/devices/{deviceId:guid}/tags/{tagId:guid}", async (
            Guid deviceId,
            Guid tagId,
            SaveTagRequest request,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (!TryToDomain(tagId, deviceId, request, out var tag, out var error))
            {
                return Results.BadRequest(new { error });
            }

            return await SaveAsync(
                () => tags.UpdateAsync(tag, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        });
    }

    private static void MapTagDeletion(WebApplication app) =>
        app.MapDelete("/api/devices/{deviceId:guid}/tags/{tagId:guid}", async (
            Guid tagId,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => tags.DeleteAsync(tagId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent));

    /// <summary>
    /// Runs a configuration write, reloads the catalogue on success, and turns the
    /// failures this layer can expect into client errors rather than 500s.
    /// </summary>
    private static async Task<IResult> SaveAsync(
        Func<Task> write,
        ConfigurationReloader reloader,
        CancellationToken cancellationToken,
        Func<IResult> success)
    {
        try
        {
            await write();
        }
        catch (ConfigurationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (PostgresException exception) when (exception.SqlState == "23503")
        {
            // The composite foreign keys are the enforcement point for cross-site
            // placement, so this is a request the database refused, not a server fault.
            return Results.BadRequest(new
            {
                error = exception.ConstraintName switch
                {
                    "fk_folder_parent_same_site" =>
                        "A folder's parent must belong to the same site as the folder.",
                    "fk_device_folder_same_site" =>
                        "A device's folder must belong to the same site as the device.",
                    _ => "The referenced site, folder or device does not exist.",
                },
            });
        }

        // Only after the write lands: a reload on a failed write would republish the
        // catalogue that is already in force, for nothing.
        await reloader.ReloadAsync(cancellationToken);
        return success();
    }

    private static Device ToDomain(Guid id, Guid siteId, SaveDeviceRequest request) => new()
    {
        Id = id,
        SiteId = siteId,
        FolderId = request.FolderId,
        Name = request.Name,
        DriverKey = request.DriverKey,
        ConnectionSettings = request.ConnectionSettings,
        ScanInterval = TimeSpan.FromMilliseconds(request.ScanIntervalMs),
    };

    private static bool TryToDomain(
        Guid id,
        Guid deviceId,
        SaveTagRequest request,
        out Tag tag,
        out string error)
    {
        tag = null!;
        error = string.Empty;

        if (!Enum.TryParse<TagValueKind>(request.ValueKind, ignoreCase: true, out var valueKind))
        {
            error = $"Unknown value kind '{request.ValueKind}'.";
            return false;
        }

        UnitOfMeasure? unit;
        try
        {
            unit = request.Unit?.ToDomain();
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        if (unit is not null && valueKind != TagValueKind.Numeric)
        {
            // A unit describes a measured quantity. Attaching one to a boolean or a text
            // tag would give the UI something to display that means nothing.
            error = "Only a numeric tag can carry a unit of measure.";
            return false;
        }

        tag = new Tag
        {
            Id = id,
            DeviceId = deviceId,
            Name = request.Name,
            ValueKind = valueKind,
            Unit = unit,
            SourceAddress = request.SourceAddress,
            IsWritable = request.IsWritable,
        };

        return true;
    }
}
