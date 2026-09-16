using ScadaDarbox.Core.Security;
using ScadaDarbox.Gateway.RealTime;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Rebuilds the user directory after a user or role change and makes it take effect
/// everywhere — including on hub connections that are already open (ADR-0011).
/// </summary>
public sealed class AccessReloader
{
    private readonly ISecurityStore _store;
    private readonly UserDirectorySource _source;
    private readonly HubConnectionRegistry _hubs;

    public AccessReloader(ISecurityStore store, UserDirectorySource source, HubConnectionRegistry hubs)
    {
        _store = store;
        _source = source;
        _hubs = hubs;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        _source.Set(await BuildAsync(_store, cancellationToken).ConfigureAwait(false));

        // Awaited rather than fired off: by the time an Admin is told a role was removed,
        // that user's live connections have already left the Site's group. Otherwise a
        // value could still arrive after the Admin had been told access was gone. Not
        // cancellable either — a change that is already stored must not be half-applied
        // because the Admin's browser went away.
        await _hubs.ApplyAccessAsync().ConfigureAwait(false);
    }

    public static async Task<UserDirectory> BuildAsync(ISecurityStore store, CancellationToken cancellationToken) =>
        new(await store.GetActiveUsersAsync(cancellationToken).ConfigureAwait(false));
}
