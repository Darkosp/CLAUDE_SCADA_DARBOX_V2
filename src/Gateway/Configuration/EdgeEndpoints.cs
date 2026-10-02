using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// The tenant's edges, the device carrying each one's link, and the devices each one reads
/// (ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// An edge hangs off the tenant rather than a Site: its name is the identity in its certificate
/// and the segment the broker carries its topics under, so it has to be unique across the whole
/// deployment (ADR-0015, ADR-0017). Nothing scopes it to a Site, and by the rule an unscoped tag
/// already follows that makes the whole resource Admin-only (ADR-0011) — there is no Site to hold
/// an answer against, so there is no not-found that would tell a lesser caller less than a refusal
/// already does.
/// </para>
/// <para>
/// Which devices an edge reads is not set here: that is an ordinary edit of the device, and
/// <c>EdgeId</c> on its save request is the only way it changes (ADR-0019 §2). It is shown here
/// because that assignment is the edge's membership, and nowhere else reports it.
/// </para>
/// </remarks>
internal static class EdgeEndpoints
{
    internal static void MapEdgeApi(this WebApplication app)
    {
        var edges = app.MapGroup("/api/edges").RequireAuthorization(Policies.Admin);

        edges.MapGet("", (TagCatalogSource catalogSource) =>
        {
            var catalog = catalogSource.Current;

            return Results.Ok(catalog.Edges
                .OrderBy(edge => edge.Name, StringComparer.OrdinalIgnoreCase)
                .Select(edge => new EdgeDto(
                    edge.Id,
                    edge.Name,
                    edge.LinkDeviceId,
                    catalog.Devices
                        .Where(device => device.EdgeId == edge.Id)
                        .Select(device => device.Id)
                        .ToArray(),
                    edge.DeclaredDriverKeys,
                    edge.DriversDeclaredAt,
                    UnreadableDevices(edge, catalog))));
        });

        edges.MapPost("", async (
            SaveEdgeRequest request,
            TagCatalogSource catalogSource,
            DriverShapes shapes,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var catalog = catalogSource.Current;

            if (ProblemWithLink(request.LinkDeviceId, catalog, shapes) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var edge = new Edge
            {
                Id = Guid.NewGuid(),
                // The tenant is the ownership scope (ADR-0004), not a field of the form.
                TenantId = catalog.Tenant.Id,
                Name = request.Name,
                LinkDeviceId = request.LinkDeviceId,
            };

            return await ConfigurationEndpoints.SaveAsync(
                () => repository.AddAsync(edge, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/edges/{edge.Id}", edge.Id));
        }).AdminWrite("edge.create", "edge");

        edges.MapPut("/{edgeId:guid}", async (
            Guid edgeId,
            SaveEdgeRequest request,
            TagCatalogSource catalogSource,
            DriverShapes shapes,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (ProblemWithLink(request.LinkDeviceId, catalogSource.Current, shapes) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var edge = new Edge
            {
                Id = edgeId,
                TenantId = catalogSource.Current.Tenant.Id,
                Name = request.Name,
                LinkDeviceId = request.LinkDeviceId,
            };

            return await ConfigurationEndpoints.SaveAsync(
                () => repository.UpdateAsync(edge, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        }).AdminWrite("edge.update", "edge", "edgeId");

        edges.MapDelete("/{edgeId:guid}", async (
            Guid edgeId,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await ConfigurationEndpoints.SaveAsync(
                () => repository.DeleteAsync(edgeId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent)).AdminWrite("edge.delete", "edge", "edgeId");
    }

    /// <summary>
    /// The devices this edge is assigned and is not reading, from both places that can say so
    /// (ADR-0019 §8, ADR-0021).
    /// </summary>
    /// <remarks>
    /// <para>
    /// **From the assignment**: a device assigned to this edge whose driver the edge's declaration
    /// does not list. §8's case — the cloud can work this out by itself, and a save is not what
    /// made the assignment.
    /// </para>
    /// <para>
    /// **From the edge**: a device the edge itself named as unreadable. This is the case §8 cannot
    /// see at all, because an edge redeployed without a driver re-examines nothing — no save
    /// happens, the device stays assigned, and until ADR-0021 nothing in the cloud knew.
    /// </para>
    /// <para>
    /// A device named by both appears once, marked as reported by the edge, which is the stronger
    /// of the two facts: the edge attempted it. A name that no longer resolves is still listed with
    /// a null id — it is what the edge said, and hiding it would hide the one case where an
    /// operator most needs to look.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<UnreadableDeviceDto> UnreadableDevices(Edge edge, TagCatalog catalog)
    {
        var assigned = catalog.Devices.Where(device => device.EdgeId == edge.Id).ToList();
        var unreadable = new List<UnreadableDeviceDto>();

        if (edge.DeclaredDriverKeys is { } declared)
        {
            unreadable.AddRange(assigned
                .Where(device => !declared.Contains(device.DriverKey, StringComparer.OrdinalIgnoreCase))
                .Select(device => new UnreadableDeviceDto(device.Id, device.Name, device.DriverKey, ReportedByEdge: false)));
        }

        foreach (var reported in edge.UnreadableDevices ?? [])
        {
            var match = assigned.FirstOrDefault(device =>
                string.Equals(device.Name, reported.Device, StringComparison.OrdinalIgnoreCase));

            // The edge's own report replaces the cloud's inference for the same device: it is the
            // stronger statement, and a device the edge named must not be shown as a deduction.
            unreadable.RemoveAll(device => match is not null && device.DeviceId == match.Id);

            unreadable.Add(new UnreadableDeviceDto(
                match?.Id,
                match?.Name ?? reported.Device,
                reported.Driver,
                ReportedByEdge: true));
        }

        return unreadable;
    }

    /// <summary>
    /// Why this device cannot carry an edge's link, or null when it can (ADR-0016, ADR-0019).
    /// </summary>
    /// <remarks>
    /// A link is the Gateway's end of the connection the edge already holds, so it has to be a
    /// device that pushes; naming a polled one would leave every device assigned to that edge with
    /// nothing reading it. The device also has to be configured at all — the catalogue holds live
    /// devices only, so an id that has been deleted is refused here rather than passing the
    /// foreign key and leaving the edge naming a device that is gone (ADR-0009).
    /// </remarks>
    private static string? ProblemWithLink(Guid? linkDeviceId, TagCatalog catalog, DriverShapes shapes)
    {
        if (linkDeviceId is not { } deviceId)
        {
            return null;
        }

        if (catalog.FindDevice(deviceId) is not { } device)
        {
            return "There is no such device to carry this edge's link.";
        }

        return shapes.Pushes(device.DriverKey)
            ? null
            : $"'{device.DriverKey}' is polled, not pushing, so a device using it cannot carry an edge's link.";
    }

    /// <summary>
    /// Why this driver key cannot be used for a device assigned to this edge, or null when it can
    /// (ADR-0019 §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The edge's own declaration is the only list that can answer this. Which driver keys exist is
    /// a fact about the build running at the plant, and the Gateway's list is a different one: it
    /// registers a driver no edge reads with, and an edge may one day register one the Gateway does
    /// not. Refusing here is what stops a device being derived, published, and refused only by the
    /// edge — loud at the plant, silent in the cloud.
    /// </para>
    /// <para>
    /// An edge that has declared nothing yet is <b>not</b> refused. It may simply never have
    /// started, and a plant's devices are ordinarily configured before its edge is, so refusing
    /// would make the natural order impossible. The difference is reported instead: an edge that
    /// has declared nothing is shown as having declared nothing, never as having the cloud's list.
    /// </para>
    /// </remarks>
    internal static string? ProblemWithEdgeDrivers(Guid? edgeId, string driverKey, TagCatalog catalog)
    {
        if (edgeId is not { } id)
        {
            return null;
        }

        if (catalog.Edges.FirstOrDefault(edge => edge.Id == id) is not { } edge)
        {
            return "There is no such edge to read this device.";
        }

        if (edge.DeclaredDriverKeys is not { } declared)
        {
            return null;
        }

        return declared.Contains(driverKey, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"Edge '{edge.Name}' has declared it cannot read '{driverKey}'; the drivers it has declared are {Describe(declared)}.";
    }

    private static string Describe(IReadOnlyList<string> declared) =>
        declared.Count == 0 ? "none at all" : string.Join(", ", declared.Select(key => $"'{key}'"));
}
