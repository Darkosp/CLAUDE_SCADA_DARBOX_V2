using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// A real Mosquitto running the repository's own broker configuration, <c>deploy/cloud/mosquitto</c>,
/// mounted as it is — so a change to that configuration is what the tests run against — with
/// throwaway certificates made here: a CA, the broker, the Gateway, two edges, and an impostor
/// signed by another CA.
/// </summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    public const string GatewayName = "scada-gateway";

    /// <summary>The broker image the cloud Compose file pins; ComposeFileTests holds the two together.</summary>
    public const string BrokerImage = "eclipse-mosquitto:2.1.2-alpine";

    private readonly string _certs = Path.Combine(Path.GetTempPath(), $"broker-certs-{Guid.NewGuid():N}");

    public string Container { get; } = $"scada-brokertest-{Guid.NewGuid():N}"[..28];

    /// <summary>The edges' listener, 8883 in the container.</summary>
    public int EdgePort { get; private set; }

    /// <summary>The Gateway's listener, 8884 in the container.</summary>
    public int GatewayPort { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerAvailable.IsAvailable)
        {
            return;
        }

        MakeCertificates();

        Docker(
            $"run -d --name {Container} -p 127.0.0.1::8883 -p 127.0.0.1::8884 " +
            $"-v \"{ConfigDirectory}:/mosquitto/config:ro\" -v \"{_certs}:/mosquitto/certs:ro\" " +
            $"{BrokerImage} sh /mosquitto/config/broker.sh");

        await WaitUntilListeningAsync();
    }

    public Task DisposeAsync()
    {
        if (DockerAvailable.IsAvailable)
        {
            Docker($"rm -f -v {Container}", check: false);
        }

        if (Directory.Exists(_certs))
        {
            Directory.Delete(_certs, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>The client files for a name made here: <c>scada-gateway</c>, <c>edge-a</c>, <c>edge-b</c>, <c>impostor</c>.</summary>
    public MqttTlsFiles Files(string name) =>
        new(Path.Combine(_certs, name == "impostor" ? "other-ca.crt" : "ca.crt"), Path.Combine(_certs, $"{name}.crt"), Path.Combine(_certs, $"{name}.key"));

    /// <summary>An edge: on the edges' listener, MQTT 5, its own certificate.</summary>
    public MqttClientOptions Edge(string name, string? clientId = null) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", EdgePort)
            .WithClientId(clientId ?? $"scada-edge-{name}")
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithMutualTls(Files(name))
            .Build();

    /// <summary>The Gateway: on its own listener, with the persistent session its driver uses.</summary>
    public MqttClientOptions Gateway(string clientId) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", GatewayPort)
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(false)
            .WithSessionExpiryInterval(3600)
            .WithMutualTls(Files(GatewayName))
            .Build();

    /// <summary>The broker's audit file of refusals and connections.</summary>
    public string AuditLog() => Docker($"exec {Container} cat /mosquitto/audit/refused.log", check: false);

    /// <summary>Kills the broker without warning, as a crash or a power cut would, and starts it again.</summary>
    public async Task KillAndRestartAsync()
    {
        Docker($"kill -s KILL {Container}");
        Docker($"start {Container}");
        await WaitUntilListeningAsync();
    }

    public static string Docker(string arguments, bool check = true)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (check && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments} failed: {error}");
        }

        return output;
    }

    /// <summary>
    /// Whether Docker is there, asked through <see cref="ToolProbe"/> so that a daemon that did
    /// not answer in time is not reported as one that is not installed.
    /// </summary>
    internal static readonly ToolAvailability DockerAvailable = new(
        "Docker",
        TimeSpan.FromSeconds(60),
        () => ToolProbe.Run("docker", "version --format {{.Server.Version}}", TimeSpan.FromSeconds(60)));

    private static string ConfigDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScadaDarbox.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "deploy", "cloud", "mosquitto");
        }
    }

    private async Task WaitUntilListeningAsync()
    {
        // Host ports are assigned again on every start.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                EdgePort = HostPort("8883");
                GatewayPort = HostPort("8884");

                using var probe = new MqttClientFactory().CreateMqttClient();
                await probe.ConnectAsync(Edge("edge-a", "readiness-probe"));
                await probe.DisconnectAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private int HostPort(string containerPort)
    {
        var mapping = Docker($"port {Container} {containerPort}/tcp").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    private void MakeCertificates()
    {
        Directory.CreateDirectory(_certs);

        using var authority = Authority("Test broker CA");
        Write("ca", authority, authority.GetRSAPrivateKey()!);

        using (var brokerKey = RSA.Create(2048))
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            using var broker = Issue(authority, "localhost", brokerKey, serverAuth: true, names.Build());
            Write("broker", broker, brokerKey);
        }

        foreach (var name in new[] { GatewayName, "edge-a", "edge-b" })
        {
            using var key = RSA.Create(2048);
            using var client = Issue(authority, name, key, serverAuth: false);
            Write(name, client, key);
        }

        // Named edge-a, as a real edge is — but issued by some other authority.
        using var other = Authority("Some other CA");
        Write("other-ca", other, other.GetRSAPrivateKey()!);
        using var impostorKey = RSA.Create(2048);
        using var impostor = Issue(other, "edge-a", impostorKey, serverAuth: false);
        Write("impostor", impostor, impostorKey);
    }

    private static X509Certificate2 Authority(string name)
    {
        // Not disposed here: the certificate returned signs with it.
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Issue(X509Certificate2 authority, string name, RSA key, bool serverAuth, X509Extension? names = null)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (names is not null)
        {
            request.CertificateExtensions.Add(names);
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
    }

    private void Write(string name, X509Certificate2 certificate, RSA key)
    {
        File.WriteAllText(Path.Combine(_certs, $"{name}.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_certs, $"{name}.key"), key.ExportPkcs8PrivateKeyPem());
    }
}

/// <summary>Reports as skipped, rather than passing, where Docker is not available.</summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!BrokerFixture.DockerAvailable.IsAvailable)
        {
            Skip = BrokerFixture.DockerAvailable.Why;
        }
    }
}
