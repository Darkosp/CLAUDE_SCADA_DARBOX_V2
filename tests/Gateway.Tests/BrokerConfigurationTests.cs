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

    [RequiresDockerFact]
    public async Task An_edge_declares_its_drivers_and_the_Gateway_reads_it_and_no_other_edge_can()
    {
        // The declaration is the edge's own statement about its build, so the ACL gives it write on
        // its own topic only (ADR-0019 §8) — the same confinement as its samples.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));

        var declared = await edge.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/drivers")
            .WithPayload($"{_run}:drivers")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build());
        Assert.True(declared.IsSuccess, $"refused: {declared.ReasonCode}");

        // The Gateway reads every edge's declaration: one subscription for the deployment, because
        // an edge the catalogue does not know yet is exactly the one it has to hear from.
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));
        var received = new ConcurrentQueue<string>();
        gateway.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await gateway.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/+/drivers", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal($"{_run}:drivers", received.SingleOrDefault());

        // Another edge may not say it for it: a declaration an edge did not make is a declaration
        // about a build nobody asked.
        using var other = await ConnectAsync(_broker.Edge("edge-b"));
        var forged = await other.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/drivers")
            .WithPayload($"{_run}:forged")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        Assert.False(forged.IsSuccess, "an edge must not be able to declare another edge's drivers");
    }

    [RequiresDockerFact]
    public async Task The_Gateway_asks_an_edge_to_write_and_that_edge_answers_it()
    {
        // The write conversation (ADR-0023 §1-§2): the cloud publishes a request on the edge's own
        // topic, and the edge publishes its result back. Two new topics, and the ACL has to allow
        // both or the path is dead in the water — which is what this test is for, the two rules
        // having been written by inspection and never exercised until now.
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));

        var received = new ConcurrentQueue<string>();
        edge.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-a/writes", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        var asked = await gateway.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/writes")
            .WithPayload($"{_run}:write")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        Assert.True(asked.IsSuccess, $"the Gateway must be able to ask: {asked.ReasonCode}");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal($"{_run}:write", received.SingleOrDefault());

        // The answer travels the other way, and the Gateway reads every edge's results on one
        // subscription — matched by the id in the payload, not by the topic.
        using var reader = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-results"));
        var results = new ConcurrentQueue<string>();
        reader.ApplicationMessageReceivedAsync += message =>
        {
            results.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await reader.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/+/write-results", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        var answered = await edge.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/write-results")
            .WithPayload($"{_run}:written")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        Assert.True(answered.IsSuccess, $"the edge must be able to answer: {answered.ReasonCode}");

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (results.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal($"{_run}:written", results.SingleOrDefault());
    }

    [RequiresDockerFact]
    public async Task An_edge_cannot_read_another_edges_write_requests()
    {
        // ADR-0023's confinement, and the one that matters most on this path: a write request is a
        // command to change a plant, so a plant being able to read another plant's commands would
        // be an operator's action leaking across sites — and, worse, an edge that acted on one.
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));

        var received = new ConcurrentQueue<string>();
        edge.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };

        // The positive control first, and it is not optional: an ACL that refused *everything* would
        // satisfy the assertion below just as well, and would be a broken deployment rather than a
        // confined one. Subscribing to this edge's own topic delivers, so the mechanism works.
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-a/writes", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await PublishAndAwaitOne(gateway, "scada/edge/edge-a/writes", $"{_run}:own", received);

        // Now the same edge asks for another edge's requests.
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-b/writes", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await PublishAndAwaitOne(gateway, "scada/edge/edge-b/writes", $"{_run}:forbidden", received);

        // It was given its own and not the other's: the count says the second publish delivered
        // nothing, and the contents say which one arrived.
        Assert.Equal([$"{_run}:own"], received);
    }

    /// <summary>
    /// Publishes, waits for it to arrive, and gives the assertion something to be wrong about — a
    /// subscription that silently failed and a topic that carried nothing look identical otherwise.
    /// </summary>
    private static async Task PublishAndAwaitOne(
        IMqttClient publisher,
        string topic,
        string payload,
        ConcurrentQueue<string> received)
    {
        var before = received.Count;

        Assert.True((await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build())).IsSuccess, $"the Gateway must be able to publish to {topic}");

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (received.Count == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    [RequiresDockerFact]
    public async Task An_edge_cannot_answer_another_edges_write()
    {
        // The mirror of the declaration rule (ADR-0021): a result is this edge's own statement
        // about its own plant, so an edge that could publish under another edge's name could tell
        // the cloud a write succeeded that never happened here — the untruth the whole path is
        // built to avoid.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));

        var forged = await edge.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-b/write-results")
            .WithPayload($"{_run}:forged")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.False(forged.IsSuccess, "an edge must not be able to answer another edge's write");
    }

    [RequiresDockerFact]
    public async Task A_write_request_the_broker_holds_is_not_given_to_an_edge_that_asks_later()
    {
        // ADR-0023 §3, and the reason it is a decision rather than a detail: a retained write is one
        // an edge receives the moment it reconnects, having missed the moment. A late sample is
        // still true of its own moment; a late command is a request to change a plant after the
        // reason for it has passed.
        //
        // This watches the broker's half — that a request published **without** the flag leaves
        // nothing behind for a later connection, which is what an edge arriving after the fact
        // would find. The other half is that the Gateway's own publisher never sets the flag, and
        // that is asserted where that publisher is tested.
        using var gateway = await ConnectAsync(_broker.Gateway($"scada-darbox-{_run}-provisioning"));

        Assert.True((await gateway.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scada/edge/edge-a/writes")
            .WithPayload($"{_run}:stale-command")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            // Deliberately NOT .WithRetainFlag(): this is how the product publishes a write.
            .Build())).IsSuccess);

        // A fresh connection, as an edge that was offline would make.
        using var edge = await ConnectAsync(_broker.Edge("edge-a"));
        var received = new ConcurrentQueue<string>();
        edge.ApplicationMessageReceivedAsync += message =>
        {
            received.Enqueue(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await edge.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("scada/edge/edge-a/writes", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Nothing waited for it. The control that this topic is otherwise live is the first test in
        // this pair: the same topic, the same ACL, and a message that does arrive when it is sent
        // while somebody is listening.
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
