using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0013 through the running Gateway: alarm state survives a restart, and a shelf
/// always ends.
/// </summary>
public sealed class AlarmJournalHostTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;
    private static readonly Guid Skopje = DemoConfigurationSeeder.SiteId;

    private readonly GatewayTestHost _host;

    public AlarmJournalHostTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task An_acknowledged_alarm_survives_a_restart_with_who_acknowledged_it()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Restart probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));

        using (var asOperator = _host.CreateClient(@operator.Token))
        {
            using var acknowledged = await asOperator.PostAsync($"/api/alarms/{definitionId}/acknowledge", content: null);
            Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);
        }

        // Control: acknowledged before the restart, so what is read afterwards is the rebuild
        // at work rather than a state the alarm never left.
        var before = await AlarmAsync(admin, definitionId);
        Assert.Equal("Acknowledged", before.GetProperty("state").GetString());

        await _host.RestartAsync();

        // Read immediately, before anything has been scanned: the probe still reads above
        // its threshold, so an engine that forgot the alarm would raise a fresh Active one
        // with a new occurrence once scanning began.
        var after = await AlarmAsync(admin, definitionId);
        Assert.Equal("Acknowledged", after.GetProperty("state").GetString());
        Assert.Equal(@operator.Username, after.GetProperty("acknowledgedBy").GetString());
        Assert.Equal(
            before.GetProperty("occurrenceId").GetGuid(),
            after.GetProperty("occurrenceId").GetGuid());
        // To the microsecond, which is what timestamptz keeps; .NET's clock has ten times that.
        Assert.Equal(
            ToMicroseconds(before.GetProperty("acknowledgedAtUtc").GetDateTimeOffset()),
            ToMicroseconds(after.GetProperty("acknowledgedAtUtc").GetDateTimeOffset()));

        // And the outage is on record: a clean stop, then a start whose gap is bounded.
        var engineEvents = await EngineEventsAsync();
        var stopped = engineEvents.FindLastIndex(e => e.Type == "EvaluationStopped");
        Assert.True(stopped >= 0, "No EvaluationStopped was recorded.");
        var started = engineEvents[stopped + 1];
        Assert.Equal("EvaluationStarted", started.Type);
        Assert.NotNull(started.GapFrom);
    }

    [RequiresDatabaseFact]
    public async Task A_shelf_without_an_end_or_beyond_the_maximum_is_refused()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Shelf limit probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        using var asOperator = _host.CreateClient(@operator.Token);

        await AssertBadRequestAsync(asOperator.PostAsync($"/api/alarms/{definitionId}/shelve", content: null));
        await AssertBadRequestAsync(asOperator.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { }));
        await AssertBadRequestAsync(asOperator.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { durationMinutes = 24 * 60 + 1 }));
        await AssertBadRequestAsync(asOperator.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { durationMinutes = 0 }));

        Assert.Equal("Active", await _host.AlarmStateAsync(admin, definitionId));
        Assert.Empty(await _host.AuditActorsAsync("alarm.shelve", definitionId));

        // Control: the same operator on the same alarm can shelve within the limit, so the
        // refusals above are the duration rule and not something else.
        using var allowed = await asOperator.PostAsJsonAsync($"/api/alarms/{definitionId}/shelve", new { durationMinutes = 24 * 60 });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("Shelved", await _host.AlarmStateAsync(admin, definitionId));
    }

    [RequiresDatabaseFact]
    public async Task An_acknowledgement_the_journal_cannot_record_is_refused_and_not_audited()
    {
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Journal outage probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        using var asOperator = _host.CreateClient(@operator.Token);

        // A real outage of the one thing the acknowledgement needs: the application role can
        // no longer append to the journal. Grants belong to this scratch database alone.
        await _host.Database.ExecutePrivilegedAsync("REVOKE INSERT ON alarm_event FROM scada_app");
        try
        {
            using var refused = await asOperator.PostAsync($"/api/alarms/{definitionId}/acknowledge", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            Assert.Equal("Active", await _host.AlarmStateAsync(admin, definitionId));
            Assert.Empty(await _host.AuditActorsAsync("alarm.acknowledge", definitionId));
        }
        finally
        {
            await _host.Database.ExecutePrivilegedAsync("GRANT INSERT ON alarm_event TO scada_app");
        }

        // Control: with the journal back, the same request by the same operator succeeds, so
        // the refusal above was the journal and nothing else.
        using var accepted = await asOperator.PostAsync($"/api/alarms/{definitionId}/acknowledge", content: null);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(@operator.Id, Assert.Single(await _host.AuditActorsAsync("alarm.acknowledge", definitionId)));
    }

    private async Task<JsonElement> AlarmAsync(string adminToken, Guid definitionId)
    {
        using var client = _host.CreateClient(adminToken);
        var alarms = await client.GetFromJsonAsync<JsonElement>("/api/alarms");

        return Assert.Single(
            alarms.EnumerateArray(),
            alarm => alarm.GetProperty("definitionId").GetGuid() == definitionId);
    }

    private async Task<List<(string Type, DateTimeOffset? GapFrom)>> EngineEventsAsync()
    {
        await using var connection = new NpgsqlConnection(_host.Database.PrivilegedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT event_type, gap_from FROM alarm_event
            WHERE event_type IN ('EvaluationStarted', 'EvaluationStopped')
            ORDER BY id
            """,
            connection);

        var events = new List<(string, DateTimeOffset?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return events;
    }

    [RequiresDatabaseFact]
    public async Task The_journal_shows_every_reader_the_engine_events_that_belong_to_no_site()
    {
        // An evaluation outage applies to the whole Gateway, so it has no Site — and a
        // Viewer on one Site still has to know the system was not watching (ADR-0013).
        // A filter written as a bare site_id = ANY(...) drops these rows, and the outage
        // then reads as a quiet period.
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        await _host.RestartAsync();

        using var asViewer = _host.CreateClient(viewer.Token);
        var seen = await TypesAsync(asViewer);

        Assert.Contains("EvaluationStarted", seen);
        Assert.Contains("EvaluationStopped", seen);
    }

    [RequiresDatabaseFact]
    public async Task The_journal_hides_events_belonging_to_a_site_the_reader_cannot_see()
    {
        // The other half: "no Site means everyone's" must not leak into "any Site is
        // everyone's". Raised on Bitola, read by a Viewer who only has Skopje.
        var admin = await _host.LoginAsAdminAsync();
        var probe = await _host.CreateLiveDeviceAsync(admin, Bitola, "Journal scoping probe");
        var definitionId = await _host.RaiseAlarmAsync(admin, probe.TagId);
        var viewer = await _host.CreateUserAsync(admin, (Skopje, "Viewer"));

        using var asAdmin = _host.CreateClient(admin);
        using var asViewer = _host.CreateClient(viewer.Token);

        // First as Admin, so the refusal below hides something really there.
        Assert.Contains(definitionId, await DefinitionsAsync(asAdmin));
        Assert.DoesNotContain(definitionId, await DefinitionsAsync(asViewer));
    }

    private static async Task<List<string>> TypesAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/alarms/journal?limit=1000");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("type").GetString()!)
            .ToList();
    }

    private static async Task<List<Guid>> DefinitionsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/alarms/journal?limit=1000");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("definitionId"))
            .Where(id => id.ValueKind != JsonValueKind.Null)
            .Select(id => id.GetGuid())
            .ToList();
    }

    private static long ToMicroseconds(DateTimeOffset value) => value.UtcTicks / 10;

    private static async Task AssertBadRequestAsync(Task<HttpResponseMessage> send)
    {
        using var response = await send;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
