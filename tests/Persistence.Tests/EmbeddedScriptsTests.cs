using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;
using Xunit.Abstractions;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0014's second finding: a build can carry no migration scripts at all, and then both
/// ADR-0012 guarantees pass while nothing has been migrated.
/// </summary>
/// <remarks>
/// The scripts are embedded by a glob that a case-sensitive build (Linux, so every container
/// image) matches against the folder's name exactly. A mismatch embeds nothing, the migrator
/// "succeeds" at once, and the Gateway's check compares an empty build with an empty journal.
/// A case-insensitive working copy never shows it.
/// </remarks>
public sealed class EmbeddedScriptsTests
{
    /// <summary>
    /// The resource-name prefix every existing journal has recorded. Pinned here rather than
    /// derived: a build embedding the same files under another name would re-apply every script
    /// to a database that already has them.
    /// </summary>
    private const string JournalPrefix = "ScadaDarbox.Persistence.TimescaleDb.migrations.";

    private readonly ITestOutputHelper _output;

    public EmbeddedScriptsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Every_migration_script_in_the_source_tree_is_embedded_under_the_name_the_journal_knows()
    {
        var folder = MigrationsFolder();
        var onDisk = Directory.GetFiles(folder)
            .Where(file => file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(file => JournalPrefix + Path.GetFileName(file))
            .Order(StringComparer.Ordinal)
            .ToList();

        var embedded = DatabaseMigrator.ScriptNames();
        _output.WriteLine($"{onDisk.Count} .sql files in '{folder}'; {embedded.Count} embedded.");

        // Something to compare, so equality below cannot be empty against empty.
        Assert.NotEmpty(onDisk);
        Assert.Equal(onDisk, embedded);
    }

    [RequiresDatabaseFact]
    public async Task A_migrator_built_without_scripts_refuses_and_leaves_the_database_untouched()
    {
        var name = await TestDatabase.CreateEmptyAsync();
        try
        {
            var connectionString = TestDatabase.ConnectionStringFor(name);

            // This test assembly carries no .sql resources: it stands in for a build that
            // embedded none.
            var run = DatabaseMigrator.RunAsync(
                connectionString,
                TestDatabase.ApplicationPassword,
                DatabaseMigrator.DefaultLockTimeout,
                [],
                CancellationToken.None,
                scriptSource: typeof(EmbeddedScriptsTests).Assembly);

            // Without the refusal this run succeeds — the failure this guards against.
            var refusal = await Assert.ThrowsAsync<NoMigrationScriptsException>(() => run);
            Assert.Contains("carries no migration scripts", refusal.Message, StringComparison.Ordinal);

            // Refused before touching the database: not even a journal was created.
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var journal = dataSource.CreateCommand("SELECT to_regclass('schemaversions') IS NULL");
            Assert.True((bool)(await journal.ExecuteScalarAsync())!);
        }
        finally
        {
            await TestDatabase.DropEmptyAsync(name);
        }
    }

    [RequiresDatabaseFact]
    public async Task The_gateway_check_refuses_a_build_without_scripts_against_a_database_never_migrated()
    {
        // The pair that would otherwise pass: nothing expected, nothing applied.
        var name = await TestDatabase.CreateEmptyAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(TestDatabase.ConnectionStringFor(name));

            var check = SchemaVersion.EnsureCurrentAsync(
                dataSource, typeof(EmbeddedScriptsTests).Assembly, CancellationToken.None);

            var refusal = await Assert.ThrowsAsync<NoMigrationScriptsException>(() => check);
            Assert.Contains("carries no migration scripts", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            await TestDatabase.DropEmptyAsync(name);
        }
    }

    /// <summary>
    /// The scripts' folder in the source tree, found whatever its capitalisation — the test is
    /// about what the build did with it, so it must not fail to find it for the same reason.
    /// </summary>
    private static string MigrationsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScadaDarbox.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var project = Path.Combine(directory.FullName, "src", "Persistence.TimescaleDb");

        var folders = Directory.GetDirectories(project)
            .Where(path => string.Equals(Path.GetFileName(path), "migrations", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Assert.Single(folders);
    }
}
