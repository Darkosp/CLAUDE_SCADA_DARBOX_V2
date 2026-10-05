using Dapper;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The demo seeder starting more than once at the same time.
/// </summary>
/// <remarks>
/// <para>
/// This is the race that was found by the Gateway's own test host, which starts the app once per
/// test in parallel against one database — a much better double-start generator than a deployment
/// is, and the reason it was noticed at all. It is recorded in <c>open-work.md</c> §2.0c as found and
/// deliberately unfixed at the time; this file is the fix.
/// </para>
/// <para>
/// <b>The test asserts two things, and the second is the one that matters.</b> That the demo dataset
/// exists once is obvious. That no seeder <i>failed</i> is the real assertion: every id in the seed
/// is a fixed constant, so two seeders that both got past their own question collide on the tenant's
/// primary key. Before the lock this test failed with <c>23505</c> rather than with a duplicate row,
/// and a test that only counted rows would have passed on a run where one of the two had not yet
/// committed.
/// </para>
/// <para>
/// <b>One test method, not two.</b> A fixture gives one database to the whole class and xUnit does
/// not promise an order, so a second method asserting "the seeder leaves an already-seeded database
/// alone" could run first and make the first method's <c>count == 0</c> false. Both properties are
/// checked in one method, in the order they have to happen in.
/// </para>
/// <para>
/// Eight at once rather than two, because the window between the question and the insert is short and
/// the point is to reproduce it rather than to hope.
/// </para>
/// </remarks>
public sealed class DemoSeederConcurrencyTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public DemoSeederConcurrencyTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task Starting_the_seeder_eight_times_at_once_seeds_once_and_a_later_run_changes_nothing()
    {
        // A database this fixture has not been seeded into: the demo seeder is what a Gateway does at
        // startup when it finds nothing, and this one is empty.
        var dataSource = _database.DataSource;

        Assert.Equal(0, await CountAsync(dataSource, "tenant"));

        using var start = new Barrier(8);

        var seeders = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            // Released together, so the eight questions are asked as close to simultaneously as the
            // machine allows. A barrier rather than a loop: eight tasks that happen to start within
            // a few milliseconds of each other is not the same as eight that start now.
            start.SignalAndWait();
            await DemoConfigurationSeeder.SeedIfEmptyAsync(dataSource, "127.0.0.1", 502, CancellationToken.None);
        }));

        // Any of them throwing fails here, which is the assertion that a second seeder did not try to
        // insert ids the first had already used.
        await Task.WhenAll(seeders);

        Assert.Equal(1, await CountAsync(dataSource, "tenant"));
        Assert.Equal(2, await CountAsync(dataSource, "site"));
        Assert.Equal(1, await CountAsync(dataSource, "device"));
        Assert.Equal(2, await CountAsync(dataSource, "tag"));

        // And the screens ADR-0024 7 gives a new Site, once each rather than once per seeder.
        Assert.Equal(2, await CountAsync(dataSource, "screen"));

        // Then an ordinary later run, which is what an upgrade is: nothing changes, and in
        // particular the operator's own screens are not joined by a second seeded one.
        var screens = await CountAsync(dataSource, "screen");

        await DemoConfigurationSeeder.SeedIfEmptyAsync(dataSource, "127.0.0.1", 502, CancellationToken.None);
        await DemoConfigurationSeeder.SeedIfEmptyAsync(dataSource, "127.0.0.1", 502, CancellationToken.None);

        Assert.Equal(1, await CountAsync(dataSource, "tenant"));
        Assert.Equal(screens, await CountAsync(dataSource, "screen"));
    }

    private static async Task<long> CountAsync(Npgsql.NpgsqlDataSource dataSource, string table)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);

        // The table name is a literal in this file and never a caller's, so it is interpolated
        // rather than parameterised -- a parameter cannot be an identifier, and swallowing that
        // limitation with string concatenation from anywhere else would be the injection this
        // avoids by keeping the list closed.
        return await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}");
    }
}
