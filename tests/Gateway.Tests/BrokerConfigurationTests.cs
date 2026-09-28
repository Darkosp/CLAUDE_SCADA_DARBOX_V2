using System.Collections.Concurrent;
using MQTTnet;
using MQTTnet.Protocol;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The broker as deployed (ADR-0017), run for real: <c>deploy/cloud/mosquitto</c> in an
/// <c>eclipse-mosquitto:2</c> container. Each edge publishes under its own name only and a refusal
/// is audited; no certificate, or one from another CA, is no connection; what the broker holds for
/// an absent Gateway is neither capped nor lost to a crash, and cannot be taken by an edge.
/// </summary>
public sealed class BrokerConfigurationTests : IClassFixture<BrokerFixture>
{
    private readonly BrokerFixture _broker;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public BrokerConfigurationTests(BrokerFixture broker) => _broker = broker;

    [RequiresDockerFact]
    public async Task An_edge_publishes_under_its_own_name_and_the_Gateway_receives_it()
    {
        await using var gateway = await GatewayAsync();
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));

        var result = await PublishAsync(edge, "scada/edge/edge-a/samples", "own");

        Assert.True(result.IsSuccess, $"refused: {result.ReasonCode}");
        await gateway.WaitForAsync(1);
    }

    [RequiresDockerFact]
    public async Task An_edge_publishing_another_edges_topic_is_refused_and_the_refusal_is_audited()
    {
        await using var gateway = await GatewayAsync();

        // edge-a's certificate, and a client id that claims to be edge-b.
        using var edge = await ConnectAsync(_broker.Edge("edge-a", clientId: "scada-edge-edge-b"));

        var result = await PublishAsync(edge, "scada/edge/edge-b/samples", "forged");

        Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, result.ReasonCode);

        // Audited: the refusal with the topic it tried, under a client id that — on the edges'
        // listener — is the certificate's name, not the one it claimed.
        var audit = _broker.AuditLog();
        Assert.Contains("Denied PUBLISH from edge-a ", audit, StringComparison.Ordinal);
        Assert.Contains("'scada/edge/edge-b/samples'", audit, StringComparison.Ordinal);
        Assert.Contains("as edge-a (p5", audit, StringComparison.Ordinal);
        Assert.Contains("u'edge-a')", audit, StringComparison.Ordinal);

        // And nothing reached the Gateway.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(gateway.Received, message => message.Payload.EndsWith("forged", StringComparison.Ordinal));
    }

    [RequiresDockerFact]
    public async Task A_client_without_a_certificate_from_our_authority_does_not_connect()
    {
        // No client certificate at all.
        var anonymous = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker.EdgePort)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(_ => true))
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(anonymous));

        // A certificate named edge-a, issued by some other authority.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(_broker.Edge("impostor")));

        // Plain TCP.
        var plain = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _broker.EdgePort).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(plain));

        // The control: the real edge-a does connect.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));
        Assert.True(edge.IsConnected);
    }

    [RequiresDockerFact]
    public async Task An_edge_cannot_take_over_the_Gateways_session_and_empty_its_queue()
    {
        var gatewayId = $"scada-darbox-{_run}";
        await (await GatewayAsync(gatewayId)).DisposeAsync();

        using (var edge = await ConnectAsync(_broker.Edge("edge-a")))
        {
            Assert.True((await PublishAsync(edge, "scada/edge/edge-a/samples", "while-away")).IsSuccess);
        }

        // An edge connects under the Gateway's client id, with a persistent session, as if to
        // resume it. Were that allowed, the broker would hand it the Gateway's queue and discard
        // every message the edge may not read.
        using (var thief = new MqttClientFactory().CreateMqttClient())
        {
            var options = _broker.Edge("edge-a", clientId: gatewayId);
            options.CleanSession = false;
            options.SessionExpiryInterval = 3600;
            await thief.ConnectAsync(options);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await thief.DisconnectAsync();
        }

        await using var back = await GatewayAsync(gatewayId);
        var received = await back.WaitForAsync(1);
        Assert.Contains(received, message => message.Payload.EndsWith("while-away", StringComparison.Ordinal));
    }

    [RequiresDockerFact]
    public async Task What_is_queued_for_an_absent_Gateway_is_not_capped()
    {
        // Mosquitto's default keeps 1000 and discards the rest without a word.
        const int Published = 1_100;
        var gatewayId = $"scada-darbox-{_run}";
        await (await GatewayAsync(gatewayId)).DisposeAsync();

        using (var edge = await ConnectAsync(_broker.Edge("edge-b")))
        {
            for (var i = 0; i < Published; i++)
            {
                Assert.True((await PublishAsync(edge, "scada/edge/edge-b/samples", $"n{i}")).IsSuccess);
            }
        }

        await using var back = await GatewayAsync(gatewayId);
        var received = await back.WaitForAsync(Published, TimeSpan.FromSeconds(60));
        Assert.True(
            received.Count == Published,
            $"{Published} published while the Gateway was away, {received.Count} delivered when it came back.");
    }

    [RequiresDockerFact]
    public async Task What_is_queued_for_an_absent_Gateway_survives_the_broker_being_killed()
    {
        var gatewayId = $"scada-darbox-{_run}";
        await (await GatewayAsync(gatewayId)).DisposeAsync();

        using (var edge = await ConnectAsync(_broker.Edge("edge-a")))
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.True((await PublishAsync(edge, "scada/edge/edge-a/samples", $"k{i}")).IsSuccess);
            }
        }

        // Acknowledged to the edge — which has deleted them — and then a crash.
        await _broker.KillAndRestartAsync();

        await using var back = await GatewayAsync(gatewayId);
        var received = await back.WaitForAsync(5, TimeSpan.FromSeconds(20));
        Assert.Equal(5, received.Count);
    }

    [RequiresDockerFact]
    public async Task The_broker_counts_a_messages_expiry_down_by_the_time_it_held_it()
    {
        // What the Gateway's skew check relies on (SamplePayload.WaitedInBroker): measured by the
        // broker, not by either end's clock.
        var gatewayId = $"scada-darbox-{_run}";
        await (await GatewayAsync(gatewayId)).DisposeAsync();

        using (var edge = await ConnectAsync(_broker.Edge("edge-a")))
        {
            Assert.True((await PublishAsync(edge, "scada/edge/edge-a/samples", "held")).IsSuccess);
        }

        await Task.Delay(TimeSpan.FromSeconds(4));

        await using var back = await GatewayAsync(gatewayId);
        var message = Assert.Single(await back.WaitForAsync(1));
        Assert.InRange(SamplePayload.WaitedInBroker(message.Expiry), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    }

    [RequiresDockerFact]
    public async Task The_Gateway_publishes_an_edges_configuration_and_that_edge_reads_it()
    {
        // The Gateway's own identity, publishing retained to an edge's topic (ADR-0019 §4).
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));
        var published = await gateway.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/config")
            .WithPayload($"{_run}:config")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build());
        Assert.True(published.IsSuccess, $"refused: {published.ReasonCode}");

        // The edge asks for it and is given it: retained, so it arrives without the cloud having
        // to notice that the edge connected.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));
        var received = new ConcurrentQueue<string>();
        edge.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-a/config", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal($"{_run}:config", received.SingleOrDefault());
    }

    [RequiresDockerFact]
    public async Task An_edge_cannot_read_another_edges_configuration()
    {
        // A retained configuration for edge-b is sitting on the broker.
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));
        Assert.True((await gateway.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-b/config")
            .WithPayload($"{_run}:forbidden")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build())).IsSuccess);

        // edge-a asks for it, and is given nothing: the ACL confines every edge to its own name.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));
        var received = new ConcurrentQueue<string>();
        edge.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-b/config", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(received);
    }

    private async Task<GatewaySession> GatewayAsync(string? clientId = null)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var session = new GatewaySession(client, _run);
        client.ApplicationMessageReceivedAsync += session.OnMessageAsync;
        await client.ConnectAsync(_broker.Gateway(clientId ?? $"scada-darbox-{_run}-{Guid.NewGuid():N}"[..40]));
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/+/samples", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        return session;
    }

    private static async Task<IMqttClient> ConnectAsync(MqttClientOptions options)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(options, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private Task<MqttClientPublishResult> PublishAsync(IMqttClient client, string topic, string text) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload($"{_run}:{text}")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithMessageExpiryInterval(SamplePayload.MessageExpirySeconds)
            .Build());

    /// <summary>The Gateway's side: what arrived for this test run, with the expiry it arrived with.</summary>
    private sealed class GatewaySession(IMqttClient client, string run) : IAsyncDisposable
    {
        public ConcurrentQueue<Received> Received { get; } = new();

        public Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs message)
        {
            var payload = message.ApplicationMessage.ConvertPayloadToString();
            if (payload.StartsWith(run + ":", StringComparison.Ordinal))
            {
                Received.Enqueue(new Received(payload, message.ApplicationMessage.MessageExpiryInterval));
            }

            return Task.CompletedTask;
        }

        public async Task<List<Received>> WaitForAsync(int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (Received.Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            // A little longer, so more than expected would show.
            await Task.Delay(300);
            return Received.ToList();
        }

        public async ValueTask DisposeAsync()
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync();
            }

            client.Dispose();
        }
    }

    private sealed record Received(string Payload, uint Expiry);
}
