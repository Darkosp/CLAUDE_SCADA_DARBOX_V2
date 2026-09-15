using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Standing alarms, and the thresholds that raise them.
/// </summary>
internal static class AlarmEndpoints
{
    internal static void MapAlarmApi(this WebApplication app)
    {
        app.MapGet("/api/alarms", (IAlarmEngine alarms, TagCatalogSource catalogSource, Caller caller) =>
        {
            var catalog = catalogSource.Current;

            return Results.Ok(alarms.GetCurrent()
                .Where(alarm => caller.Access.CanSeeTag(catalog, alarm.TagId))
                .Select(alarm => AlarmDto.From(alarm, catalog.SiteOfTag(alarm.TagId))));
        });

        app.MapPost("/api/alarms/{definitionId:guid}/acknowledge", async (
            Guid definitionId,
            Caller caller,
            IAlarmEngine alarms,
            TagCatalogSource catalogSource,
            IAuditLog audit,
            TimeProvider timeProvider) =>
        {
            var (alarm, refusal) = Operable(definitionId, "Acknowledging", caller, alarms, catalogSource);
            if (alarm is null)
            {
                return refusal;
            }

            // Not tied to the request: once the alarm has changed state, the entry saying
            // who changed it is written even if the browser has gone away.
            var acknowledged = await alarms.AcknowledgeAsync(
                definitionId,
                timeProvider.GetUtcNow(),
                CancellationToken.None);

            // Not found rather than an error: an alarm that cleared and was retired
            // between the operator seeing it and clicking is a normal race, not a fault.
            if (!acknowledged)
            {
                return Results.NotFound();
            }

            // Who acknowledged — the gap Phase 3 deliberately left open until there were
            // users to name (ADR-0011).
            await audit.AppendAsync(Entry(caller, "alarm.acknowledge", alarm), CancellationToken.None);
            return Results.NoContent();
        });

        app.MapPost("/api/alarms/{definitionId:guid}/shelve", async (
            Guid definitionId,
            Caller caller,
            IAlarmEngine alarms,
            TagCatalogSource catalogSource,
            IAuditLog audit) =>
        {
            var (alarm, refusal) = Operable(definitionId, "Shelving", caller, alarms, catalogSource);
            if (alarm is null)
            {
                return refusal;
            }

            if (!await alarms.ShelveAsync(definitionId, CancellationToken.None))
            {
                return Results.NotFound();
            }

            await audit.AppendAsync(Entry(caller, "alarm.shelve", alarm), CancellationToken.None);
            return Results.NoContent();
        });

        MapDefinitions(app);
    }

    private static void MapDefinitions(WebApplication app)
    {
        app.MapGet("/api/tags/{tagId:guid}/alarms", (Guid tagId, TagCatalogSource catalogSource, Caller caller) =>
        {
            var catalog = catalogSource.Current;

            return caller.Access.CanSeeTag(catalog, tagId)
                ? Results.Ok(catalog.AlarmsOfTag(tagId).Select(AlarmDefinitionDto.From))
                : Results.NotFound();
        });

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
        }).AdminWrite("alarm_definition.create", "alarm_definition");

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
        }).AdminWrite("alarm_definition.update", "alarm_definition", "definitionId");

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
        }).AdminWrite("alarm_definition.delete", "alarm_definition", "definitionId");
    }

    /// <summary>
    /// The standing alarm, when the caller may take operational action on it; otherwise the
    /// response refusing them.
    /// </summary>
    private static (Alarm? Alarm, IResult Refusal) Operable(
        Guid definitionId,
        string action,
        Caller caller,
        IAlarmEngine alarms,
        TagCatalogSource catalogSource)
    {
        var catalog = catalogSource.Current;
        var alarm = alarms.GetCurrent().FirstOrDefault(standing => standing.DefinitionId == definitionId);

        // Not found for an alarm in a Site the caller cannot see — the same answer as for
        // one that does not exist, so the refusal does not confirm it is there.
        if (alarm is null || !caller.Access.CanSeeTag(catalog, alarm.TagId))
        {
            return (null, Results.NotFound());
        }

        return caller.Access.CanOperateTag(catalog, alarm.TagId)
            ? (alarm, Results.Empty)
            : (null, ApiErrors.Forbidden($"{action} an alarm needs the Operator role on its Site."));
    }

    private static AuditEntry Entry(Caller caller, string action, Alarm alarm) => new(
        caller.UserId,
        action,
        "alarm_definition",
        alarm.DefinitionId,
        Audit.Detail(("tagId", alarm.TagId), ("tagPath", alarm.TagPath), ("stateBefore", alarm.State.ToString())));

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
