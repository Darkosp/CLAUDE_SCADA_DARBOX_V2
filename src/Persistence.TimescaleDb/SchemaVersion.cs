using Npgsql;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// The Gateway's side of ADR-0012: it no longer migrates, so it checks instead.
/// </summary>
public static class SchemaVersion
{
    private const string UndefinedTable = "42P01";
    private const string ResourcePrefix = "ScadaDarbox.Persistence.TimescaleDb.migrations.";

    /// <summary>
    /// Refuses to continue unless the database's migration journal records exactly the
    /// scripts this build carries.
    /// </summary>
    /// <remarks>
    /// Exact, not merely "at least": ADR-0012's decision is that the schema must be at the
    /// version the build expects. A journal that is ahead means this build is older than
    /// the database it is pointed at, and would be serving against tables it has never
    /// heard of — no safer than serving against ones that do not exist yet. Only reads the
    /// journal, so it works over the unprivileged application role.
    /// </remarks>
    /// <exception cref="SchemaVersionMismatchException">The schema is not the one this build expects.</exception>
    /// <exception cref="NoMigrationScriptsException">
    /// The build carries no scripts, so there is nothing to compare against: an empty build and a
    /// never-migrated database would otherwise match (ADR-0014).
    /// </exception>
    public static Task EnsureCurrentAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
        EnsureCurrentAsync(dataSource, typeof(DatabaseMigrator).Assembly, cancellationToken);

    /// <param name="scriptSource">The assembly carrying the migration scripts; a test may stand in one without any.</param>
    internal static async Task EnsureCurrentAsync(
        NpgsqlDataSource dataSource,
        System.Reflection.Assembly scriptSource,
        CancellationToken cancellationToken)
    {
        var expected = DatabaseMigrator.ScriptNames(scriptSource);
        NoMigrationScriptsException.ThrowIfEmpty(expected, scriptSource);

        var applied = await AppliedScriptsAsync(dataSource, cancellationToken).ConfigureAwait(false);

        if (!expected.SequenceEqual(applied, StringComparer.Ordinal))
        {
            throw new SchemaVersionMismatchException(
                expected.Select(Short).ToList(),
                applied.Select(Short).ToList());
        }
    }

    /// <summary>
    /// Refuses to continue if this connection could rewrite the audit trail.
    /// </summary>
    /// <remarks>
    /// ADR-0011's append-only guarantee is a missing grant, which a superuser or the
    /// tables' owner simply does not need. A Gateway started with the migrator's credential
    /// would run happily and quietly make that guarantee worthless, so it is checked rather
    /// than left to configuration.
    /// </remarks>
    public static async Task EnsureUnprivilegedAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT current_user,
                   has_table_privilege('audit_log', 'UPDATE') OR has_table_privilege('audit_log', 'DELETE')
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        var user = reader.GetString(0);
        if (reader.GetBoolean(1))
        {
            throw new UnsafeDatabaseRoleException(user);
        }
    }

    private static async Task<IReadOnlyList<string>> AppliedScriptsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT scriptname FROM schemaversions");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var names = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }

            return names.Order(StringComparer.Ordinal).ToList();
        }
        catch (PostgresException exception) when (exception.SqlState == UndefinedTable)
        {
            // No journal at all: the migrator has never run against this database.
            return [];
        }
    }

    private static string Short(string scriptName)
    {
        var name = scriptName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            ? scriptName[ResourcePrefix.Length..]
            : scriptName;

        return name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>The Gateway's database connection holds more privilege than it may.</summary>
public sealed class UnsafeDatabaseRoleException(string user) : Exception(
    $"The Gateway is connected to the database as '{user}', which can rewrite the audit log. " +
    $"Connect as the application role '{ApplicationRole.Name}' instead; the privileged " +
    "credential belongs to the migrator only (ADR-0011, ADR-0012).");

/// <summary>The database would not let the Gateway log in.</summary>
/// <remarks>
/// The application role gets its login and its password from the migrator (ADR-0012), so the
/// usual cause is a migrator that has not run, or was given a different password. Says so
/// rather than surfacing as an unhandled exception: ADR-0012 promises a startup failure that is
/// loud, early and says what is wrong. Never quotes the password.
/// </remarks>
public sealed class DatabaseLoginRefusedException(string user, PostgresException inner) : Exception(
    $"The database refused the Gateway's login as '{user}' ({inner.SqlState}: {inner.MessageText}). " +
    $"The migrator creates the application role '{ApplicationRole.Name}' and sets its password from " +
    "SCADA_APP_DB_PASSWORD: has the migrator run against this database, and were the migrator and " +
    "the Gateway given the same SCADA_APP_DB_PASSWORD?",
    inner)
{
    private const string InvalidPassword = "28P01";
    private const string InvalidAuthorization = "28000";

    /// <summary>Whether the server turned the login down, as opposed to failing some other way.</summary>
    public static bool IsLoginRefusal(PostgresException exception) =>
        exception.SqlState is InvalidPassword or InvalidAuthorization;
}

/// <summary>The database schema is not the one this build was made for.</summary>
public sealed class SchemaVersionMismatchException : Exception
{
    public SchemaVersionMismatchException(IReadOnlyList<string> expected, IReadOnlyList<string> applied)
        : base(Describe(expected, applied))
    {
        Expected = expected;
        Applied = applied;
    }

    public IReadOnlyList<string> Expected { get; }

    public IReadOnlyList<string> Applied { get; }

    private static string Describe(IReadOnlyList<string> expected, IReadOnlyList<string> applied)
    {
        var missing = expected.Except(applied, StringComparer.Ordinal).ToList();
        var unknown = applied.Except(expected, StringComparer.Ordinal).ToList();

        var message =
            "The database schema is not at the version this build expects. " +
            $"Expected version: {expected.LastOrDefault() ?? "none"}; " +
            $"database version: {applied.LastOrDefault() ?? "none (never migrated)"}.";

        if (missing.Count > 0)
        {
            message += $" Not yet applied: {string.Join(", ", missing)}. Run the migrator before starting the Gateway.";
        }

        if (unknown.Count > 0)
        {
            message += $" Applied but unknown to this build: {string.Join(", ", unknown)} — this build is older than the database.";
        }

        return message;
    }
}
