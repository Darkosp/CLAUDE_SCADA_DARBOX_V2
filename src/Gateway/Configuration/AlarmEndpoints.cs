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
        // Filtered by the Site fixed when each alarm was raised (ADR-0013), so an alarm whose
        // tag has since been deleted is still shown to exactly the people who could see it.
        app.MapGet("/api/alarms", (IAlarmEngine alarms, Caller caller) =>
            Results.Ok(alarms.GetCurrent()
                .Where(alarm => caller.Access.CanView(alarm.SiteId))
                .Select(AlarmDto.From)));

        // The journal, newest first. The Site filter lives in the query rather than here:
        // filtering in memory would mean reading rows the caller may not see in order to
        // discard them, and the limit would then be applied to the wrong set.
        app.MapGet("/api/alarms/journal", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            Caller caller,
            IAlarmJournal journal) =>
        {
            // An Admin is unrestricted (null); anyone else is held to their own Sites.
            // Engine events reach both — see AlarmJournalQuery.
            var sites = caller.Access.IsAdmin
                ? null
                : caller.Access.SiteRoles.Keys.ToArray();

            var events = await journal.ReadHistoryAsync(
                new AlarmJournalQuery(sites, from, to, Math.Clamp(limit ?? 200, 1, 1000)),
                CancellationToken.None);

            return Results.Ok(events.Select(AlarmEventDto.From));
        });

        app.MapPost("/api/alarms/{definitionId:guid}/acknowledge", async (
            Guid definitionId,
            Caller caller,
            IAlarmEngine alarms,
            IAuditLog audit) =>
        {
            var (alarm, refusal) = Operable(definitionId, "Acknowledging", caller, alarms);
            if (alarm is null)
            {
                return refusal;
            }

            // Not tied to the request: once the alarm has changed state, the entry saying
            // who changed it is written even if the browser has gone away.
            bool acknowledged;
            try
            {
                acknowledged = await alarms.AcknowledgeAsync(definitionId, Actor(caller), CancellationToken.None);
            }
            catch (AlarmJournalUnavailableException)
            {
                return JournalUnavailable();
            }

            // Not found rather than an error: an alarm that cleared and was retired
            // between the operator seeing it and clicking is a normal race, not a fault.
            if (!acknowledged)
            {
                return Results.NotFound();
            }

            await audit.AppendAsync(Entry(caller, "alarm.acknowledge", alarm), CancellationToken.None);
            return Results.NoContent();
        });

        app.MapPost("/api/alarms/{definitionId:guid}/shelve", async (
            Guid definitionId,
            ShelveRequest? request,
            Caller caller,
            IAlarmEngine alarms,
            IAuditLog audit) =>
        {
            var (alarm, refusal) = Operable(definitionId, "Shelving", caller, alarms);
            if (alarm is null)
            {
                return refusal;
            }

            // A shelf always ends (ADR-0013): no duration is a malformed request, not an
            // indefinite suppression.
            if (request?.DurationMinutes is not { } minutes)
            {
                return Results.BadRequest(new { error = "A shelve needs a duration." });
            }

            ShelveOutcome outcome;
            try
            {
                outcome = await alarms.ShelveAsync(
                    definitionId,
                    Actor(caller),
                    TimeSpan.FromMinutes(minutes),
                    CancellationToken.None);
            }
            catch (AlarmJournalUnavailableException)
            {
                return JournalUnavailable();
            }

            switch (outcome)
            {
                case ShelveOutcome.NotFound:
                    return Results.NotFound();

                case ShelveOutcome.NotInAlarm:
                    return Results.Conflict(new { error = "The value is back in range; acknowledge the alarm instead." });

                case ShelveOutcome.DurationNotAllowed:
                    return Results.BadRequest(new
                    {
                        error = $"A shelve lasts between one minute and {alarms.MaxShelveDuration.TotalMinutes:0} minutes.",
                    });
            }

            await audit.AppendAsync(
                Entry(caller, "alarm.shelve", alarm, ("durationMinutes", minutes)),
                CancellationToken.None);
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
        IAlarmEngine alarms)
    {
        var alarm = alarms.GetCurrent().FirstOrDefault(standing => standing.DefinitionId == definitionId);

        // Not found for an alarm in a Site the caller cannot see — the same answer as for
        // one that does not exist, so the refusal does not confirm it is there.
        if (alarm is null || !caller.Access.CanView(alarm.SiteId))
        {
            return (null, Results.NotFound());
        }

        return caller.Access.CanOperate(alarm.SiteId)
            ? (alarm, Results.Empty)
            : (null, ApiErrors.Forbidden($"{action} an alarm needs the Operator role on its Site."));
    }

    private static AlarmActor Actor(Caller caller) => new(caller.UserId, caller.Access.Username);

    /// <summary>
    /// The action was refused because it could not be recorded (ADR-0013). Unavailable
    /// rather than a server error: nothing is wrong with the request, and retrying once the
    /// database is back will work.
    /// </summary>
    private static IResult JournalUnavailable() => Results.Json(
        new { error = "The alarm journal is unavailable, so the action was not carried out. Try again shortly." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static AuditEntry Entry(
        Caller caller,
        string action,
        Alarm alarm,
        params (string Key, object? Value)[] extra) => new(
        caller.UserId,
        action,
        "alarm_definition",
        alarm.DefinitionId,
        Audit.Detail(
        [
            ("tagId", alarm.TagId),
            ("tagPath", alarm.TagPath),
            ("stateBefore", alarm.State.ToString()),
            .. extra,
        ]));

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
