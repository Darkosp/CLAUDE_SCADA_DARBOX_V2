using ScadaDarbox.Core.Security;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Configuration writes: Admin only, and each one that succeeds is recorded (ADR-0011).
/// </summary>
internal static class AuditedEndpoints
{
    /// <summary>
    /// Restricts an endpoint to Admin and appends an audit entry for every call that
    /// succeeds.
    /// </summary>
    /// <remarks>
    /// One filter rather than a line in every handler, so a configuration endpoint cannot
    /// be restricted and then forget to be audited, or the reverse.
    /// </remarks>
    /// <param name="idRouteValue">The route value naming the entity; a created entity's id is taken from the response instead.</param>
    public static RouteHandlerBuilder AdminWrite(
        this RouteHandlerBuilder builder,
        string action,
        string entityType,
        string? idRouteValue = null) =>
        builder
            .RequireAuthorization(Policies.Admin)
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context).ConfigureAwait(false);

                if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                {
                    var http = context.HttpContext;
                    var detail = new Dictionary<string, object?>();

                    foreach (var (key, value) in http.Request.RouteValues)
                    {
                        detail[key] = value;
                    }

                    foreach (var argument in context.Arguments.Where(IsRequestBody))
                    {
                        detail["request"] = argument;
                    }

                    if (result is IValueHttpResult { Value: { } returned })
                    {
                        detail["result"] = returned;
                    }

                    Guid? entityId = idRouteValue is not null
                                     && Guid.TryParse(http.Request.RouteValues[idRouteValue]?.ToString(), out var routeId)
                        ? routeId
                        : result is IValueHttpResult { Value: Guid createdId } ? createdId : null;

                    // Not tied to the request: a change that has already been made is
                    // recorded even if the browser that asked for it has gone away.
                    await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(
                        new AuditEntry(SessionClaims.UserIdOf(http.User), action, entityType, entityId, detail),
                        CancellationToken.None).ConfigureAwait(false);
                }

                return result;
            });

    private static bool IsRequestBody(object? argument) =>
        argument?.GetType().Namespace == typeof(SaveDeviceRequest).Namespace;
}

internal static class Audit
{
    public static Dictionary<string, object?> Detail(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
}
