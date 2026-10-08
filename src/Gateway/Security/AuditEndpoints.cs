using ScadaDarbox.Core.Security;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// The audit trail, read back (ADR-0032).
/// </summary>
/// <remarks>
/// **Admin only, and by the same policy as user management rather than a second mechanism.** An audit row
/// carries no Site (ADR-0011) — a failed sign-in may name no account, and a Gateway-wide event belongs to no
/// Site — so there is no Site filter over this table that could be honest, and a trail with holes in it is
/// worse than no trail because it looks complete.
/// </remarks>
internal static class AuditEndpoints
{
    internal static void MapAuditApi(this WebApplication app)
    {
        app.MapGet("/api/audit", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? action,
            Guid? actor,
            string? entityType,
            Guid? entityId,
            long? before,
            int? limit,
            IAuditTrail trail) =>
        {
            // The cap is the journal's, to the number (ADR-0032 §8): two screens asking the same server for
            // "the newest N rows" should not disagree about what N may be.
            var page = await trail.ReadAsync(
                new AuditTrailQuery(
                    from,
                    to,
                    string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
                    actor,
                    string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim(),
                    entityId,
                    before,
                    Math.Clamp(limit ?? 200, 1, 1000)),
                CancellationToken.None);

            return Results.Ok(new AuditPageDto(page.Entries.Select(AuditEntryDto.From).ToList(), page.Total));
        }).RequireAuthorization(Policies.Admin);
    }
}
