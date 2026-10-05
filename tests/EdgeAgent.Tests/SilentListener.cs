using System.Net;
using System.Net.Sockets;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// A TCP listener that accepts connections and then never answers (ADR-0023).
/// </summary>
/// <remarks>
/// The one failure a real Modbus slave cannot be asked to produce. Phase 7's walk found this shape
/// in the read path — a device that accepts the connection and then stops answering, holding the
/// scan loop while the tags already read kept their values — and the write path has its own bound
/// for it. Accepting and then going quiet is the only way to make that bound fire, and a closed port
/// is a different thing entirely: it fails immediately with a refusal, which is the case
/// <c>A_device_that_is_not_listening</c> already covers.
/// </remarks>
internal sealed class SilentListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<TcpClient> _accepted = [];
    private readonly Task _accepting;

    private SilentListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = AcceptAndHoldAsync(_shutdown.Token);
    }

    internal int Port { get; }

    internal static SilentListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new SilentListener(listener);
    }

    /// <summary>
    /// Accepts and holds every connection, reading nothing and writing nothing. The socket stays
    /// open, which is what makes the caller wait rather than fail.
    /// </summary>
    private async Task AcceptAndHoldAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            lock (_accepted)
            {
                _accepted.Add(client);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        lock (_accepted)
        {
            foreach (var client in _accepted)
            {
                client.Dispose();
            }

            _accepted.Clear();
        }

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // The accept loop faults when its listener is torn down; that is the shutdown.
        }

        _shutdown.Dispose();
    }
}
