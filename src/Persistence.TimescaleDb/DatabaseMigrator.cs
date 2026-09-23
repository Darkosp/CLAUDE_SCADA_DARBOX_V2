using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Applies the numbered SQL migrations under <c>migrations/</c> (ADR-0007).
/// </summary>
/// <remarks>
/// Scripts run in name order and are recorded in DbUp's journal table, so re-running
/// is a no-op for anything already applied. A script that has been applied is never
/// edited — changing course means adding a new numbered script, the same discipline
/// the ADRs themselves use.
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>
    /// How long a run waits for another to finish before giving up. Longer than any migration
    /// this project has, and short enough that a hung run or a crossed deployment shows up as
    /// a failed migrator within minutes. A deployment with a longer migration raises it with
    /// <c>SCADA_MIGRATOR_LOCK_TIMEOUT_SECONDS</c>.
    /// </summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A whole migrator run (ADR-0012): brings the database up to date, creating it from
    /// nothing if necessary, then lets the application role log in with
    /// <paramref name="applicationPassword"/> — all under the migration lock (ADR-0014), so a
    /// second run arriving meanwhile waits and then finds nothing to do.
    /// </summary>
    /// <exception cref="MigrationLockTimeoutException">
    /// Another run held the database for longer than <paramref name="lockTimeout"/>. Nothing
    /// was applied.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A migration failed. The Gateway must not serve against a schema in an unknown state, so
    /// this is thrown rather than logged and swallowed.
    /// </exception>
    public static Task RunAsync(
        string connectionString,
        string applicationPassword,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken) =>
        RunAsync(connectionString, applicationPassword, lockTimeout, [], cancellationToken);

    /// <param name="additionalScripts">
    /// Scripts applied as if the build carried them — for tests that need a migration this
    /// build does not have, such as a data script meeting a second run.
    /// </param>
    internal static async Task RunAsync(
        string connectionString,
        string applicationPassword,
        TimeSpan lockTimeout,
        IReadOnlyList<(string Name, string Sql)> additionalScripts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationPassword);

        // Taken before DbUp reads its journal, and held through the password step: the whole
        // run is one run.
        await using var held = await MigrationLock.AcquireAsync(connectionString, lockTimeout, cancellationToken)
            .ConfigureAwait(false);

        Migrate(connectionString, additionalScripts);
        await ApplicationRole.SetPasswordAsync(connectionString, applicationPassword, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Migrate(string connectionString, IReadOnlyList<(string Name, string Sql)> additionalScripts)
    {
        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithScripts(additionalScripts.Select(script => new SqlScript(script.Name, script.Sql)))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed on script '{result.ErrorScript?.Name ?? "unknown"}'.",
                result.Error);
        }
    }

    /// <summary>The scripts this assembly carries, in the order they would be applied.</summary>
    public static IReadOnlyList<string> ScriptNames() =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
}
