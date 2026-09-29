using System.Diagnostics;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Phase 7 step 5: what the cloud and edge Compose files promise (ADR-0017), read from Compose's
/// own rendering of them, as <see cref="ComposeFileTests"/> does for the on-premises file.
/// </summary>
public sealed class CloudComposeFileTests
{
    private const string AdminSentinel = "admin-password-sentinel-7a2f";

    private static readonly Dictionary<string, string> Cloud = new()
    {
        ["SCADA_IMAGE_TAG"] = "0000000",
        ["SCADA_DB_ADMIN_PASSWORD"] = AdminSentinel,
        ["SCADA_APP_DB_PASSWORD"] = "app-password-sentinel-3e81",
        ["SCADA_CERT_DIR"] = "/srv/scada/certs",
    };

    private static readonly Dictionary<string, string> Edge = new()
    {
        ["SCADA_IMAGE_TAG"] = "0000000",
        ["SCADA_EDGE_ID"] = "plant-7",
        ["SCADA_BROKER_HOST"] = "mqtt.example.test",
        ["SCADA_EDGE_CERT_DIR"] = "/srv/edge/certs",
    };

    [RequiresDockerComposeFact]
    public void Only_the_database_and_the_migrator_are_given_the_privileged_password()
    {
        var holders = Render("cloud", Cloud).GetProperty("services").EnumerateObject()
            .Where(service => service.Value.GetRawText().Contains(AdminSentinel, StringComparison.Ordinal))
            .Select(service => service.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["migrator", "timescaledb"], holders);
    }

    [RequiresDockerComposeFact]
    public void Each_service_is_given_only_its_own_key_and_none_is_given_the_CAs()
    {
        var services = Render("cloud", Cloud).GetProperty("services");

        // Every file mounted from the host, not only those that look like certificates: a directory
        // mounted whole would hand over every key in it.
        Assert.Equal(
            ["/srv/scada/certs/broker.crt", "/srv/scada/certs/broker.key", "/srv/scada/certs/ca.crt", "deploy/cloud/mosquitto"],
            BindSources(services.GetProperty("broker")));
        Assert.Equal(
            ["/srv/scada/certs/ca.crt", "/srv/scada/certs/scada-gateway.crt", "/srv/scada/certs/scada-gateway.key"],
            BindSources(services.GetProperty("gateway")));

        // The CA's key signs every identity in the system; no service holds it.
        Assert.DoesNotContain("ca.key", services.GetRawText(), StringComparison.Ordinal);
    }

    [RequiresDockerComposeFact]
    public void Only_the_edges_listener_is_published()
    {
        var broker = Render("cloud", Cloud).GetProperty("services").GetProperty("broker");

        var published = broker.GetProperty("ports").EnumerateArray().Select(port => port.GetProperty("target").GetInt32()).ToList();

        // 8884 is the Gateway's listener, where a client keeps the id it asks for; published, an edge
        // could reach it and take over the Gateway's session.
        Assert.Equal([8883], published);
    }

    [RequiresDockerComposeFact]
    public void The_broker_runs_the_version_its_configuration_was_tested_against()
    {
        var broker = Render("cloud", Cloud).GetProperty("services").GetProperty("broker");

        Assert.Equal(BrokerFixture.BrokerImage, broker.GetProperty("image").GetString());
        Assert.Equal(["sh", "/mosquitto/config/broker.sh"], broker.GetProperty("command").EnumerateArray().Select(part => part.GetString()));
    }

    [RequiresDockerComposeFact]
    public void An_edges_id_and_its_certificate_are_named_by_the_same_setting()
    {
        var edge = Render("edge", Edge).GetProperty("services").GetProperty("edge");

        Assert.Equal("plant-7", edge.GetProperty("environment").GetProperty("Edge__Id").GetString());
        Assert.Contains("/srv/edge/certs/plant-7.crt", CertificateSources(edge));
        Assert.Contains("/srv/edge/certs/plant-7.key", CertificateSources(edge));
        Assert.DoesNotContain("ca.key", edge.GetRawText(), StringComparison.Ordinal);

        // The buffer is on a volume: the container is replaced on every upgrade, the buffer must not be.
        Assert.Contains(
            edge.GetProperty("volumes").EnumerateArray(),
            volume => volume.GetProperty("type").GetString() == "volume" && volume.GetProperty("target").GetString() == "/data");
    }

    private static List<string> BindSources(JsonElement service) =>
        service.GetProperty("volumes").EnumerateArray()
            .Where(volume => volume.GetProperty("type").GetString() == "bind")
            .Select(volume => volume.GetProperty("source").GetString()!.Replace('\\', '/'))
            .Select(source => source.Contains("/srv/", StringComparison.Ordinal)
                ? source[source.IndexOf("/srv/", StringComparison.Ordinal)..]
                : source[source.IndexOf("deploy/", StringComparison.Ordinal)..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> CertificateSources(JsonElement service) =>
        service.GetProperty("volumes").EnumerateArray()
            .Where(volume => volume.GetProperty("type").GetString() == "bind")
            .Select(volume => volume.GetProperty("source").GetString()!.Replace('\\', '/'))
            .Where(source => source.EndsWith(".crt", StringComparison.Ordinal) || source.EndsWith(".key", StringComparison.Ordinal))
            .Select(source => source[source.IndexOf("/srv/", StringComparison.Ordinal)..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static JsonElement Render(string topology, IReadOnlyDictionary<string, string> variables)
    {
        var file = Path.Combine(Path.GetDirectoryName(ComposeFileTests.ComposeFile)!, topology, "docker-compose.yml");
        var emptyEnvFile = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in (string[])["compose", "--file", file, "--env-file", emptyEnvFile, "config", "--format", "json"])
            {
                start.ArgumentList.Add(argument);
            }

            // Only what the test sets: nothing inherited from whoever runs the suite.
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("SCADA_", StringComparison.Ordinal)).ToList())
            {
                start.Environment.Remove(name);
            }

            foreach (var (name, value) in variables)
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error.Result);
            return JsonDocument.Parse(output.Result).RootElement.Clone();
        }
        finally
        {
            File.Delete(emptyEnvFile);
        }
    }
}
