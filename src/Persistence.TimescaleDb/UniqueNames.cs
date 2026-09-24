using Npgsql;
using ScadaDarbox.Core.Configuration;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Turns a violation of ADR-0015's name indexes into a refusal the caller can show.
/// </summary>
/// <remarks>
/// The database is what enforces the rule (migration 0010) — no code path can bypass an index.
/// This only puts it into words: which kind of thing, under what name, already exists. Any
/// other unique violation is left alone, because calling it a name clash would mislead.
/// </remarks>
internal static class UniqueNames
{
    private const string UniqueViolation = "23505";

    private const string Explanation = "Names are unique within their parent, ignoring case (ADR-0015).";

    /// <summary>
    /// The refusal for <paramref name="exception"/> if it is one of the name indexes; otherwise null.
    /// </summary>
    /// <param name="device">The device name the write used, if it wrote one.</param>
    /// <param name="folder">The folder name the write used, if it wrote one.</param>
    /// <param name="tag">The tag name the write used, if it wrote exactly one.</param>
    public static ConfigurationConflictException? Conflict(
        PostgresException exception,
        string? device = null,
        string? folder = null,
        string? tag = null)
    {
        if (exception.SqlState != UniqueViolation)
        {
            return null;
        }

        var message = exception.ConstraintName switch
        {
            "ux_device_name_in_parent" => $"A device {Named(device)}already exists in the same folder, or directly under the same Site.",
            "ux_folder_name_in_parent" => $"A folder {Named(folder)}already exists in the same place.",
            "ux_tag_name_in_device" => $"A tag {Named(tag)}already exists on this device.",
            _ => null,
        };

        return message is null ? null : new ConfigurationConflictException($"{message} {Explanation}");
    }

    private static string Named(string? name) => name is null ? "with the same name " : $"named '{name}' ";
}
