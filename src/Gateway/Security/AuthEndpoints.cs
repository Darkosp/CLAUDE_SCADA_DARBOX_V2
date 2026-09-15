using ScadaDarbox.Core.Security;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.RealTime;

namespace ScadaDarbox.Gateway.Security;

/// <summary>Logging in and out (ADR-0011).</summary>
internal static class AuthEndpoints
{
    private const int MaximumAuditedUsernameLength = 64;

    internal static void MapAuthApi(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            Authenticator authenticator,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var outcome = request.Username is null || request.Password is null
                ? null
                : await authenticator.LoginAsync(request.Username, request.Password, cancellationToken);

            if (outcome is null)
            {
                await audit.AppendAsync(
                    new AuditEntry(
                        ActorUserId: null,
                        "auth.login_failed",
                        Detail: Audit.Detail(("username", Truncate(request.Username)))),
                    CancellationToken.None);

                // One message for both cases, so the answer does not reveal which names exist.
                return Results.Json(new { error = "Wrong user name or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await audit.AppendAsync(
                new AuditEntry(outcome.Access.UserId, "auth.login", "session", outcome.Session.SessionId),
                CancellationToken.None);

            return Results.Ok(new LoginResponse(outcome.Session.Token, AccessDto.From(outcome.Access)));
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (
            Caller caller,
            SessionManager sessions,
            HubConnectionRegistry hubs,
            IAuditLog audit) =>
        {
            // Ended on the server, not just forgotten by the browser: the token stops
            // working everywhere at once, including on a hub connection it opened.
            await sessions.RevokeAsync(caller.SessionId, CancellationToken.None);
            await hubs.DisconnectSessionAsync(caller.SessionId);

            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "auth.logout", "session", caller.SessionId),
                CancellationToken.None);

            return Results.NoContent();
        });

        app.MapGet("/api/auth/me", (Caller caller) => Results.Ok(AccessDto.From(caller.Access)));
    }

    private static string? Truncate(string? username) =>
        username is { Length: > MaximumAuditedUsernameLength } ? username[..MaximumAuditedUsernameLength] : username;
}
