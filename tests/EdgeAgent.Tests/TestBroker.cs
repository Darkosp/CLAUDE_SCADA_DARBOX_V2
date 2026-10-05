using System.Collections.Concurrent;
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

            Watch(server);

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
    /// <summary>
    /// Waits until the broker has a client subscribed to <paramref name="topicFilter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is how a test stops depending on MQTTnet's retained-message delivery, which is not
    /// reliable.</b> A service subscribes in the background — <c>UplinkService</c> connects and
    /// subscribes inside its own loop — so a test that publishes a retained configuration straight
    /// after <c>StartAsync</c> is relying on that message being replayed to a subscription which did
    /// not exist when it was published. The broker holds it, and MQTTnet does not reliably hand it
    /// over. Measured on 2026-10-05 in the Gateway's copy of this type, three ways in one failure: the
    /// publisher logged the publish, the retained set contained the topic, and the subscriber reported
    /// itself connected with nothing received for twenty seconds. That is
    /// [dotnet/MQTTnet#1353](https://github.com/dotnet/MQTTnet/issues/1353) — reported as "maybe it
    /// can be some timing issue", closed as fixed in 4.0, and still reachable at 5.2.0.1603 under load.
    /// </para>
    /// <para>
    /// It is not this project's code and not the product's broker, which is Mosquitto; nothing in this
    /// repository ships MQTTnet's server. So a test that needs a message to arrive waits for the
    /// subscriber to be subscribed, and then publishes. Delivery to a live subscription has never been
    /// seen to fail.
    /// </para>
    /// <para>
    /// Watched through <c>ClientSubscribedTopicAsync</c> rather than read back off the session, because
    /// <c>MqttSessionStatus</c> exposes no subscription list.
    /// </para>
    /// </remarks>
    internal async Task<bool> WaitForSubscriptionAsync(string topicFilter, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            if (Subscribed.Contains(topicFilter))
            {
                return true;
            }

            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>Every topic filter subscribed on this broker, in the order they were subscribed.</summary>
    private static readonly ConcurrentQueue<string> Subscribed = new();

    /// <summary>Arms the subscription watch. Called on every server this type builds.</summary>
    private static void Watch(MqttServer server) =>
        server.ClientSubscribedTopicAsync += args =>
        {
            Subscribed.Enqueue(args.TopicFilter.Topic);
            return Task.CompletedTask;
        };

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
