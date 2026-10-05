using System.Net;
using System.Net.Sockets;
using MQTTnet.Server;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// An in-process MQTT broker on a loopback port, chosen by trying to bind rather than by asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third copy of this type, and the copies are deliberate.</b> The other two are in
/// <c>Gateway.Tests</c> and <c>EdgeAgent.Tests</c>, and each lives with the tests that use it rather
/// than in a shared project — the bug it works around is in MQTTnet's API and belongs to none of the
/// three assemblies, so no copy is derived from another's subject matter. They must be kept in step
/// by hand, and the reasoning is written out in full in the Gateway one.
/// </para>
/// <para>
/// <b>What it replaces.</b> Picking a free port and then binding it later:
/// </para>
/// <code>
/// using var probe = new TcpListener(IPAddress.Loopback, 0);
/// probe.Start();
/// return ((IPEndPoint)probe.LocalEndpoint).Port;   // and the listener is disposed here
/// </code>
/// <para>
/// The number is free when it is read and free again the moment the listener is disposed, so another
/// test class — in this assembly or in a running one — can be handed the same port before the first
/// has bound it. Measured on 2026-10-05: the second broker throws <c>SocketException: Only one usage
/// of each socket address … is normally permitted</c>, while a <b>client still connects</b> to
/// whichever broker won. Two ends meant to meet on that port then talk to two different brokers, and
/// the test fails much later with a timeout that names neither the port nor the race.
/// </para>
/// <para>
/// This type has not been seen to flake here. It is fixed anyway because the shape is the same one
/// that cost the Gateway suite an unexplainable failure, and a race that has not been observed is not
/// the same thing as a race that is not there.
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
    /// <param name="configure">
    /// Anything the caller needs on the server's options that this helper does not know about. The
    /// pushing driver's tests need <c>WithPersistentSessions(true)</c> — a queue that outlives the
    /// connection is the whole point of several of them — and a helper that decided that for every
    /// caller would be deciding what those tests are about.
    /// </param>
    /// <param name="beforeStart">
    /// The built server, for a caller that needs to hook an event before it starts accepting. The
    /// pushing driver's tests arm <c>ClientSubscribedTopicAsync</c> this way so they can wait for the
    /// driver to be listening rather than sleep and hope.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No port could be claimed in <see cref="Attempts"/> tries, which means something other than
    /// this suite is holding the machine's ports.
    /// </exception>
    internal static async Task<TestBroker> StartAsync(
        Action<MqttServerOptionsBuilder>? configure = null,
        Action<MqttServer>? beforeStart = null)
    {
        var factory = new MqttServerFactory();

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var port = FreePort();

            var options = factory.CreateServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port);

            configure?.Invoke(options);

            var server = factory.CreateMqttServer(options.Build());
            beforeStart?.Invoke(server);

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
    /// Whether this failure is the port having been taken, as opposed to anything else going wrong.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Swallowing every exception here would turn a real broker failure into
    /// twenty retries and a confusing message about ports.
    /// </remarks>
    private static bool IsPortTaken(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || (exception.InnerException is not null && IsPortTaken(exception.InnerException));

    /// <summary>
    /// Takes ownership of a server the caller built, on a port the caller knows.
    /// </summary>
    /// <remarks>
    /// For the one test that restarts the broker on the same port with persistent sessions: it has
    /// to build the server itself to pass that option, and it still wants this type's disposal so
    /// there is one place that knows how a broker is torn down.
    /// </remarks>
    internal static TestBroker Adopt(MqttServer server, int port) => new(server, port);

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
