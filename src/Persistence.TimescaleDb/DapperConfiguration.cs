using Dapper;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Dapper setup shared by every repository in this assembly (ADR-0008).
/// </summary>
internal static class DapperConfiguration
{
    /// <summary>
    /// Makes Dapper match snake_case columns to PascalCase properties.
    /// </summary>
    /// <remarks>
    /// Called from each repository's static constructor rather than a module
    /// initializer: the setting is process-global to Dapper, so it is applied when this
    /// assembly's own types are first used, not as a side effect of the assembly being
    /// loaded next to somebody else's Dapper code. It is idempotent.
    /// </remarks>
    internal static void Ensure() => DefaultTypeMap.MatchNamesWithUnderscores = true;
}
