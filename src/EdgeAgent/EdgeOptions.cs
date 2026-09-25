using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent;

/// <summary>
/// The edge agent's configuration, bound from <c>appsettings.json</c> and the environment.
/// </summary>
/// <remarks>
/// Tag ids are the cloud Gateway's own (ADR-0001): the samples name tags by id, and the Gateway
/// accepts them only for tags it has under that id. How this list reaches an edge — copied by
/// hand today — is still open.
/// </remarks>
public sealed class EdgeOptions
{
    /// <summary>This edge's identity; its samples go under the topic prefix for it alone (ADR-0017).</summary>
    public string Id { get; set; } = string.Empty;

    public BrokerOptions Broker { get; set; } = new();

    public BufferOptions Buffer { get; set; } = new();

    public List<EdgeDeviceOptions> Devices { get; set; } = [];

    /// <summary>Where this edge publishes: <c>{TopicPrefix}/{Id}/samples</c>.</summary>
    public string SamplesTopic => $"{Broker.TopicPrefix.TrimEnd('/')}/{Id}/samples";

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

        foreach (var device in Devices)
        {
            if (device.ScanIntervalMs < 1)
            {
                problems.Add($"Device '{device.Name}': ScanIntervalMs must be positive.");
            }

            if (device.Tags.Count == 0)
            {
                problems.Add($"Device '{device.Name}' has no tags.");
            }

            problems.AddRange(device.Tags
                .Where(tag => tag.Id == Guid.Empty || string.IsNullOrWhiteSpace(tag.Address))
                .Select(tag => $"Device '{device.Name}': every tag needs the Gateway's tag Id and an Address."));
        }

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

public sealed class EdgeDeviceOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary><c>modbus-tcp</c> or <c>opc-ua</c> — the same driver keys, and modules, as the Gateway's.</summary>
    public string Driver { get; set; } = string.Empty;

    public int ScanIntervalMs { get; set; } = 1000;

    public Dictionary<string, string> Settings { get; set; } = [];

    public List<EdgeTagOptions> Tags { get; set; } = [];
}

public sealed class EdgeTagOptions
{
    /// <summary>The cloud Gateway's id for this tag.</summary>
    public Guid Id { get; set; }

    public string Address { get; set; } = string.Empty;

    public TagValueKind Kind { get; set; } = TagValueKind.Numeric;
}
