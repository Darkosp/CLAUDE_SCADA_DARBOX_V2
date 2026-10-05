using System.Net;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Server;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// An in-process MQTT broker on a loopback port for the life of one test (ADR-0017).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a deliberate mirror of <c>Gateway.Tests/Hosting/TestBroker.cs</c></b>, and the two are
/// kept in step by hand. The alternative was a shared test-support project, and that was not worth
/// its cost for one small type that both copies own in full: the bug it works around is in MQTTnet's
/// API and not in either assembly, so neither copy is derived from the other's subject matter.
/// </para>
/// <para>
/// <b>Why it exists at all.</b> The shape it replaces — in several classes in both test projects —
/// was to pick a free port and then bind it later:
/// </para>
/// <code>
/// using var probe = new TcpListener(IPAddress.Loopback, 0);
/// probe.Start();
/// return ((IPEndPoint)probe.LocalEndpoint).Port;   // and the listener is disposed here
/// </code>
/// <para>
/// The number is free when it is read and free again the moment the listener is disposed, so another
/// test class — in this assembly or in the other one, since xUnit runs them at the same time — can
/// be handed the same port before the first has bound it. Measured on 2026-10-05 rather than guessed:
/// the second broker throws <c>SocketException: Only one usage of each socket address … is normally
/// permitted</c>, while a <b>client still connects</b> — to whichever broker won. A publisher and a
/// subscriber meant to meet on that port then talk to two different brokers, and the test fails
/// twenty seconds later with a timeout that names neither the port nor the race.
/// </para>
/// <para>
/// The fix is to <b>bind and retry</b>, so choosing a port and claiming it are the same attempt and
/// there is no window between them.
/// </para>
/// </remarks>
internal sealed class TestBroker : IAsyncDisposable
{
    /// <summary>How many ports to try before giving up on the machine.</summary>
    private const int Attempts = 20;

    private readonly MqttServer _server;

    private TestBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    /// <summary>The port this broker actually holds, which is the only one worth telling a client.</summary>
    internal int Port { get; }

    /// <summary>
    /// Starts a broker on a port nobody else holds.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No port could be claimed in <see cref="Attempts"/> tries, which means something other than
    /// this suite is holding the machine's ports.
    /// </exception>
    internal static async Task<TestBroker> StartAsync()
    {
        var factory = new MqttServerFactory();

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var port = FreePort();

            var server = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port)
                .Build());

            try
            {
                await server.StartAsync().ConfigureAwait(false);
                return new TestBroker(server, port);
            }
            catch (Exception exception) when (IsPortTaken(exception))
            {
                // Lost the race that this type exists to lose safely. The number is not ours; take
                // another. Nothing is left running: a server that failed to bind never became a
                // listener.
                server.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not claim a loopback port in {Attempts} attempts. Something other than this suite is using them.");
    }

    /// <summary>
    /// A port to configure a client with, before a raw server has been started on it.
    /// </summary>
    /// <remarks>
    /// <b>This is the racy half, and it is deliberately small.</b> A test that must build its own
    /// <see cref="MqttServer"/> — because it intercepts publishes, or watches the connection — needs
    /// to know the port before the server exists, and there is no way around that with MQTTnet: it
    /// will not bind zero and tell you what it chose. What this does is start a real
    /// <see cref="TestBroker"/> to reserve a number, stop it, and hand the number over — after
    /// which the caller should use <see cref="StartRawAsync"/> rather than binding by hand, because
    /// that retries if the number was taken in the gap.
    /// </remarks>
    internal static async Task<int> ReservePortAsync()
    {
        var reserved = await StartAsync().ConfigureAwait(false);
        var port = reserved.Port;
        await reserved.DisposeAsync().ConfigureAwait(false);
        return port;
    }

    /// <summary>
    /// Starts a server the caller built, retrying on a different port when the bind is refused.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="ReservePortAsync"/>: between reserving a number and binding it,
    /// another test can take it, and this is what makes that survivable rather than a mystery
    /// failure in a test about something else. The caller's factory is given the port each time,
    /// because the port is the only thing that changes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No port could be claimed in <see cref="Attempts"/> tries.
    /// </exception>
    internal static async Task<TestBroker> StartRawAsync(Func<int, MqttServer> build)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var port = await ReservePortAsync().ConfigureAwait(false);
            var server = build(port);

            try
            {
                await server.StartAsync().ConfigureAwait(false);
                return new TestBroker(server, port);
            }
            catch (Exception exception) when (IsPortTaken(exception))
            {
                server.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not claim a loopback port in {Attempts} attempts. Something other than this suite is using them.");
    }

    /// <summary>
    /// Whether this failure is the port having been taken, as opposed to anything else going wrong.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Swallowing every exception here would turn a real broker failure into
    /// twenty retries and a confusing message about ports, which is the class of mistake this file
    /// exists because of.
    /// </remarks>
    private static bool IsPortTaken(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || (exception.InnerException is not null && IsPortTaken(exception.InnerException));

    /// <summary>
    /// Starts a server the caller built, on a number the caller already reserved.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="ReservePortAsync"/>, for a test that had to tell something else
    /// the port <i>before</i> the broker existed — an uplink that must be pointed at an address while
    /// nothing is listening there. It cannot move to another port, because the thing already
    /// listening is looking at this one; so a bind that fails here is a real failure and is reported
    /// rather than retried into a mystery.
    /// </remarks>
    internal static async Task<TestBroker> StartOnReservedAsync(int port, Func<int, MqttServer> build)
    {
        var server = build(port);
        await server.StartAsync().ConfigureAwait(false);
        return new TestBroker(server, port);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A server being torn down under a test that has already failed is not worth reporting
            // over the failure that matters.
        }

        _server.Dispose();
    }
}
