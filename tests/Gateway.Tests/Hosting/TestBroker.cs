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

    /// <summary>
    /// What the broker is holding as retained, by topic — the question a test asks when a message it
    /// watched being published does not arrive.
    /// </summary>
    /// <remarks>
    /// This exists because "the publisher published and the subscriber did not receive it" has two
    /// very different explanations, and the broker is the only thing that can tell them apart: either
    /// the retained message was stored and was not delivered, or it was never stored at all. Without
    /// this, both look identical from the outside — a timeout.
    /// </remarks>
    internal async Task<IReadOnlyList<string>> RetainedTopicsAsync()
    {
        var retained = await _server.GetRetainedMessagesAsync().ConfigureAwait(false);
        return retained.Select(message => message.Topic).ToList();
    }

    /// <summary>The retained messages themselves, for a test that needs to read the payload back.</summary>
    internal async Task<IReadOnlyList<MqttApplicationMessage>> RetainedMessagesAsync() =>
        (await _server.GetRetainedMessagesAsync().ConfigureAwait(false)).ToList();

    /// <summary>
    /// Waits until the broker is holding a retained message on <paramref name="topic"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because delivering a retained message to a subscriber that was not yet connected
    /// is not reliable in MQTTnet's in-process server, and the tests were built on it.</b> Measured on
    /// 2026-10-05, three ways, in one failure: the publisher logged that it had published the
    /// configuration to <c>scada/edge/edge-a/config</c>; <see cref="RetainedTopicsAsync"/> showed the
    /// broker holding it; and the subscriber reported <c>connected=True</c> with nothing received, for
    /// twenty seconds. That is [dotnet/MQTTnet#1353](https://github.com/dotnet/MQTTnet/issues/1353)
    /// exactly, reproduced "maybe it can be some timing issue" against an old preview and closed as
    /// fixed in 4.0 — and it is still reachable at 5.2.0.1603 under load.
    /// </para>
    /// <para>
    /// It is <b>not this project's code</b>: the publisher set the retain flag (ADR-0019 §4) and the
    /// broker stored it. It is also <b>not the product's broker</b>, which is Mosquitto — this type is
    /// a test double, and nothing in this repository ships MQTTnet's server. So a test that waits for
    /// this delivery is testing MQTTnet, not the project.
    /// </para>
    /// <para>
    /// What a test should wait for instead is the thing it actually owns: that the cloud published,
    /// retained, with the right content. <b>The retained set is the authoritative statement of that</b>
    /// — the publisher was told the publish succeeded, and the broker is holding it. Delivery to a
    /// later subscriber is then asserted where it is deterministic, in the test that publishes and
    /// subscribes on the same connection.
    /// </para>
    /// </remarks>
    internal async Task<bool> RetainsAsync(string topic, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var retained = await _server.GetRetainedMessagesAsync().ConfigureAwait(false);
            if (retained.Any(message => message.Topic == topic))
            {
                return true;
            }

            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The clients the broker believes are connected, with what it believes each has pending.
    /// </summary>
    /// <remarks>
    /// The question a test asks when a client says it is connected and subscribed, a retained message
    /// is sitting on the broker, and nothing is delivered. Those two accounts can disagree, and this
    /// is the only way to find out that they do.
    /// </remarks>
    internal async Task<IReadOnlyList<string>> ClientSubscriptionsAsync()
    {
        var sessions = await _server.GetSessionsAsync().ConfigureAwait(false);

        return sessions
            .Select(session => $"{session.Id} pending={session.PendingApplicationMessagesCount}")
            .ToList();
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
