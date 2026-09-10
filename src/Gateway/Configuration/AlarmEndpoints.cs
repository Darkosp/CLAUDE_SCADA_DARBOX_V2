using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Standing alarms, and the thresholds that raise them.
/// </summary>
internal static class AlarmEndpoints
{
    internal static void MapAlarmApi(this WebApplication app)
    {
        app.MapGet("/api/alarms", (IAlarmEngine alarms) =>
            Results.Ok(alarms.GetCurrent().Select(AlarmDto.From)));

        app.MapPost("/api/alarms/{definitionId:guid}/acknowledge", async (
            Guid definitionId,
            IAlarmEngine alarms,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var acknowledged = await alarms.AcknowledgeAsync(
                definitionId,
                timeProvider.GetUtcNow(),
                cancellationToken);

            // Not found rather than an error: an alarm that cleared and was retired
            // between the operator seeing it and clicking is a normal race, not a fault.
            return acknowledged ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/alarms/{definitionId:guid}/shelve", async (
            Guid definitionId,
            IAlarmEngine alarms,
            CancellationToken cancellationToken) =>
            await alarms.ShelveAsync(definitionId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        MapDefinitions(app);
    }

    private static void MapDefinitions(WebApplication app)
    {
        app.MapGet("/api/tags/{tagId:guid}/alarms", (Guid tagId, TagCatalogSource catalogSource) =>
            Results.Ok(catalogSource.Current.AlarmsOfTag(tagId).Select(AlarmDefinitionDto.From)));

        app.MapPost("/api/tags/{tagId:guid}/alarms", async (
            Guid tagId,
            SaveAlarmDefinitionRequest request,
            IAlarmDefinitionRepository definitions,
            TagCatalogSource catalogSource,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (Reject(request, tagId, catalogSource) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var definition = new AlarmDefinition
            {
                Id = Guid.NewGuid(),
                TagId = tagId,
                HighLimit = request.HighLimit,
                LowLimit = request.LowLimit,
            };

            await definitions.AddAsync(definition, cancellationToken);
            await reloader.ReloadAsync(cancellationToken);

            return Results.Created($"/api/tags/{tagId}/alarms", definition.Id);
        });

        app.MapPut("/api/tags/{tagId:guid}/alarms/{definitionId:guid}", async (
            Guid tagId,
            Guid definitionId,
            SaveAlarmDefinitionRequest request,
            IAlarmDefinitionRepository definitions,
            TagCatalogSource catalogSource,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (Reject(request, tagId, catalogSource) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                await definitions.UpdateAsync(
                    new AlarmDefinition
                    {
                        Id = definitionId,
                        TagId = tagId,
                        HighLimit = request.HighLimit,
                        LowLimit = request.LowLimit,
                    },
                    cancellationToken);
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        });

        app.MapDelete("/api/tags/{tagId:guid}/alarms/{definitionId:guid}", async (
            Guid definitionId,
            IAlarmDefinitionRepository definitions,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await definitions.DeleteAsync(definitionId, cancellationToken);
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Why this threshold cannot be saved, or null when it can.
    /// </summary>
    private static string? Reject(
        SaveAlarmDefinitionRequest request,
        Guid tagId,
        TagCatalogSource catalogSource)
    {
        if (request.HighLimit is null && request.LowLimit is null)
        {
            // The database refuses this too, but a rejected insert would surface as a
            // constraint name rather than something an operator can act on.
            return "An alarm needs at least a high or a low limit.";
        }

        if (request.HighLimit is { } high && request.LowLimit is { } low && low >= high)
        {
            // Every reading would be in alarm on one limit or the other, so the alarm
            // could never return to normal.
            return "The low limit must be below the high limit.";
        }

        var tag = catalogSource.Current.FindTag(tagId);

        if (tag is null)
        {
            return "That tag does not exist.";
        }

        if (tag.ValueKind != TagValueKind.Numeric)
        {
            // Thresholds compare magnitudes. A boolean or text tag has none, so the
            // condition could never be evaluated.
            return "Only a numeric tag can carry an alarm threshold.";
        }

        return null;
    }
}
