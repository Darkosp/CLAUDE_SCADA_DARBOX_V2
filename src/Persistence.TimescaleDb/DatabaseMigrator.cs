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
    /// Brings the database up to date, creating it from nothing if necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A migration failed. The host must not continue serving against a schema in an
    /// unknown state, so this is thrown rather than logged and swallowed.
    /// </exception>
    public static void Migrate(string connectionString)
    {
        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
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
