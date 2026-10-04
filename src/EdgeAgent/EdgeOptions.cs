using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent;

/// <summary>
/// The edge agent's configuration, bound from <c>appsettings.json</c> and the environment.
/// </summary>
/// <remarks>
/// Tag ids are the cloud Gateway's own (ADR-0001): the samples name tags by id, and the Gateway
/// accepts them only for tags it has under that id. What this edge reads is not configured here —
/// the cloud derives it and publishes it over the link the edge already holds (ADR-0019), and this
/// file names only the edge, its broker and its buffer. There is deliberately no device list: a
/// second one, typed by hand, is what ADR-0019 exists to remove.
/// </remarks>
public sealed class EdgeOptions
{
    /// <summary>This edge's identity; its samples go under the topic prefix for it alone (ADR-0017).</summary>
    public string Id { get; set; } = string.Empty;

    public BrokerOptions Broker { get; set; } = new();

    public BufferOptions Buffer { get; set; } = new();

    /// <summary>Where this edge publishes: <c>{TopicPrefix}/{Id}/samples</c>.</summary>
    public string SamplesTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/samples";

    /// <summary>
    /// Where the cloud publishes what this edge reads: <c>{TopicPrefix}/{Id}/config</c>, retained
    /// and versioned (ADR-0019 §4). The edge's own name is the whole address — it is the name in its
    /// certificate, which is what the broker's ACL confines it to (ADR-0017).
    /// </summary>
    public string ConfigurationTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/config";

    /// <summary>
    /// Where this edge says which drivers it has: <c>{TopicPrefix}/{Id}/drivers</c>, retained
    /// (ADR-0019 §8). Under the same name and the same ACL as the two topics beside it, because
    /// the fact is this edge's own and no other edge may state it for it.
    /// </summary>
    public string DriversTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/drivers";

    /// <summary>
    /// Where the cloud asks this edge to write a tag: <c>{TopicPrefix}/{Id}/writes</c> (ADR-0023).
    /// **Not retained**, and that is load-bearing: a write an edge receives the moment it
    /// reconnects is a command to change a plant after the reason for it has passed.
    /// </summary>
    public string WritesTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/writes";

    /// <summary>
    /// Where this edge answers a write: <c>{TopicPrefix}/{Id}/write-results</c>, not retained for
    /// the same reason — the cloud is waiting for it now, and an answer nobody is waiting for is
    /// not worth keeping.
    /// </summary>
    public string WriteResultsTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/write-results";

    /// <summary>Why this configuration cannot run, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Id) || Id.IndexOfAny(['/', '+', '#']) >= 0)
        {
            problems.Add("Edge:Id is required and may not contain '/', '+' or '#' (it becomes part of the topic).");
        }

        if (string.IsNullOrWhiteSpace(Broker.Host))
        {
            problems.Add("Edge:Broker:Host is required.");
        }

        if (Broker.UsesTls)
        {
            // All three or none: half a TLS configuration must not quietly fall back to plain TCP.
            problems.AddRange(Broker.TlsFiles.Problems().Select(problem => $"Edge:Broker: {problem}."));
        }

        if (string.IsNullOrWhiteSpace(Buffer.Path))
        {
            problems.Add("Edge:Buffer:Path is required.");
        }

        if (Buffer.MaxPendingSamples < 1)
        {
            problems.Add("Edge:Buffer:MaxPendingSamples must be at least 1.");
        }

        // Nothing about a device is checked here, because no device is configured here: an edge reads
        // what the cloud derives for it, and a configuration that cannot be read is refused whole on
        // arrival, by name and with every reason (EdgeConfigurationPayload, ADR-0019 §4).
        return problems;
    }
}

public sealed class BrokerOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 1883;

    public string TopicPrefix { get; set; } = "scada/edge";

    /// <summary>
    /// Mutual TLS (ADR-0017): the CA that signed the broker's certificate, and this edge's own
    /// certificate and key, PEM. The certificate's name is this edge's Id: the broker's ACL lets it
    /// publish under that name only. Plain TCP when none is set.
    /// </summary>
    public string CaFile { get; set; } = string.Empty;

    public string CertFile { get; set; } = string.Empty;

    public string KeyFile { get; set; } = string.Empty;

    internal bool UsesTls => !string.IsNullOrWhiteSpace(CaFile) || !string.IsNullOrWhiteSpace(CertFile) || !string.IsNullOrWhiteSpace(KeyFile);

    internal MqttTlsFiles TlsFiles => new(CaFile, CertFile, KeyFile);
}

public sealed class BufferOptions
{
    /// <summary>The SQLite file. On a disk that survives a restart — not a tmpfs.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>How many samples may wait before the oldest are dropped, and their window recorded.</summary>
    public long MaxPendingSamples { get; set; } = 1_000_000;
}

// There is no EdgeDeviceOptions and no EdgeTagOptions here any more, and deliberately so. What an
// edge reads travels as an EdgeConfigurationDevice (Drivers.Mqtt, ADR-0019 §4) — derived by the
// cloud, carrying the Gateway's own tag ids — and a second, local shape of the same thing is one
// more place a tag id could be typed by hand.
