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
            var attempt = request.Username is null || request.Password is null
                ? LoginAttempt.Wrong
                : await authenticator.LoginAsync(request.Username, request.Password, cancellationToken);

            if (attempt.JustLocked)
            {
                // The transition only, with the moment it reopens, so the trail says when an account was
                // shut rather than repeating it for every attempt made while it is (ADR-0031 §9).
                await audit.AppendAsync(
                    new AuditEntry(
                        ActorUserId: null,
                        "auth.account_locked",
                        Detail: Audit.Detail(
                            ("username", Truncate(request.Username)),
                            ("until", attempt.ShutUntilUtc?.ToString("O")))),
                    CancellationToken.None);
            }

            if (attempt.ShutUntilUtc is { } until)
            {
                await audit.AppendAsync(
                    new AuditEntry(
                        ActorUserId: null,
                        "auth.login_failed",
                        Detail: Audit.Detail(("username", Truncate(request.Username)))),
                    CancellationToken.None);

                // **The account is told it is shut, and that is deliberate** (ADR-0031 §4): an operator at
                // three in the morning who is told "wrong user name or password" goes looking for a password
                // problem they do not have. The time is sent as an unambiguous instant with its zone rather
                // than as a wall clock, because the server does not know what the reader's clock says.
                return Results.Json(
                    new
                    {
                        error = $"Too many failed sign-ins. This account is locked until {until:O} "
                                + "(UTC); an Admin can reset the password to reopen it.",
                    },
                    statusCode: StatusCodes.Status423Locked);
            }

            if (attempt.Outcome is null)
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
                new AuditEntry(attempt.Outcome.Access.UserId, "auth.login", "session", attempt.Outcome.Session.SessionId),
                CancellationToken.None);

            return Results.Ok(new LoginResponse(attempt.Outcome.Session.Token, AccessDto.From(attempt.Outcome.Access)));
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
