using System.Text.Json;
using Npgsql;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0012's criteria: the Gateway never migrates, refuses a schema that is not exactly its
/// own, and runs only as the application role.
/// </summary>
/// <remarks>
/// A refusal here is an exception out of <see cref="GatewayApp.BuildAsync"/>, before a host
/// exists — so nothing is listening, and nothing can be served.
/// </remarks>
public sealed class StartupCheckTests
{
    [RequiresDatabaseFact]
    public async Task A_journal_behind_the_build_is_refused_naming_both_versions()
    {
        // Derived from the build rather than written down, so the test stays true as
        // migrations are added: the database is one script behind whatever is newest.
        var scripts = DatabaseMigrator.ScriptNames();
        var newest = scripts[^1];

        await using var database = await ScratchDatabase.CreateMigratedAsync();
        await database.ExecutePrivilegedAsync($"DELETE FROM schemaversions WHERE scriptname = '{newest}'");

        var refusal = await Assert.ThrowsAsync<SchemaVersionMismatchException>(
            () => GatewayApp.BuildAsync(database.ApplicationArgs()));

        Assert.Contains($"Expected version: {VersionOf(newest)}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"database version: {VersionOf(scripts[^2])}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Run the migrator", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A script's version as the refusal names it: the file name without its prefix or extension.</summary>
    private static string VersionOf(string scriptName) =>
        scriptName["ScadaDarbox.Persistence.TimescaleDb.migrations.".Length..^".sql".Length];

    [RequiresDatabaseFact]
    public async Task A_journal_ahead_of_the_build_is_refused_too()
    {
        // The case "is not behind" would let through: this build has never heard of the
        // newer script, and whatever it changed.
        await using var database = await ScratchDatabase.CreateMigratedAsync();
        await database.ExecutePrivilegedAsync(
            """
            INSERT INTO schemaversions (scriptname, applied)
            VALUES ('ScadaDarbox.Persistence.TimescaleDb.migrations.9999_from_a_newer_build.sql', now())
            """);

        var refusal = await Assert.ThrowsAsync<SchemaVersionMismatchException>(
            () => GatewayApp.BuildAsync(database.ApplicationArgs()));

        Assert.Contains($"Expected version: {VersionOf(DatabaseMigrator.ScriptNames()[^1])}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("database version: 9999_from_a_newer_build", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("older than the database", refusal.Message, StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task A_connection_that_could_rewrite_the_audit_log_is_refused()
    {
        await using var database = await ScratchDatabase.CreateMigratedAsync();

        var refusal = await Assert.ThrowsAsync<UnsafeDatabaseRoleException>(
            () => GatewayApp.BuildAsync(
                [$"--ConnectionStrings:ScadaDb={database.PrivilegedConnectionString}", "--urls=http://127.0.0.1:0"]));

        Assert.Contains("'scada'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_configuration_holds_no_privileged_credential()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var connection = new NpgsqlConnectionStringBuilder(
            settings.RootElement.GetProperty("ConnectionStrings").GetProperty("ScadaDb").GetString());

        Assert.Equal(ApplicationRole.Name, connection.Username);
        Assert.True(string.IsNullOrEmpty(connection.Password));
    }
}
