using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Screen storage (ADR-0008, ADR-0024).</summary>
/// <remarks>
/// Cross-site placement is not checked here. The composite foreign keys
/// <c>(site_id, screen_id) REFERENCES screen (site_id, id)</c> and
/// <c>(site_id, tag_id) REFERENCES tag (site_id, id)</c> make such a row unwritable, so no
/// repository code can forget it — including the code that does not exist yet.
/// </remarks>
public sealed class ScreenRepository : IScreenRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static ScreenRepository() => DapperConfiguration.Ensure();

    public ScreenRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Screen>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var screens = (await connection.QueryAsync<ScreenRow>(
            new CommandDefinition(
                """
                SELECT id, tenant_id, site_id, name, position
                FROM screen_active
                WHERE site_id = @siteId
                ORDER BY position, lower(name)
                """,
                new { siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        if (screens.Count == 0)
        {
            return [];
        }

        // One query for every screen's components rather than one per screen: a Site with a dozen
        // screens would otherwise open a dozen connections to render a list of their names.
        var components = await connection.QueryAsync<ScreenComponentRow>(
            new CommandDefinition(
                """
                SELECT component.id, component.screen_id, component.row_index, component.column_span,
                       component.position, component.kind, component.title, component.tag_id, component.device_id
                FROM screen_component_active component
                JOIN screen_active screen ON screen.id = component.screen_id
                WHERE screen.site_id = @siteId
                ORDER BY component.row_index, component.position
                """,
                new { siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var byScreen = components
            .GroupBy(component => component.ScreenId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ScreenComponent>)group.Select(c => c.ToDomain()).ToList());

        return screens
            .Select(row => row.ToDomain(byScreen.TryGetValue(row.Id, out var onScreen) ? onScreen : []))
            .ToList();
    }

    public async Task<Screen?> FindAsync(Guid screenId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ScreenRow>(
            new CommandDefinition(
                """
                SELECT id, tenant_id, site_id, name, position
                FROM screen_active
                WHERE id = @screenId
                """,
                new { screenId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var components = await connection.QueryAsync<ScreenComponentRow>(
            new CommandDefinition(
                """
                SELECT id, screen_id, row_index, column_span, position, kind, title, tag_id, device_id
                FROM screen_component_active
                WHERE screen_id = @screenId
                ORDER BY row_index, position
                """,
                new { screenId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row.ToDomain(components.Select(component => component.ToDomain()).ToList());
    }

    public async Task AddAsync(Screen screen, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO screen (id, tenant_id, site_id, name, position)
                VALUES (@Id, @TenantId, @SiteId, @Name, @Position)
                """,
                new { screen.Id, screen.TenantId, screen.SiteId, screen.Name, screen.Position },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await InsertComponentsAsync(connection, transaction, screen, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.ScreenTakenAsync(_dataSource, screen.Name, screen.SiteId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(Screen screen, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE screen
                SET name = @Name, position = @Position
                WHERE id = @Id
                """,
                // The screen itself, as the folder repository passes a folder: the parameter names
                // are the object's own, so there is no second list of names to keep in step.
                screen,                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Deleted, not soft-deleted, and this is the one place in this repository where that is
            // right. Soft deletion exists so that a historian sample or an audit row recorded long
            // ago can still resolve a *name* (ADR-0009) -- and a component has no name. It is a row
            // saying "a value component, row 0, spanning 8" and nothing else, so there is no history
            // that could want it back.
            //
            // Soft-deleting would not merely be unnecessary here, it would not work: an author
            // resending a component keeps its id, a soft-deleted row keeps its primary key, and the
            // insert below would then collide with the row it was replacing. That is what the first
            // run of these tests did, as `23505: duplicate key value violates unique constraint
            // "screen_component_pkey"`.
            //
            // Hard deletion is safe for everything pointing at this table: nothing references a
            // component.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM screen_component WHERE screen_id = @Id",
                new { screen.Id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await InsertComponentsAsync(connection, transaction, screen, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.ScreenTakenAsync(_dataSource, screen.Name, screen.SiteId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives a Site the one screen it starts with, and does nothing when it already has one
    /// (ADR-0024 §7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new Site is not born empty, because the first screen a new user saw in this project was
    /// blank and a blank screen is indistinguishable from a broken one — a lesson Phase 6.5's walk
    /// recorded rather than a preference.
    /// </para>
    /// <para>
    /// <b>Nothing here is bound to a tag.</b> A Site with no devices has no tags, so a bound
    /// component would be unreadable from its first moment and would teach a new user that
    /// unreadable tiles are normal. What it gets is a heading naming the Site and the standing
    /// alarms of it, which are the two things that are true before anything is configured.
    /// </para>
    /// <para>
    /// This lives on the repository rather than in the demo seeder so that every path that creates a
    /// Site can call it. There is one such path today and it is the seeder; a Site-creation endpoint
    /// added later must call this too, and that requirement is written here rather than discovered
    /// when a new Site comes up blank.
    /// </para>
    /// </remarks>
    public async Task SeedForSiteAsync(
        Guid tenantId,
        Guid siteId,
        string siteName,
        CancellationToken cancellationToken)
    {
        if ((await GetBySiteAsync(siteId, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            // Idempotent by asking rather than by a fixed id: a seeder that runs twice must not give
            // a Site two screens, and an operator who deleted the seeded screen and built their own
            // must not have it reappear.
            return;
        }

        // The screen's id is decided first, so each component can name it as it is built. The other
        // way round — build the components, then rewrite their ScreenId — is one more place to
        // forget, and the empty Guid it would leave behind is not a value anything should ever see.
        var screenId = Guid.NewGuid();

        var screen = new Screen
        {
            Id = screenId,
            TenantId = tenantId,
            SiteId = siteId,
            Name = "Overview",
            Position = 0,
            Components =
            [
                new ScreenComponent
                {
                    Id = Guid.NewGuid(),
                    ScreenId = screenId,
                    RowIndex = 0,
                    ColumnSpan = 12,
                    Position = 0,
                    Kind = ScreenComponentKinds.Label,
                    Title = siteName,
                },
                new ScreenComponent
                {
                    Id = Guid.NewGuid(),
                    ScreenId = screenId,
                    RowIndex = 1,
                    ColumnSpan = 12,
                    Position = 0,
                    Kind = ScreenComponentKinds.Alarms,
                    // A heading over the alarms, and optional by ADR-0024's kinds table — but supplied,
                    // because a screen called "Overview" whose second row is a bare list reads better
                    // with one and costs nothing. This is NOT why the line exists: the line exists
                    // because this row used to have no title at all and the API refuses to save an
                    // `alarms` component without one, which made every seeded screen unsaveable.
                    Title = "Alarms",
                },
            ],
        };

        // Checked against the API's own rules before it is written, so that what a new Site is born
        // with is a screen the API would also have accepted. A seed that bypasses validation is how
        // the defect above got in: the rules tightened, the seed did not, and nothing said so until
        // an author tried to save a screen they had only opened.
        if (ScreenRules.ProblemWith(screen) is { } problem)
        {
            throw new InvalidOperationException(
                $"The screen seeded for a new Site would be refused by the API: {problem}");
        }

        await AddAsync(screen, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid screenId, CancellationToken cancellationToken)
    {        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The components go with the screen, unlike a folder's contents (ADR-0001 §6): a component
        // has no meaning apart from the screen it is on, so there is nothing for an operator to move
        // out first and no decision about where it would go.
        //
        // Deleted outright rather than soft-deleted, for the reason UpdateAsync gives: a component
        // has no name for a soft-deleted row to preserve (ADR-0009), and it is this table's DELETE
        // grant rather than its UPDATE one that exists to carry a save.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM screen_component WHERE screen_id = @screenId",
            new { screenId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE screen SET deleted_at = now() WHERE id = @screenId AND deleted_at IS NULL",
            new { screenId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertComponentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Screen screen,
        CancellationToken cancellationToken)
    {
        if (screen.Components.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO screen_component
                (id, site_id, screen_id, row_index, column_span, position, kind, title, tag_id, device_id)
            VALUES
                (@Id, @SiteId, @ScreenId, @RowIndex, @ColumnSpan, @Position, @Kind, @Title, @TagId, @DeviceId)
            """,
            screen.Components.Select(component => new
            {
                component.Id,
                // Written from the screen, never from the request: a caller does not get to place a
                // component on a Site, because that is what the composite key exists to prevent.
                SiteId = screen.SiteId,
                component.ScreenId,
                component.RowIndex,
                component.ColumnSpan,
                component.Position,
                component.Kind,
                component.Title,
                component.TagId,
            component.DeviceId,
            }),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
