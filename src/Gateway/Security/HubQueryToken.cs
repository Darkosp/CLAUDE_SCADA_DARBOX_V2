using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Takes a session token out of the request's query string before anything can log it
/// (ADR-0011).
/// </summary>
/// <remarks>
/// A browser cannot put a header on a WebSocket handshake, so the SignalR client sends its
/// token as <c>access_token</c> in the URL — and a URL is exactly what request logging
/// writes down. Turning one log category down would hold only until someone raised a log
/// level while chasing a fault. Instead the token is lifted out of the request before the
/// host builds its HttpContext, which is also before the host writes its "request starting"
/// line: nothing downstream, logging included, ever sees a URL that still contains it.
///
/// The token is stripped on every path but honoured only on the hub path. Anywhere else a
/// token in the URL authenticates nothing — and is still kept out of the logs.
/// </remarks>
internal static class HubQueryToken
{
    public const string HubPath = "/hubs/tags";
    public const string ParameterName = "access_token";

    /// <summary>
    /// Wraps the registered HTTP server so every request passes through <see cref="Lift"/>
    /// first.
    /// </summary>
    public static void ScrubQueryTokens(this IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(service => service.ServiceType == typeof(IServer))
            ?? throw new InvalidOperationException("No HTTP server is registered, so there is nothing to protect.");
        var implementation = descriptor.ImplementationType
            ?? throw new InvalidOperationException("The HTTP server is not registered as a plain type and cannot be wrapped.");

        services.Remove(descriptor);
        services.AddSingleton(implementation);
        services.AddSingleton<IServer>(provider =>
            new ScrubbingServer((IServer)provider.GetRequiredService(implementation)));
    }

    /// <summary>
    /// Removes any access token from the request target, keeping it aside when the request
    /// is for the hub.
    /// </summary>
    internal static void Lift(IFeatureCollection features)
    {
        // A server may reuse a feature collection for the next request on a kept-alive
        // connection, so a token from an earlier request must never survive into a later one.
        features.Set<HubAccessTokenFeature>(null);

        var request = features.Get<IHttpRequestFeature>();
        if (request is null || request.QueryString.Length == 0)
        {
            return;
        }

        string? token = null;
        var kept = new List<string>();

        foreach (var pair in request.QueryString.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);

            if (string.Equals(name, ParameterName, StringComparison.OrdinalIgnoreCase))
            {
                token ??= separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
                continue;
            }

            kept.Add(pair);
        }

        if (token is null)
        {
            return;
        }

        request.QueryString = kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);

        var queryStart = request.RawTarget.IndexOf('?');
        request.RawTarget = (queryStart < 0 ? request.RawTarget : request.RawTarget[..queryStart]) + request.QueryString;

        if (token.Length > 0 && IsHubPath(request.Path))
        {
            features.Set(new HubAccessTokenFeature(token));
        }
    }

    private static bool IsHubPath(string path) =>
        path.Equals(HubPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(HubPath + "/", StringComparison.OrdinalIgnoreCase);

    private sealed class ScrubbingServer(IServer inner) : IServer
    {
        public IFeatureCollection Features => inner.Features;

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull =>
            inner.StartAsync(new ScrubbingApplication<TContext>(application), cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private sealed class ScrubbingApplication<TContext>(IHttpApplication<TContext> inner) : IHttpApplication<TContext>
        where TContext : notnull
    {
        public TContext CreateContext(IFeatureCollection contextFeatures)
        {
            Lift(contextFeatures);
            return inner.CreateContext(contextFeatures);
        }

        public Task ProcessRequestAsync(TContext context) => inner.ProcessRequestAsync(context);

        public void DisposeContext(TContext context, Exception? exception) => inner.DisposeContext(context, exception);
    }
}

/// <summary>A session token that arrived in a hub request's query string, set aside.</summary>
internal sealed class HubAccessTokenFeature(string token)
{
    public string Token { get; } = token;
}
