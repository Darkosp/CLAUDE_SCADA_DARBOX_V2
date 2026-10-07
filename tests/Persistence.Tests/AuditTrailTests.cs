using Npgsql;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Reading the audit trail (ADR-0032): the order, the cursor, the total, the filters, and the actor's name.
/// </summary>
/// <remarks>
/// **Every test writes rows under an action prefix of its own** (`probe-{guid}.…`), so what it counts is what
/// it wrote. The trail is shared with whatever else the host and the fixture have journalled, and a test that
/// asserted a global total would be measuring its neighbours.
/// </remarks>
public sealed class AuditTrailTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public AuditTrailTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_newest_entry_comes_first_and_the_order_survives_rows_written_together()
    {
        var store = Store();
        var prefix = Prefix();

        // Written back to back, so several of these share a millisecond — which is the whole reason the read
        // orders by id rather than by time (ADR-0032 §2). A time-ordered page over these would be free to
        // reorder them between two requests for the same window.
        foreach (var name in new[] { "one", "two", "three" })
        {
            await store.AppendAsync(new AuditEntry(null, $"{prefix}.{name}"), CancellationToken.None);
        }

        var page = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix), CancellationToken.None);

        Assert.Equal(3, page.Total);
        Assert.Equal([$"{prefix}.three", $"{prefix}.two", $"{prefix}.one"], page.Entries.Select(entry => entry.Action));
        Assert.True(page.Entries[0].Id > page.Entries[1].Id);
        Assert.True(page.Entries[1].Id > page.Entries[2].Id);
    }

    [RequiresDatabaseFact]
    public async Task A_page_says_how_many_rows_the_same_filters_match()
    {
        var store = Store();
        var prefix = Prefix();

        for (var index = 0; index < 5; index++)
        {
            await store.AppendAsync(new AuditEntry(null, $"{prefix}.{index}"), CancellationToken.None);
        }

        var page = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix, Limit: 2), CancellationToken.None);

        // Two entries, five matching: the page is short and says so, which is the difference between "the
        // newest 2 of 5" and a quiet night (ADR-0032 §3).
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(5, page.Total);
    }

    [RequiresDatabaseFact]
    public async Task The_cursor_walks_backwards_without_repeating_or_skipping_a_row()
    {
        var store = Store();
        var prefix = Prefix();

        for (var index = 0; index < 5; index++)
        {
            await store.AppendAsync(new AuditEntry(null, $"{prefix}.{index}"), CancellationToken.None);
        }

        var seen = new List<long>();
        long? before = null;

        // **Bounded on purpose.** The first version of this test looped until a page came back empty — which
        // is correct only while the cursor is correct, so the mutation that breaks it (`id <= before`) does
        // not fail this test, it hangs the suite forever. A test that cannot finish is not a test that
        // catches anything, and a mutation is supposed to leave a named failure behind.
        for (var page = 0; page < 10; page++)
        {
            var read = await store.ReadAsync(
                new AuditTrailQuery(ActionPrefix: prefix, BeforeId: before, Limit: 2),
                CancellationToken.None);

            if (read.Entries.Count == 0)
            {
                break;
            }

            seen.AddRange(read.Entries.Select(entry => entry.Id));

            Assert.True(
                before is null || read.Entries[^1].Id < before,
                $"the cursor did not advance: page {page} ended at {read.Entries[^1].Id} with before={before}");

            before = read.Entries[^1].Id;
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // And descending throughout, which is what makes the cursor safe to hand back.
        Assert.Equal(seen.OrderByDescending(id => id), seen);
    }

    [RequiresDatabaseFact]
    public async Task An_action_prefix_matches_the_family_and_underscore_is_not_a_wildcard()
    {
        var store = Store();
        var prefix = Prefix();

        await store.AppendAsync(new AuditEntry(null, $"{prefix}.login"), CancellationToken.None);
        await store.AppendAsync(new AuditEntry(null, $"{prefix}.login_failed"), CancellationToken.None);
        await store.AppendAsync(new AuditEntry(null, $"{prefix}Xelsewhere"), CancellationToken.None);

        var family = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: $"{prefix}."), CancellationToken.None);

        Assert.Equal(2, family.Total);
        Assert.DoesNotContain(family.Entries, entry => entry.Action.Contains("elsewhere"));

        // The trap this exists for: under `LIKE @action || '%'` the `_` is "any one character", so this
        // filter would quietly match `{prefix}Xelsewhere` and answer a wider question than the one typed.
        var underscore = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: $"{prefix}_"), CancellationToken.None);

        Assert.Equal(0, underscore.Total);
    }

    [RequiresDatabaseFact]
    public async Task A_deactivated_actor_still_reads_as_the_person_it_was()
    {
        var store = Store();
        var prefix = Prefix();
        var (tenantId, userId, username) = await SeedDeactivatedUserAsync();

        await store.AppendAsync(
            new AuditEntry(userId, $"{prefix}.bygone", "user", userId),
            CancellationToken.None);

        var page = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix), CancellationToken.None);
        var entry = Assert.Single(page.Entries);

        // Reading the actor through `app_user_active` (ADR-0009) would print this as nobody — and an
        // ex-employee's actions are exactly what an investigation is looking for (ADR-0032 §5).
        Assert.Equal(userId, entry.ActorUserId);
        Assert.Equal(username, entry.ActorUsername);
        Assert.NotEqual(Guid.Empty, tenantId);
    }

    [RequiresDatabaseFact]
    public async Task An_entry_with_no_actor_comes_back_and_says_so()
    {
        var store = Store();
        var prefix = Prefix();

        // A failed sign-in names no account at all (ADR-0011's own example), so the read has to carry a null
        // actor rather than drop the row or invent one.
        await store.AppendAsync(new AuditEntry(null, $"{prefix}.nobody"), CancellationToken.None);

        var entry = Assert.Single((await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix), CancellationToken.None)).Entries);

        Assert.Null(entry.ActorUserId);
        Assert.Null(entry.ActorUsername);
    }

    [RequiresDatabaseFact]
    public async Task The_window_narrows_and_the_detail_comes_back_untouched()
    {
        var store = Store();
        var prefix = Prefix();

        await store.AppendAsync(new AuditEntry(null, $"{prefix}.old"), CancellationToken.None);
        await Task.Delay(20);
        await store.AppendAsync(
            new AuditEntry(null, $"{prefix}.new", Detail: new Dictionary<string, object?> { ["nested"] = new { a = 1 } }),
            CancellationToken.None);

        var all = await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix), CancellationToken.None);
        Assert.Equal(2, all.Total);

        var newest = all.Entries[0];
        var older = all.Entries[1];

        // **The boundaries come from the rows, not from this process's clock.** `occurred_at` is the
        // database's `now()`, and comparing it against a timestamp taken here assumes the two clocks agree —
        // which is the assumption this project refuses everywhere else (ADR-0017 journals a source's clock
        // skew rather than pretending it has none). The first version of this test did exactly that and read
        // both rows as newer than "now".
        var olderOnly = await store.ReadAsync(
            new AuditTrailQuery(ActionPrefix: prefix, ToUtc: newest.OccurredAtUtc),
            CancellationToken.None);

        Assert.Equal($"{prefix}.old", Assert.Single(olderOnly.Entries).Action);
        Assert.True(olderOnly.Entries[0].Id < newest.Id);

        // And `from` is inclusive, which is what makes the pair of them a window rather than a gap.
        var fromOlder = await store.ReadAsync(
            new AuditTrailQuery(ActionPrefix: prefix, FromUtc: older.OccurredAtUtc),
            CancellationToken.None);

        Assert.Equal(2, fromOlder.Total);

        var entry = Assert.Single(
            (await store.ReadAsync(new AuditTrailQuery(ActionPrefix: prefix, FromUtc: newest.OccurredAtUtc), CancellationToken.None)).Entries);
        Assert.Equal($"{prefix}.new", entry.Action);

        // **Text, not a parsed object** (ADR-0032 §6): the reader must not understand the writer's document,
        // or the two drift and the old entries are the ones that stop meaning what they meant.
        Assert.Contains("nested", entry.DetailJson);
        Assert.IsType<string>(entry.DetailJson);
    }

    private SecurityStore Store() => new(_database.ApplicationDataSource);

    private static string Prefix() => $"probe-{Guid.NewGuid():N}";

    private async Task<(Guid TenantId, Guid UserId, string Username)> SeedDeactivatedUserAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var username = $"bygone-{Guid.NewGuid():N}"[..20];

        await using var connection = new NpgsqlConnection(_database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO tenant (id, name) VALUES ('{tenantId}', 'Audit tenant');
            INSERT INTO app_user (id, tenant_id, username, password_hash, is_admin, deleted_at)
            VALUES ('{userId}', '{tenantId}', '{username}', 'not-a-real-hash', false, now());
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        return (tenantId, userId, username);
    }
}
