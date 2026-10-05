using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Operator screens (ADR-0024): the rows an operator looks at, over the API.
/// </summary>
/// <remarks>
/// <para>
/// A screen is configuration and not code, so this is the whole of how one is created — there is no
/// upload, no file and no scripting. What the API refuses, it refuses by name and before storing
/// anything, because a screen saved with a component nobody can draw is a screen that is silently
/// missing something (ADR-0024 §3).
/// </para>
/// <para>
/// <b>A binding is checked against what the caller may see, not only against what exists.</b> A
/// component naming a tag on another Site would be a screen that reads across the boundary ADR-0011
/// draws, and the composite key in migration 0017 makes such a row unwritable — this layer is what
/// turns that refusal into a sentence rather than a constraint violation.
/// </para>
/// <para>
/// <b>These saves do not reload the tag catalogue</b>, and that is deliberate rather than an
/// omission. A screen is not part of what tags exist, so reloading would republish a catalogue
/// nothing about it changed. A future component kind that reads the tree would need it, and that is
/// the moment to add it rather than now.
/// </para>
/// </remarks>
internal static class ScreenEndpoints
{
    internal static void MapScreenApi(this WebApplication app)
    {
        app.MapGet("/api/sites/{siteId:guid}/screens", async (
            Guid siteId,
            IScreenRepository screens,
            TagCatalogSource catalogSource,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            // Not found rather than forbidden, so the answer does not confirm the Site exists
            // (ADR-0011). The same rule as every other Site-scoped path.
            if (!caller.Access.CanView(siteId))
            {
                return Results.NotFound();
            }

            var catalog = catalogSource.Current;
            var live = await screens.GetBySiteAsync(siteId, cancellationToken).ConfigureAwait(false);

            return Results.Ok(live.Select(screen => ToDto(screen, catalog, caller)));
        });

        app.MapGet("/api/screens/{screenId:guid}", async (
            Guid screenId,
            IScreenRepository screens,
            TagCatalogSource catalogSource,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            var screen = await screens.FindAsync(screenId, cancellationToken).ConfigureAwait(false);

            if (screen is null || !caller.Access.CanView(screen.SiteId))
            {
                return Results.NotFound();
            }

            return Results.Ok(ToDto(screen, catalogSource.Current, caller));
        });

        app.MapPost("/api/sites/{siteId:guid}/screens", async (
            Guid siteId,
            SaveScreenRequest request,
            IScreenRepository screens,
            TagCatalogSource catalogSource,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            if (!caller.Access.CanView(siteId))
            {
                return Results.NotFound();
            }

            if (!caller.Access.CanOperate(siteId))
            {
                return ApiErrors.Forbidden("Editing screens needs the Operator role on their Site.");
            }

            var catalog = catalogSource.Current;
            if (ToDomain(Guid.NewGuid(), siteId, request, catalog, caller) is not { } screen)
            {
                return Results.BadRequest(new { error = Problem });
            }

            return await SaveAsync(
                () => screens.AddAsync(screen, cancellationToken),
                () => Results.Created($"/api/screens/{screen.Id}", screen.Id)).ConfigureAwait(false);
        });

        app.MapPut("/api/screens/{screenId:guid}", async (
            Guid screenId,
            SaveScreenRequest request,
            IScreenRepository screens,
            TagCatalogSource catalogSource,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            var existing = await screens.FindAsync(screenId, cancellationToken).ConfigureAwait(false);

            if (existing is null || !caller.Access.CanView(existing.SiteId))
            {
                return Results.NotFound();
            }

            if (!caller.Access.CanOperate(existing.SiteId))
            {
                return ApiErrors.Forbidden("Editing screens needs the Operator role on their Site.");
            }

            // The Site is the screen's, never the request's: a screen cannot be moved between
            // Sites, and the composite keys would refuse one that tried.
            var catalog = catalogSource.Current;
            if (ToDomain(screenId, existing.SiteId, request, catalog, caller) is not { } screen)
            {
                return Results.BadRequest(new { error = Problem });
            }

            return await SaveAsync(
                () => screens.UpdateAsync(screen, cancellationToken),
                () => Results.NoContent()).ConfigureAwait(false);
        });

        app.MapDelete("/api/screens/{screenId:guid}", async (
            Guid screenId,
            IScreenRepository screens,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            var existing = await screens.FindAsync(screenId, cancellationToken).ConfigureAwait(false);

            if (existing is null || !caller.Access.CanView(existing.SiteId))
            {
                return Results.NotFound();
            }

            if (!caller.Access.CanOperate(existing.SiteId))
            {
                return ApiErrors.Forbidden("Editing screens needs the Operator role on their Site.");
            }

            // Deleting the last screen is allowed: that leaves an empty Site, which is what a Site
            // with no screens looks like. Seeding is what a new Site gets, not a rule that the last
            // screen may not go.
            await screens.DeleteAsync(screenId, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    /// <summary>Why the last <see cref="ToDomain(Guid, Guid, SaveScreenRequest, TagCatalog, Caller)"/>
    /// returned null. Only ever read on the line after a null, so it is not shared state between
    /// requests in any way that can matter — and the tests that pin the refusals read the API's own
    /// answer, not this.</summary>
    private static string Problem { get; set; } = string.Empty;

    /// <summary>What the caller can see of one screen, with each component resolved for them.</summary>
    private static ScreenDto ToDto(Screen screen, TagCatalog catalog, Caller caller) =>
        new(
            screen.Id,
            screen.SiteId,
            screen.Name,
            screen.Position,
            screen.Components
                .Select(component => new ScreenComponentDto(
                    component.Id,
                    component.RowIndex,
                    component.ColumnSpan,
                    component.Position,
                    component.Kind,
                    component.Title,
                    component.TagId,
                    // ADR-0024 §5: a binding the reader may not see is still rendered, and says so.
                    // Deciding it here means every renderer gets the same answer, and there is one
                    // place to check rather than one per component.
                    Readable: component.TagId is null || IsReadable(component.TagId.Value, catalog, caller),
                    // ADR-0026 §2: the server decides whether the write control is offered, for the
                    // same reason it decides readability. False for a reader who cannot operate the
                    // Site even when the tag is writable, because "you may not" and "this cannot be"
                    // are different sentences — and the first two answers are separate, so a client
                    // cannot make one out of the other.
                    Writable: IsWritable(component.TagId, catalog, caller)))
                .ToList());

    private static bool IsReadable(Guid tagId, TagCatalog catalog, Caller caller) =>
        catalog.FindTag(tagId) is { } tag
        && catalog.FindDevice(tag.DeviceId) is { } device
        && caller.Access.CanView(device.SiteId);

    /// <summary>
    /// Whether this component's tag is one the caller could write — which is only ever a marking,
    /// because writing from a screen is not built (ADR-0024 §9).
    /// </summary>
    /// <remarks>
    /// A tag that is not readable is not writable either, and the order is deliberate: the first
    /// question about a binding the reader cannot see is not what they may do with it.
    /// </remarks>
    private static bool IsWritable(Guid? tagId, TagCatalog catalog, Caller caller)
    {
        if (tagId is not { } id || !IsReadable(id, catalog, caller))
        {
            return false;
        }

        return catalog.FindTag(id) is { IsWritable: true } tag
            && catalog.FindDevice(tag.DeviceId) is { } device
            && caller.Access.CanOperate(device.SiteId);
    }

    /// <summary>
    /// The screen a request describes, or null with <see cref="Problem"/> saying why.
    /// </summary>
    private static Screen? ToDomain(
        Guid id,
        Guid siteId,
        SaveScreenRequest request,
        TagCatalog catalog,
        Caller caller)
    {
        Problem = string.Empty;

        var screen = new Screen
        {
            Id = id,
            TenantId = catalog.Tenant.Id,
            SiteId = siteId,
            Name = request.Name,
            Position = request.Position,
            Components = request.Components
                .Select(component => new ScreenComponent
                {
                    // A component the client is resending keeps its id, so a future audit entry
                    // about editing it still points at the same thing; a new one gets a new id.
                    Id = component.Id ?? Guid.NewGuid(),
                    ScreenId = id,
                    RowIndex = component.RowIndex,
                    ColumnSpan = component.ColumnSpan,
                    Position = component.Position,
                    Kind = component.Kind,
                    Title = component.Title,
                    TagId = component.TagId,
                })
                .ToList(),
        };

        if (ScreenRules.ProblemWith(screen) is { } rule)
        {
            Problem = rule;
            return null;
        }

        // Every bound tag has to be one this caller may see, and that is a different question from
        // whether the kind needs a tag at all — which ScreenRules has just answered. A tag on
        // another Site, or one that no longer exists, is refused here by name rather than left for
        // the composite key to reject as a constraint violation.
        foreach (var component in screen.Components.Where(component => component.TagId is { }))
        {
            var tag = catalog.FindTag(component.TagId!.Value);

            if (tag is null)
            {
                Problem = $"Component of kind '{component.Kind}' names tag {component.TagId}, which does not exist.";
                return null;
            }

            if (!IsReadable(component.TagId.Value, catalog, caller))
            {
                Problem = $"Component of kind '{component.Kind}' names a tag on a Site this session cannot see.";
                return null;
            }

            // Derived from the tag, never taken from the request: a caller does not get to name a
            // device, because the device is what pins the tag to this Site in the database (0017).
            component.DeviceId = tag.DeviceId;
        }

        return screen;
    }

    private static async Task<IResult> SaveAsync(Func<Task> write, Func<IResult> success)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (ConfigurationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (PostgresException exception) when (exception.SqlState == "23503")
        {
            // The composite keys are the enforcement point for cross-Site placement, so this is a
            // request the database refused rather than a server fault.
            return Results.BadRequest(new
            {
                error = "The screen, or a tag it names, does not belong to the Site the screen is on.",
            });
        }

        return success();
    }
}
