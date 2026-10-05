using System.Net;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Server;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// An in-process MQTT broker on a loopback port for the life of one test (ADR-0017).
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because picking a free port and then binding it is a race, and the race cost
/// this project a flake it could not explain.</b> The old shape — in five test classes — was:
/// </para>
/// <code>
/// using var probe = new TcpListener(IPAddress.Loopback, 0);
/// probe.Start();
/// return ((IPEndPoint)probe.LocalEndpoint).Port;   // and the listener is disposed here
/// </code>
/// <para>
/// The port is free when it is read and free again the moment the listener is disposed, so a second
/// test class can be handed the same number before the first has bound it. What that produces was
/// measured on 2026-10-05 rather than guessed: the second broker throws
/// <c>SocketException: Only one usage of each socket address … is normally permitted</c>, while a
/// <b>client still connects</b> — to whichever broker won. A publisher and a subscriber that were
/// meant to meet on that port then talk to two different brokers, the configuration never arrives,
/// and the test fails twenty seconds later with <c>Timed out waiting for the first configuration to
/// be published</c>.
/// </para>
/// <para>
/// That is exactly the failure that was seen once and could not be named. It was rare because it
/// needs two classes to be handed the same port inside a short window, and it was confusing because
/// the test that fails is not the test that lost the race.
/// </para>
/// <para>
/// The fix is to <b>try to bind, and try again on a different port when the bind fails</b>. That
/// removes the window entirely: there is no moment between choosing a port and claiming it, because
/// choosing and claiming are the same attempt. It cannot deadlock and it cannot loop forever — a
/// machine where twenty thousand ports are all taken has a problem this is not going to solve.
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
                // another. Nothing is left running from this attempt: a server that failed to bind
                // never became a listener.
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
    /// twenty retries and a confusing message about ports, which is the class of mistake this whole
    /// file is about.
    /// </remarks>
    private static bool IsPortTaken(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || (exception.InnerException is not null && IsPortTaken(exception.InnerException));

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
            // A server that is being torn down under a test that has already failed is not worth
            // reporting over the failure that matters.
        }

        _server.Dispose();
    }
}
