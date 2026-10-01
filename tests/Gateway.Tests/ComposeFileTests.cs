using System.Diagnostics;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Phase 6: what the on-premises Compose file promises, read from Compose's own rendering of it
/// (<c>docker compose config</c>) rather than from the YAML text — so interpolation, defaults and
/// merging are Compose's, not a guess at them.
/// </summary>
/// <remarks>
/// The secrets passed in are sentinels, not credentials: nothing is started. An empty env file
/// is passed explicitly so a real <c>deploy/.env</c> on the machine cannot leak into the result.
/// </remarks>
public sealed class ComposeFileTests
{
    private const string AdminSentinel = "admin-password-sentinel-5c1e";
    private const string AppSentinel = "app-password-sentinel-9b7d";

    private static readonly Dictionary<string, string> AllSet = new()
    {
        ["SCADA_IMAGE_TAG"] = "0000000",
        ["SCADA_DB_ADMIN_PASSWORD"] = AdminSentinel,
        ["SCADA_APP_DB_PASSWORD"] = AppSentinel,
    };

    [RequiresDockerComposeFact]
    public void Only_the_database_and_the_migrator_are_given_the_privileged_password()
    {
        var services = Render(AllSet).GetProperty("services");

        var holders = services.EnumerateObject()
            .Where(service => service.Value.GetRawText().Contains(AdminSentinel, StringComparison.Ordinal))
            .Select(service => service.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The migrator holding it shows the search finds it where it is.
        Assert.Equal(["migrator", "timescaledb"], holders);

        // And the Gateway's own connection names the application role, with its password
        // supplied separately.
        var gateway = services.GetProperty("gateway").GetProperty("environment");
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(gateway.GetProperty("ConnectionStrings__ScadaDb").GetString());
        Assert.Equal("scada_app", connection.Username);
        Assert.Null(connection.Password);
        Assert.False(gateway.TryGetProperty("SCADA_MIGRATOR_CONNECTION", out _));
        Assert.Equal(AppSentinel, gateway.GetProperty("SCADA_APP_DB_PASSWORD").GetString());
    }

    [RequiresDockerComposeFact]
    public void The_gateway_starts_only_after_the_migrator_has_completed()
    {
        var services = Render(AllSet).GetProperty("services");

        var dependency = services.GetProperty("gateway").GetProperty("depends_on").GetProperty("migrator");
        Assert.Equal("service_completed_successfully", dependency.GetProperty("condition").GetString());

        // A one-shot: a restart policy would turn a failed migration into a loop, and a
        // completed one into a container that never counts as completed.
        Assert.Equal("no", services.GetProperty("migrator").GetProperty("restart").GetString());
    }

    [RequiresDockerComposeFact]
    public void What_the_gateway_must_keep_across_containers_is_on_named_volumes()
    {
        var gateway = Render(AllSet).GetProperty("services").GetProperty("gateway");
        var mounts = gateway.GetProperty("volumes").EnumerateArray()
            .Where(mount => mount.GetProperty("type").GetString() == "volume")
            .Select(mount => mount.GetProperty("target").GetString())
            .ToList();

        // The OPC UA certificate: a new one would be refused by servers that trusted the old.
        Assert.Contains("/app/pki", mounts);

        // The Data Protection key ring, wherever the Gateway is told to keep it.
        var keys = gateway.GetProperty("environment").GetProperty("DataProtection__KeysDirectory").GetString();
        Assert.Contains(keys, mounts);
    }

    [RequiresDockerComposeTheory]
    [InlineData("SCADA_DB_ADMIN_PASSWORD")]
    [InlineData("SCADA_APP_DB_PASSWORD")]
    [InlineData("SCADA_IMAGE_TAG")]
    public void A_missing_secret_stops_compose_and_is_named(string variable)
    {
        var withoutIt = AllSet.Where(pair => pair.Key != variable).ToDictionary();

        var (exitCode, _, error) = Compose(withoutIt, "config", "--format", "json");

        // Not rendered with some default in its place: refused, and the refusal names it.
        Assert.NotEqual(0, exitCode);
        Assert.Contains($"required variable {variable} is missing a value", error, StringComparison.Ordinal);
    }

    [RequiresDockerComposeFact]
    public void An_unset_initial_admin_is_empty_rather_than_a_default()
    {
        var gateway = Render(AllSet).GetProperty("services").GetProperty("gateway").GetProperty("environment");

        // Empty means "not requested": the Gateway creates no account (ADR-0011).
        Assert.Equal(string.Empty, gateway.GetProperty("SCADA_INITIAL_ADMIN_USERNAME").GetString());
        Assert.Equal(string.Empty, gateway.GetProperty("SCADA_INITIAL_ADMIN_PASSWORD").GetString());
    }

    private static JsonElement Render(IReadOnlyDictionary<string, string> variables)
    {
        var (exitCode, output, error) = Compose(variables, "config", "--format", "json");
        Assert.True(exitCode == 0, error);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    private static (int ExitCode, string Output, string Error) Compose(
        IReadOnlyDictionary<string, string> variables,
        params string[] arguments)
    {
        var emptyEnvFile = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in (string[])["compose", "--file", ComposeFile, "--env-file", emptyEnvFile, .. arguments])
            {
                start.ArgumentList.Add(argument);
            }

            // Only what the test sets: nothing inherited from whoever runs the suite.
            foreach (var name in new[] { "SCADA_IMAGE_TAG", "SCADA_DB_ADMIN_PASSWORD", "SCADA_APP_DB_PASSWORD",
                                         "SCADA_INITIAL_ADMIN_USERNAME", "SCADA_INITIAL_ADMIN_PASSWORD", "SCADA_HTTP_PORT" })
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
            return (process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            File.Delete(emptyEnvFile);
        }
    }

    internal static string ComposeFile
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScadaDarbox.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "deploy", "docker-compose.yml");
        }
    }

    /// <summary>
    /// Whether the Compose CLI is there, asked through <see cref="ToolProbe"/> so that a CLI that
    /// did not answer in time is not reported as one that is not installed.
    /// </summary>
    internal static readonly ToolAvailability ComposeAvailable = new(
        "The Docker Compose CLI",
        TimeSpan.FromSeconds(30),
        () => ToolProbe.Run("docker", "compose version", TimeSpan.FromSeconds(30)));
}

/// <summary>Reports as skipped, rather than passing, where the Docker Compose CLI is not installed.</summary>
public sealed class RequiresDockerComposeFactAttribute : FactAttribute
{
    public RequiresDockerComposeFactAttribute()
    {
        if (!ComposeFileTests.ComposeAvailable.IsAvailable)
        {
            Skip = ComposeFileTests.ComposeAvailable.Why;
        }
    }
}

/// <summary>The theory form of <see cref="RequiresDockerComposeFactAttribute"/>.</summary>
public sealed class RequiresDockerComposeTheoryAttribute : TheoryAttribute
{
    public RequiresDockerComposeTheoryAttribute()
    {
        if (!ComposeFileTests.ComposeAvailable.IsAvailable)
        {
            Skip = ComposeFileTests.ComposeAvailable.Why;
        }
    }
}
