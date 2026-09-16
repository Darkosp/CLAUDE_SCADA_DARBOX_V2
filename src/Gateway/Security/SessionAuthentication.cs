using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Authenticates a request by its opaque session token (ADR-0011).
/// </summary>
/// <remarks>
/// The token resolves to a user and a session and nothing else. Roles are deliberately not
/// put into claims: they are read from the user directory at the moment they are needed,
/// so a role removed a second ago is already gone.
/// </remarks>
internal sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Session";

    private const string BearerPrefix = "Bearer ";

    private readonly SessionManager _sessions;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionManager sessions)
        : base(options, logger, encoder) =>
        _sessions = sessions;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The header works everywhere. The query-string form exists only because a browser
        // cannot set a header on a WebSocket handshake, and is only ever set aside for the
        // hub path (see HubQueryToken).
        var token = BearerToken() ?? Context.Features.Get<HubAccessTokenFeature>()?.Token;
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var session = await _sessions.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return AuthenticateResult.Fail("The session is unknown, revoked or expired.");
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
                new Claim(SessionClaims.SessionId, session.SessionId.ToString()),
            },
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }
}

/// <summary>The two facts a session token establishes about a request.</summary>
internal static class SessionClaims
{
    public const string SessionId = "scada:session_id";

    public static Guid? UserIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? SessionIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(SessionId), out var id) ? id : null;
}

/// <summary>
/// The user behind a request, with their access resolved at the moment of asking.
/// </summary>
internal sealed class Caller
{
    private static readonly IReadOnlyDictionary<Guid, SiteRole> NoRoles = new Dictionary<Guid, SiteRole>();

    private Caller(UserAccess access, Guid sessionId)
    {
        Access = access;
        SessionId = sessionId;
    }

    public UserAccess Access { get; }

    public Guid SessionId { get; }

    public Guid UserId => Access.UserId;

    /// <summary>Lets an endpoint take a <see cref="Caller"/> parameter directly.</summary>
    public static ValueTask<Caller?> BindAsync(HttpContext context) =>
        ValueTask.FromResult<Caller?>(
            From(context.User, context.RequestServices.GetRequiredService<UserDirectorySource>().Current));

    public static Caller From(ClaimsPrincipal? principal, UserDirectory directory)
    {
        var userId = SessionClaims.UserIdOf(principal);
        var access = userId is { } id ? directory.Find(id) : null;

        // No access rather than null: a user deactivated between authentication and this
        // line is filtered down to nothing and refused every action, instead of every
        // endpoint having to remember a null check.
        return new Caller(
            access ?? new UserAccess(userId ?? Guid.Empty, string.Empty, IsAdmin: false, NoRoles),
            SessionClaims.SessionIdOf(principal) ?? Guid.Empty);
    }
}

/// <summary>Authorization policies (ADR-0011).</summary>
internal static class Policies
{
    /// <summary>Configuration of any kind, templates included, and user management.</summary>
    public const string Admin = "Admin";

    public static void AddScadaPolicies(this AuthorizationOptions options)
    {
        // Every endpoint needs a valid session unless it explicitly says otherwise, so one
        // added later is authenticated from the moment it exists — the same reason
        // ADR-0011 states its Site rule generically rather than as a list.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        options.AddPolicy(Admin, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new AdminRequirement()));
    }
}

internal sealed class AdminRequirement : IAuthorizationRequirement
{
}

/// <summary>Grants <see cref="AdminRequirement"/> from the directory as it stands now, never from a claim.</summary>
internal sealed class AdminRequirementHandler(UserDirectorySource users) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (SessionClaims.UserIdOf(context.User) is { } userId && users.Current.Find(userId) is { IsAdmin: true })
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class ApiErrors
{
    /// <summary>A 403 that says which permission was missing, so the UI can show why.</summary>
    public static IResult Forbidden(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden);
}
