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

/// <summary>
/// The screen a new Site is born with (ADR-0024 §5), for a Site the seeder's own statement did not
/// name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by walking the gate, and it is a gap in the rule rather than in the code that implements
/// it.</b> The seeder listed its Sites by hand — a call to <c>SeedForSiteAsync</c> each for Skopje and
/// for Bitola — so "a new Site is not born empty" held only for as long as nobody added a third Site
/// to the statement above them. A third Site would have been seeded by the database and given no
/// screen, and nothing would have said so: the operator would have found it, which is exactly what
/// the rule exists to prevent.
/// </para>
/// <para>
/// <b>The two properties are checked together and in this order</b>, because the second is what makes
/// the first safe on an upgrade: a Site with no screen gets one, and a Site that has one is left
/// alone.
/// </para>
/// </remarks>
public sealed class SiteScreenSeedingTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public SiteScreenSeedingTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task A_Site_the_seeder_did_not_name_gets_a_screen_and_one_that_has_one_is_left_alone()
    {
        var dataSource = _database.DataSource;

        // The demo dataset, which is what gives us a tenant and two Sites the seeder DID name.
        await DemoConfigurationSeeder.SeedIfEmptyAsync(
            dataSource, "127.0.0.1", 502, CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);

        var tenantId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM tenant LIMIT 1");

        // A third Site, added the way a later release would add one: by writing the row, which is all
        // the seeder's own statement would have done.
        var third = Guid.NewGuid();
        await connection.ExecuteAsync(
            "INSERT INTO site (id, tenant_id, name, time_zone_id) VALUES (@id, @tenant, 'Ohrid', 'Europe/Skopje')",
            new { id = third, tenant = tenantId });

        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM screen WHERE site_id = @site", new { site = third }));

        await DemoConfigurationSeeder.SeedScreensAsync(
            dataSource, tenantId, [(third, "Ohrid")], CancellationToken.None);

        // It exists, it is named, and it carries the Site's own name as its label -- which is the whole
        // of what makes a first screen worth looking at rather than merely present.
        var name = await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM screen WHERE site_id = @site", new { site = third });
        Assert.Equal("Overview", name);

        var label = await connection.ExecuteScalarAsync<string>(
            """
            SELECT c.title FROM screen_component c
            JOIN screen s ON s.id = c.screen_id
            WHERE s.site_id = @site AND c.kind = 'label'
            """,
            new { site = third });
        Assert.Equal("Ohrid", label);

        // And the second property: running it again changes nothing, which is what makes this safe to
        // call on every start rather than only against an empty database.
        await DemoConfigurationSeeder.SeedScreensAsync(
            dataSource, tenantId, [(third, "Ohrid")], CancellationToken.None);

        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM screen WHERE site_id = @site", new { site = third }));
    }
}
