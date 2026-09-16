using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.RealTime;

/// <summary>
/// Keeps every open hub connection in exactly the Site groups its user may currently see
/// (ADR-0011).
/// </summary>
/// <remarks>
/// A connection is authenticated once, when it opens. After that this registry is what
/// keeps it honest: a role removed, a user deactivated or a session ended changes the
/// groups of a connection that is already open, or drops it, rather than waiting for the
/// browser to reconnect. A live connection that went on receiving a Site's values after
/// access was revoked would be the same defect as an unfiltered endpoint, arriving through
/// a different door.
/// </remarks>
public sealed class HubConnectionRegistry
{
    private readonly IHubContext<TagHub> _hub;
    private readonly UserDirectorySource _users;
    private readonly TagCatalogSource _catalog;
    private readonly SessionManager _sessions;
    private readonly ILogger<HubConnectionRegistry> _logger;

    private readonly ConcurrentDictionary<string, TrackedConnection> _connections = new(StringComparer.Ordinal);

    // Serialises every change to group membership, so two reconciliations can never
    // interleave their joins and leaves for the same connection.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HubConnectionRegistry(
        IHubContext<TagHub> hub,
        UserDirectorySource users,
        TagCatalogSource catalog,
        SessionManager sessions,
        ILogger<HubConnectionRegistry> logger)
    {
        _hub = hub;
        _users = users;
        _catalog = catalog;
        _sessions = sessions;
        _logger = logger;

        // A new Site changes which groups an Admin should be in.
        _catalog.Changed += OnCatalogChanged;
    }

    public static string GroupOf(Guid siteId) => $"site:{siteId:N}";

    internal async Task RegisterAsync(HubCallerContext context, Guid userId, Guid sessionId)
    {
        var connection = new TrackedConnection(context, userId, sessionId);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _connections[connection.ConnectionId] = connection;
            await ReconcileAsync(connection, _users.Current, _catalog.Current).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a closed connection. SignalR removes it from its groups itself.</summary>
    internal void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>
    /// Brings every open connection's groups in line with the directory as it now stands.
    /// </summary>
    /// <remarks>Completes only once every affected connection has left the groups it lost.</remarks>
    public async Task ApplyAccessAsync()
    {
        var changed = new List<string>();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var users = _users.Current;
            var catalog = _catalog.Current;

            foreach (var connection in _connections.Values.ToList())
            {
                if (await ReconcileAsync(connection, users, catalog).ConfigureAwait(false))
                {
                    changed.Add(connection.ConnectionId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed.Count > 0)
        {
            // Sent outside the gate: a slow client applying back-pressure must not hold up
            // every other connection's permission change. The client answers by reloading
            // its current values, so it also forgets what it may no longer see.
            await _hub.Clients.Clients(changed).SendAsync(TagHub.AccessChangedMethod).ConfigureAwait(false);
        }
    }

    /// <summary>Drops every connection opened with <paramref name="sessionId"/> — a logout.</summary>
    public async Task DisconnectSessionAsync(Guid sessionId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var connection in _connections.Values.Where(c => c.SessionId == sessionId).ToList())
            {
                await DropAsync(connection).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Counts each open connection as use of its session, and drops those whose session
    /// has ended — most importantly one past its absolute lifetime, however busy.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var connection in _connections.Values.ToList())
            {
                // An open connection is use of its session (ADR-0011): an operator watching a
                // screen through a shift is never logged out for being idle.
                if (!await _sessions.TouchAsync(connection.SessionId, cancellationToken).ConfigureAwait(false))
                {
                    await DropAsync(connection).ConfigureAwait(false);
                }
            }

            _sessions.PruneExpired();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>Whether the connection's groups changed and it is still open.</returns>
    private async Task<bool> ReconcileAsync(TrackedConnection connection, UserDirectory users, TagCatalog catalog)
    {
        if (users.Find(connection.UserId) is not { } access)
        {
            await DropAsync(connection).ConfigureAwait(false);
            return false;
        }

        var desired = catalog.Sites
            .Where(site => access.CanView(site.Id))
            .Select(site => GroupOf(site.Id))
            .ToHashSet(StringComparer.Ordinal);

        var leaving = connection.Groups.Where(group => !desired.Contains(group)).ToList();
        var joining = desired.Where(group => !connection.Groups.Contains(group)).ToList();

        // Leave before joining, so the connection is never briefly in both the old and the
        // new set of Sites.
        foreach (var group in leaving)
        {
            await _hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, group).ConfigureAwait(false);
            connection.Groups.Remove(group);
        }

        foreach (var group in joining)
        {
            await _hub.Groups.AddToGroupAsync(connection.ConnectionId, group).ConfigureAwait(false);
            connection.Groups.Add(group);
        }

        return leaving.Count > 0 || joining.Count > 0;
    }

    private async Task DropAsync(TrackedConnection connection)
    {
        // Out of every group first. Aborting only starts the disconnect, and a broadcast in
        // the meantime must not still reach it.
        foreach (var group in connection.Groups.ToList())
        {
            await _hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, group).ConfigureAwait(false);
        }

        connection.Groups.Clear();
        _connections.TryRemove(connection.ConnectionId, out _);
        connection.Context.Abort();
    }

    private void OnCatalogChanged(object? sender, TagCatalog catalog) => _ = ApplyAccessAfterCatalogChangeAsync();

    private async Task ApplyAccessAfterCatalogChangeAsync()
    {
        try
        {
            await ApplyAccessAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Updating hub groups after a configuration change failed.");
        }
    }

    private sealed class TrackedConnection(HubCallerContext context, Guid userId, Guid sessionId)
    {
        public HubCallerContext Context { get; } = context;

        public string ConnectionId => Context.ConnectionId;

        public Guid UserId { get; } = userId;

        public Guid SessionId { get; } = sessionId;

        /// <summary>Touched only under the registry's gate.</summary>
        public HashSet<string> Groups { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>How often open hub connections are checked against their sessions.</summary>
public sealed record HubSweepSettings(TimeSpan Interval);

/// <summary>Runs <see cref="HubConnectionRegistry.SweepAsync"/> on a fixed interval.</summary>
internal sealed class HubSessionSweeper : BackgroundService
{
    private readonly HubConnectionRegistry _registry;
    private readonly HubSweepSettings _settings;
    private readonly ILogger<HubSessionSweeper> _logger;

    public HubSessionSweeper(HubConnectionRegistry registry, HubSweepSettings settings, ILogger<HubSessionSweeper> logger)
    {
        _registry = registry;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_settings.Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _registry.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Checking open hub connections against their sessions failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
