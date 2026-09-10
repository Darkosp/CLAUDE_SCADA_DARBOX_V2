using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Core.Templates;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Device templates and instantiation (ADR-0010).
/// </summary>
internal static class TemplateEndpoints
{
    internal static void MapTemplateApi(this WebApplication app)
    {
        app.MapGet("/api/templates", async (
            IDeviceTemplateRepository templates,
            CancellationToken cancellationToken) =>
            Results.Ok((await templates.GetAllAsync(cancellationToken)).Select(DeviceTemplateDto.From)));

        app.MapGet("/api/templates/{templateId:guid}/tags", async (
            Guid templateId,
            IDeviceTemplateRepository templates,
            CancellationToken cancellationToken) =>
            Results.Ok((await templates.GetTagsAsync(templateId, cancellationToken))
                .Select(TemplateTagDto.From)));

        app.MapPost("/api/templates", async (
            CreateTemplateRequest request,
            IDeviceTemplateRepository templates,
            IConfigurationStore store,
            CancellationToken cancellationToken) =>
        {
            var tenant = await store.GetTenantAsync(cancellationToken);
            var template = new DeviceTemplate
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = request.Name,
            };

            await templates.AddAsync(template, cancellationToken);
            return Results.Created($"/api/templates/{template.Id}", template.Id);
        });

        MapTemplateTags(app);
        MapInstantiation(app);
    }

    private static void MapTemplateTags(WebApplication app)
    {
        app.MapPost("/api/templates/{templateId:guid}/tags", async (
            Guid templateId,
            SaveTemplateTagRequest request,
            IDeviceTemplateRepository templates,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (ToDomain(Guid.NewGuid(), templateId, request, out var templateTag, out var error) is false)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                // The count is worth returning rather than swallowing: adding a tag to a
                // template silently changes every device made from it, and the operator
                // should see how many that was (ADR-0010).
                var instances = await templates.AddTagAsync(templateTag, cancellationToken);
                await reloader.ReloadAsync(cancellationToken);

                return Results.Ok(new { templateTagId = templateTag.Id, instancesUpdated = instances });
            }
            catch (TemplateParameterMissingException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapDelete("/api/templates/{templateId:guid}/tags/{templateTagId:guid}", async (
            Guid templateTagId,
            IDeviceTemplateRepository templates,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var instances = await templates.RemoveTagAsync(templateTagId, cancellationToken);
                await reloader.ReloadAsync(cancellationToken);

                return Results.Ok(new { instancesUpdated = instances });
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
    }

    private static void MapInstantiation(WebApplication app)
    {
        app.MapPost("/api/sites/{siteId:guid}/devices/from-template", async (
            Guid siteId,
            InstantiateDeviceRequest request,
            IDeviceTemplateRepository templates,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var device = new Device
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                FolderId = request.FolderId,
                Name = request.Name,
                DriverKey = request.DriverKey,
                ConnectionSettings = request.ConnectionSettings,
                ScanInterval = TimeSpan.FromMilliseconds(request.ScanIntervalMs),
                TemplateId = request.TemplateId,
                TemplateParameters = request.Parameters,
            };

            try
            {
                await templates.InstantiateAsync(device, cancellationToken);
            }
            catch (TemplateParameterMissingException exception)
            {
                // Nothing was written — the addresses are resolved before the first
                // insert — so this is a request to fix, not a half-created device.
                return Results.BadRequest(new { error = exception.Message });
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/devices/{device.Id}", device.Id);
        });
    }

    private static bool ToDomain(
        Guid id,
        Guid templateId,
        SaveTemplateTagRequest request,
        out DeviceTemplateTag templateTag,
        out string error)
    {
        templateTag = null!;
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
            error = "Only a numeric tag can carry a unit of measure.";
            return false;
        }

        templateTag = new DeviceTemplateTag
        {
            Id = id,
            TemplateId = templateId,
            Name = request.Name,
            ValueKind = valueKind,
            Unit = unit,
            AddressTemplate = request.AddressTemplate,
            IsWritable = request.IsWritable,
        };

        return true;
    }
}
