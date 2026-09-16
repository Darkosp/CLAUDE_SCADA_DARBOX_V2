using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Security;

/// <summary>User and role management — Admin only (ADR-0011).</summary>
internal static class UserEndpoints
{
    internal static void MapUserApi(this WebApplication app)
    {
        var users = app.MapGroup("/api/users").RequireAuthorization(Policies.Admin);

        users.MapGet("", (UserDirectorySource directory) =>
            Results.Ok(directory.Current.Users
                .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .Select(AccessDto.From)));

        users.MapPost("", async (
            CreateUserRequest request,
            Caller caller,
            ISecurityStore store,
            TagCatalogSource catalogSource,
            AccessReloader reloader,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if ((Credentials.ProblemWithUsername(request.Username) ?? Credentials.ProblemWithPassword(request.Password)) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var user = new NewUser(
                Guid.NewGuid(),
                catalogSource.Current.Tenant.Id,
                request.Username!.Trim(),
                Authenticator.Hash(request.Password!),
                request.IsAdmin);

            try
            {
                await store.CreateUserAsync(user, cancellationToken);
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            await reloader.ReloadAsync(CancellationToken.None);
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "user.create", "user", user.Id,
                    Audit.Detail(("username", user.Username), ("isAdmin", user.IsAdmin))),
                CancellationToken.None);

            return Results.Created($"/api/users/{user.Id}", user.Id);
        });

        users.MapDelete("/{userId:guid}", async (
            Guid userId,
            Caller caller,
            ISecurityStore store,
            SessionManager sessions,
            AccessReloader reloader,
            IAuditLog audit,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!await store.DeactivateUserAsync(userId, clock.GetUtcNow(), cancellationToken))
                {
                    return Results.NotFound();
                }
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            // The rows were revoked in the same transaction as the deactivation; this makes
            // the cached copies agree, and the reload drops the user's live connections.
            sessions.ForgetUser(userId);
            await reloader.ReloadAsync(CancellationToken.None);

            await audit.AppendAsync(new AuditEntry(caller.UserId, "user.deactivate", "user", userId), CancellationToken.None);
            return Results.NoContent();
        });

        users.MapPut("/{userId:guid}/admin", async (
            Guid userId,
            SetAdminRequest request,
            Caller caller,
            ISecurityStore store,
            AccessReloader reloader,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!await store.SetAdminAsync(userId, request.IsAdmin, cancellationToken))
                {
                    return Results.NotFound();
                }
            }
            catch (ConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            await reloader.ReloadAsync(CancellationToken.None);
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "user.set_admin", "user", userId, Audit.Detail(("isAdmin", request.IsAdmin))),
                CancellationToken.None);

            return Results.NoContent();
        });

        users.MapPut("/{userId:guid}/password", async (
            Guid userId,
            SetPasswordRequest request,
            Caller caller,
            ISecurityStore store,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (Credentials.ProblemWithPassword(request.Password) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            if (!await store.SetPasswordHashAsync(userId, Authenticator.Hash(request.Password!), cancellationToken))
            {
                return Results.NotFound();
            }

            await audit.AppendAsync(new AuditEntry(caller.UserId, "user.set_password", "user", userId), CancellationToken.None);
            return Results.NoContent();
        });

        users.MapPut("/{userId:guid}/sites/{siteId:guid}", async (
            Guid userId,
            Guid siteId,
            SetSiteRoleRequest request,
            Caller caller,
            ISecurityStore store,
            TagCatalogSource catalogSource,
            AccessReloader reloader,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<SiteRole>(request.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                return Results.BadRequest(new
                {
                    error = string.Equals(request.Role, "Admin", StringComparison.OrdinalIgnoreCase)
                        ? "Admin is tenant-wide, not a role on one Site. Make the user an Admin instead."
                        : "A Site role is either Viewer or Operator.",
                });
            }

            if (catalogSource.Current.Sites.All(site => site.Id != siteId))
            {
                return Results.NotFound(new { error = "There is no such Site." });
            }

            if (!await store.SetSiteRoleAsync(userId, siteId, role, cancellationToken))
            {
                return Results.NotFound();
            }

            await reloader.ReloadAsync(CancellationToken.None);
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "user.set_site_role", "user", userId,
                    Audit.Detail(("siteId", siteId), ("role", role.ToString()))),
                CancellationToken.None);

            return Results.NoContent();
        });

        users.MapDelete("/{userId:guid}/sites/{siteId:guid}", async (
            Guid userId,
            Guid siteId,
            Caller caller,
            ISecurityStore store,
            AccessReloader reloader,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (!await store.RemoveSiteRoleAsync(userId, siteId, cancellationToken))
            {
                return Results.NotFound();
            }

            await reloader.ReloadAsync(CancellationToken.None);
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "user.remove_site_role", "user", userId, Audit.Detail(("siteId", siteId))),
                CancellationToken.None);

            return Results.NoContent();
        });
    }
}
