using System.Diagnostics;
using Npgsql;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0012's promise that a Gateway which cannot run fails loudly, early, and says what is
/// wrong — for the case where it cannot even log in.
/// </summary>
/// <remarks>
/// Found walking Phase 6 in Compose: started ahead of the migrator on an empty database, the
/// Gateway never reached its schema check. The application role had no password yet, the login
/// failed, and the process died on an unhandled exception with a stack trace.
/// </remarks>
public sealed class DatabaseLoginRefusalTests
{
    private const string WrongPassword = "not-the-application-password-3f8a";

    [RequiresDatabaseFact]
    public async Task A_wrong_password_is_refused_asking_whether_the_migrator_has_run()
    {
        await using var database = await ScratchDatabase.CreateMigratedAsync();

        var refusal = await Assert.ThrowsAsync<DatabaseLoginRefusedException>(
            () => GatewayApp.BuildAsync(ArgsFor(database, ApplicationRole.Name, WrongPassword)));

        Assert.Equal("28P01", ((PostgresException)refusal.InnerException!).SqlState);
        Assert.Contains($"login as '{ApplicationRole.Name}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("has the migrator run against this database", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongPassword, refusal.Message, StringComparison.Ordinal);
    }

    [RequiresDatabaseFact]
    public async Task A_role_that_may_not_log_in_is_refused_the_same_way()
    {
        // The application role before the migrator's password step: it exists, NOLOGIN.
        // A role of its own, because scada_app belongs to the whole server and other tests
        // are using it right now.
        await using var database = await ScratchDatabase.CreateMigratedAsync();
        var role = $"scada_nologin_{Guid.NewGuid():N}";
        await database.ExecutePrivilegedAsync($"CREATE ROLE {role} NOLOGIN PASSWORD '{WrongPassword}'");

        try
        {
            var refusal = await Assert.ThrowsAsync<DatabaseLoginRefusedException>(
                () => GatewayApp.BuildAsync(ArgsFor(database, role, WrongPassword)));

            Assert.Equal("28000", ((PostgresException)refusal.InnerException!).SqlState);
            Assert.Contains("has the migrator run against this database", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            await database.ExecutePrivilegedAsync($"DROP ROLE IF EXISTS {role}");
        }
    }

    [RequiresDatabaseFact]
    public async Task The_gateway_process_prints_the_refusal_and_exits_1_rather_than_crashing()
    {
        // The whole process, so Program.cs is under test too: the refusal has to be caught
        // and printed there, not only thrown by BuildAsync.
        await using var database = await ScratchDatabase.CreateMigratedAsync();
        var directory = AppContext.BaseDirectory;

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(directory, "ScadaDarbox.Gateway.dll"));
        foreach (var argument in ArgsFor(database, ApplicationRole.Name, WrongPassword))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(GatewayApp.AppPasswordVariable);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The Gateway neither started nor exited within a minute.");
        }

        var printed = await error + await output;

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("has the migrator run against this database", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongPassword, printed, StringComparison.Ordinal);
    }

    private static string[] ArgsFor(ScratchDatabase database, string user, string password) =>
    [
        $"--ConnectionStrings:ScadaDb={new NpgsqlConnectionStringBuilder(database.PrivilegedConnectionString) { Username = user, Password = password }}",
        "--urls=http://127.0.0.1:0",
        // Declared so the login refusal is what this process prints, not the transport one
        // (ADR-0028): the transport check runs first, before anything needs a database.
        "--Server:TlsTerminatedUpstream=true",
    ];
}
