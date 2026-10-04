using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Where the Gateway publishes each edge's configuration (ADR-0019 §4): the broker, over the same
/// mutually authenticated link its own MQTT devices use, and the prefix edge topics sit under.
/// </summary>
/// <remarks>
/// There is no username here, and no password. The broker reads the name in the client
/// certificate — the Gateway's own, the one the ACL is already written against (ADR-0017) — so
/// the Gateway's identity is its certificate, exactly as an edge's is.
/// </remarks>
public sealed class EdgeProvisioningOptions
{
    /// <summary>
    /// Whether the Gateway publishes configurations at all. Off, nothing is published and every
    /// edge keeps the configuration it already holds — the way a deployment is undone without
    /// touching the edges.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The broker, as the cloud's Compose file names it inside the stack.</summary>
    public string Host { get; init; } = "broker";

    /// <summary>The broker's listener for clients inside the stack, not the edges' published one.</summary>
    public int Port { get; init; } = 8884;

    /// <summary>
    /// Whether to connect over TLS with this client's certificate. Only ever false for a broker
    /// that is not the cloud's — a test's, which has no certificates to present.
    /// </summary>
    public bool UsesTls { get; init; } = true;

    /// <summary>The prefix edge topics sit under, e.g. <c>scada/edge</c>.</summary>
    public string TopicPrefix { get; init; } = "scada/edge";

    /// <summary>
    /// Whether a tag whose device an edge reads may be written over the link at all (ADR-0023 §8).
    /// </summary>
    /// <remarks>
    /// On by default, which is what today's behaviour is for a device the Gateway polls: an Operator
    /// may write one and always could. This extends the same ability to the devices behind an edge.
    /// **Off** makes a write to such a tag a refusal by name, exactly as it was before ADR-0023 —
    /// for a plant where commanding equipment from a cloud is not a capability that should be
    /// reachable, and should have to be turned on deliberately rather than inherited.
    /// </remarks>
    public bool WritesEnabled { get; init; } = true;

    public string CaFile { get; init; } = "/app/mqtt/ca.crt";

    public string CertFile { get; init; } = "/app/mqtt/scada-gateway.crt";

    public string KeyFile { get; init; } = "/app/mqtt/scada-gateway.key";

    /// <summary>The Gateway's TLS files, as the MQTT client wants them.</summary>
    public MqttTlsFiles TlsFiles => new(CaFile, CertFile, KeyFile);

    /// <summary>
    /// The topic one edge's configuration is published to. The edge's name is the whole address:
    /// it is the name in the edge's certificate, which is the name the broker's ACL confines it
    /// to (ADR-0017).
    /// </summary>
    public string ConfigurationTopic(string edgeName) => $"{TopicPrefix}/{edgeName}/config";

    /// <summary>
    /// The topic one edge declares its drivers on (ADR-0019 §8).
    /// </summary>
    public string DriversTopic(string edgeName) => $"{TopicPrefix}/{edgeName}/drivers";

    /// <summary>
    /// Every edge's declaration topic at once. One subscription for the deployment rather than one
    /// per edge, because the case that matters most is an edge the Gateway does not know about yet:
    /// a subscription created from the catalogue could never hear from it.
    /// </summary>
    public string DriversTopicFilter => $"{TopicPrefix}/+/drivers";

    /// <summary>
    /// Where one edge is told to write a tag (ADR-0023). Published by the Gateway, read by that one
    /// edge, and **never retained**: a command an edge receives the moment it reconnects is one
    /// whose moment has passed.
    /// </summary>
    public string WritesTopic(string edgeName) => $"{TopicPrefix}/{edgeName}/writes";

    /// <summary>
    /// Every edge's write result at once, for the reason <see cref="DriversTopicFilter"/> exists.
    /// </summary>
    public string WriteResultsTopicFilter => $"{TopicPrefix}/+/write-results";

    /// <summary>Why these settings cannot be used, or nothing.</summary>
    public IReadOnlyList<string> Problems() =>
        UsesTls ? TlsFiles.Problems() : [];
}
