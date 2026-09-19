using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

public class AlarmEngineTests
{
    private static readonly Guid TagId = new("11111111-2222-4111-8111-111111111111");
    private static readonly Guid DefinitionId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SiteId = new("33333333-2222-4333-8333-333333333333");
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlarmActor Operator = new(new Guid("44444444-2222-4444-8444-444444444444"), "ana");

    [Fact]
    public async Task A_value_at_or_above_the_high_limit_raises_an_alarm()
    {
        var rig = await Rig.StartedAsync(high: 8.0);

        await rig.FeedAsync(8.0);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(AlarmLimit.High, alarm.Limit);
        Assert.Equal(8.0, alarm.LimitValue);
        Assert.Equal(SiteId, alarm.SiteId);
        Assert.Equal("Skopje/Pump House/Discharge Pressure", alarm.TagPath);
        Assert.Equal("bar", alarm.UnitSymbol);
    }

    [Fact]
    public async Task A_value_at_or_below_the_low_limit_raises_an_alarm()
    {
        var rig = await Rig.StartedAsync(low: 2.0);

        await rig.FeedAsync(1.5);

        Assert.Equal(AlarmLimit.Low, Assert.Single(rig.Engine.GetCurrent()).Limit);
    }

    [Fact]
    public async Task A_value_inside_the_limits_raises_nothing()
    {
        var rig = await Rig.StartedAsync(high: 8.0, low: 2.0);

        await rig.FeedAsync(4.2);

        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Empty(rig.Subscriber.Publications);
        Assert.DoesNotContain(rig.Journal.Events, e => e.OccurrenceId is not null);
    }

    [Fact]
    public async Task A_bad_reading_raises_no_alarm()
    {
        // A Bad reading carries no value (ADR-0003). There is nothing to compare, so
        // inventing an alarm from it would be as wrong as inventing the value.
        var rig = await Rig.StartedAsync(high: 8.0);

        await rig.FeedAsync(value: null, quality: Quality.Bad);

        Assert.Empty(rig.Engine.GetCurrent());
    }

    [Fact]
    public async Task A_device_going_offline_does_not_clear_a_standing_alarm()
    {
        // The case worth guarding hardest: if losing the device looked like the value
        // returning to normal, an alarm would disappear at exactly the moment the
        // operator has least information.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        await rig.FeedAsync(value: null, quality: Quality.Bad);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Null(alarm.ClearedAtUtc);
        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.Cleared);
    }

    [Fact]
    public async Task An_unacknowledged_alarm_stays_listed_after_the_value_recovers()
    {
        // An excursion that corrected itself is precisely what an operator who stepped
        // away needs to see. Dropping it would erase the only record it happened.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        await rig.FeedAsync(4.0);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Cleared, alarm.State);
        Assert.Equal(9.0, alarm.ValueAtRaise);
        Assert.NotNull(alarm.ClearedAtUtc);
    }

    [Fact]
    public async Task An_acknowledged_alarm_disappears_once_the_value_recovers()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);

        await rig.FeedAsync(4.0);

        Assert.Empty(rig.Engine.GetCurrent());
    }

    [Fact]
    public async Task Acknowledging_an_active_alarm_marks_it_seen_by_whom_without_removing_it()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        rig.Clock.Now = Start.AddMinutes(3);

        Assert.True(await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None));

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Acknowledged, alarm.State);
        Assert.Equal(Start.AddMinutes(3), alarm.AcknowledgedAtUtc);
        Assert.Equal(Operator, alarm.AcknowledgedBy);
    }

    [Fact]
    public async Task Acknowledging_a_recovered_alarm_retires_it()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.FeedAsync(4.0);

        Assert.True(await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None));

        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Equal(
            [AlarmEventType.Raised, AlarmEventType.Cleared, AlarmEventType.Acknowledged, AlarmEventType.Retired],
            rig.Journal.OccurrenceEvents().Select(e => e.Type));
    }

    [Fact]
    public async Task Acknowledging_something_that_is_not_in_alarm_reports_failure()
    {
        var rig = await Rig.StartedAsync(high: 8.0);

        Assert.False(await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None));
        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.Acknowledged);
    }

    [Fact]
    public async Task A_shelved_alarm_stays_shelved_while_the_value_is_still_out_of_range()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None);

        await rig.FeedAsync(9.5);

        Assert.Equal(AlarmState.Shelved, Assert.Single(rig.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_standing_alarm_is_not_republished_or_rejournalled_on_every_scan()
    {
        // Without this the banner would be rewritten once a second for as long as a
        // value stayed out of range, and the journal would grow a row per scan.
        var rig = await Rig.StartedAsync(high: 8.0);

        await rig.FeedAsync(9.0);
        await rig.FeedAsync(9.1);
        await rig.FeedAsync(9.2);

        Assert.Single(rig.Subscriber.Publications, p => p.Count > 0);
        Assert.Single(rig.Journal.OccurrenceEvents());
    }

    [Fact]
    public async Task A_tag_with_no_alarm_configured_is_ignored()
    {
        var rig = await Rig.StartedAsync(configured: false);

        await rig.FeedAsync(9999.0);

        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Empty(rig.Subscriber.Publications);
    }

    [Fact]
    public async Task Nothing_is_evaluated_before_the_engine_has_rebuilt_from_the_journal()
    {
        // A value evaluated before the rebuild would raise a second occurrence beside the
        // one the journal still holds open.
        var rig = Rig.Unstarted(high: 8.0);

        await rig.FeedAsync(9.0);

        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Empty(rig.Journal.Events);
    }

    // ---- The journal (ADR-0013) ----

    [Fact]
    public async Task Every_transition_is_journalled_under_one_occurrence_with_both_clocks()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        var sourceTime = Start.AddSeconds(-2);
        rig.Clock.Now = Start.AddSeconds(1);

        await rig.FeedAsync(9.0, sourceTime: sourceTime);
        await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);
        await rig.FeedAsync(4.0, sourceTime: sourceTime.AddMinutes(5));

        var events = rig.Journal.OccurrenceEvents();
        Assert.Equal(
            [AlarmEventType.Raised, AlarmEventType.Acknowledged, AlarmEventType.Cleared, AlarmEventType.Retired],
            events.Select(e => e.Type));
        Assert.Single(events.Select(e => e.OccurrenceId).Distinct());
        Assert.All(events, e =>
        {
            Assert.Equal(DefinitionId, e.DefinitionId);
            Assert.Equal(TagId, e.TagId);
            Assert.Equal(SiteId, e.SiteId);
        });

        var raised = events[0];
        Assert.Equal(sourceTime, raised.SourceTimeUtc);
        Assert.Equal(Start.AddSeconds(1), raised.RecordedAtUtc);
        Assert.Equal(9.0, raised.Value);
        Assert.Equal(AlarmLimit.High, raised.Limit);
        Assert.Equal("Skopje/Pump House/Discharge Pressure", raised.TagPath);

        Assert.Equal(Operator, events[1].Actor);
        Assert.Equal(sourceTime.AddMinutes(5), events[2].SourceTimeUtc);
    }

    [Fact]
    public async Task A_new_excursion_after_retirement_is_a_new_occurrence()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);
        await rig.FeedAsync(4.0);

        await rig.FeedAsync(9.0);

        var raises = rig.Journal.Events.Where(e => e.Type == AlarmEventType.Raised).ToList();
        Assert.Equal(2, raises.Count);
        Assert.NotEqual(raises[0].OccurrenceId, raises[1].OccurrenceId);
        Assert.Equal(raises[1].OccurrenceId, Assert.Single(rig.Engine.GetCurrent()).OccurrenceId);
    }

    [Fact]
    public async Task A_breach_after_an_unacknowledged_clear_retires_that_occurrence_and_raises_a_new_one()
    {
        // A live list still saying "recovered" while the value is out of range again is
        // true about the past and wrong about the present — and the present is what the
        // operator is looking at (ADR-0013).
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.FeedAsync(4.0);

        await rig.FeedAsync(9.5);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(9.5, alarm.ValueAtRaise);
        Assert.Null(alarm.ClearedAtUtc);

        var raises = rig.Journal.Events.Where(e => e.Type == AlarmEventType.Raised).ToList();
        Assert.Equal(2, raises.Count);
        Assert.NotEqual(raises[0].OccurrenceId, raises[1].OccurrenceId);
        Assert.Equal(raises[1].OccurrenceId, alarm.OccurrenceId);

        // Named, so a reader can tell this from a definition being removed and from the
        // retirement that follows an acknowledgement.
        var retired = Assert.Single(rig.Journal.Events.Where(e => e.Type == AlarmEventType.Retired));
        Assert.Equal(raises[0].OccurrenceId, retired.OccurrenceId);
        Assert.Equal(AlarmEngine.SupersededByNewBreachReason, retired.Reason);
    }

    [Fact]
    public async Task The_retirement_and_the_occurrence_replacing_it_are_written_together()
    {
        // Written apart, they leave an instant in which the journal shows two open
        // occurrences on one definition, or one retired with no successor.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.FeedAsync(4.0);
        var appendsBefore = rig.Journal.Appends;

        await rig.FeedAsync(9.5);

        Assert.Equal(appendsBefore + 1, rig.Journal.Appends);

        var written = rig.Journal.Events.TakeLast(2).ToList();
        Assert.Equal(AlarmEventType.Retired, written[0].Type);
        Assert.Equal(AlarmEventType.Raised, written[1].Type);
    }

    [Fact]
    public async Task An_active_alarm_is_not_replaced_by_a_further_breach()
    {
        // Only a cleared occurrence is superseded. One still out of range is the same
        // excursion, and must not be retired and re-raised on every scan.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        await rig.FeedAsync(9.5);

        Assert.Empty(rig.Journal.Events.Where(e => e.Type == AlarmEventType.Retired));
        Assert.Single(rig.Journal.Events.Where(e => e.Type == AlarmEventType.Raised));
        Assert.Equal(9.0, Assert.Single(rig.Engine.GetCurrent()).ValueAtRaise);
    }

    [Fact]
    public async Task A_restart_rebuilds_the_standing_alarms_with_who_acknowledged_them()
    {
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);
        await first.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);
        var before = Assert.Single(first.Engine.GetCurrent());

        var second = await Rig.StartedAsync(high: 8.0, journal: first.Journal);

        var after = Assert.Single(second.Engine.GetCurrent());
        Assert.Equal(before.OccurrenceId, after.OccurrenceId);
        Assert.Equal(AlarmState.Acknowledged, after.State);
        Assert.Equal(Operator, after.AcknowledgedBy);
        Assert.Equal(before.AcknowledgedAtUtc, after.AcknowledgedAtUtc);
        Assert.Equal(before.RaisedAtUtc, after.RaisedAtUtc);
        Assert.Equal(9.0, after.ValueAtRaise);
    }

    [Fact]
    public async Task A_restart_rebuilds_a_cleared_but_unacknowledged_alarm_as_cleared()
    {
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);
        await first.FeedAsync(4.0);

        var second = await Rig.StartedAsync(high: 8.0, journal: first.Journal);

        Assert.Equal(AlarmState.Cleared, Assert.Single(second.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_rebuilt_alarm_is_not_raised_a_second_time_by_the_first_value_after_restart()
    {
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);

        var second = await Rig.StartedAsync(high: 8.0, journal: first.Journal);
        await second.FeedAsync(9.5);

        Assert.DoesNotContain(second.Journal.Events, e => e.Type == AlarmEventType.Raised);
        Assert.Single(second.Engine.GetCurrent());
    }

    [Fact]
    public async Task A_breach_first_seen_after_a_restart_is_marked_as_detected_then()
    {
        var rig = await Rig.StartedAsync(high: 8.0);

        await rig.FeedAsync(9.0);

        Assert.True(Assert.Single(rig.Engine.GetCurrent()).DetectedAfterRestart);
        Assert.True(rig.Journal.OccurrenceEvents()[0].DetectedAfterRestart);
    }

    [Fact]
    public async Task Only_the_first_evaluation_after_a_restart_is_marked()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(4.0);

        await rig.FeedAsync(9.0);

        Assert.False(Assert.Single(rig.Engine.GetCurrent()).DetectedAfterRestart);
    }

    [Fact]
    public async Task A_bad_reading_does_not_use_up_the_first_evaluation_mark()
    {
        // The device was offline at startup: the first reading with a value is still the
        // first time anything could be seen.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(value: null, quality: Quality.Bad);

        await rig.FeedAsync(9.0);

        Assert.True(Assert.Single(rig.Engine.GetCurrent()).DetectedAfterRestart);
    }

    [Fact]
    public async Task A_clear_first_seen_after_a_restart_is_marked_as_detected_then()
    {
        // Recovery happened some time while nothing watched; the journal must not claim
        // it happened at the moment of the first reading.
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);

        var second = await Rig.StartedAsync(high: 8.0, journal: first.Journal);
        await second.FeedAsync(4.0);

        var cleared = Assert.Single(second.Journal.Events, e => e.Type == AlarmEventType.Cleared);
        Assert.True(cleared.DetectedAfterRestart);
    }

    [Fact]
    public async Task Evaluation_start_records_the_outage_from_the_last_moment_known_alive()
    {
        var lastAlive = Start.AddMinutes(-7);

        var rig = await Rig.StartedAsync(high: 8.0, lastAlive: lastAlive);

        var started = Assert.Single(rig.Journal.Events);
        Assert.Equal(AlarmEventType.EvaluationStarted, started.Type);
        Assert.Equal(lastAlive, started.GapFromUtc);
        Assert.Equal(Start, started.GapUntilUtc);
        Assert.Null(started.OccurrenceId);
        Assert.Null(started.SiteId);
    }

    [Fact]
    public async Task Evaluation_start_with_nothing_known_alive_leaves_the_outage_open_ended()
    {
        var rig = await Rig.StartedAsync(high: 8.0, lastAlive: null);

        var started = Assert.Single(rig.Journal.Events);
        Assert.Null(started.GapFromUtc);
        Assert.Equal(Start, started.GapUntilUtc);
    }

    [Fact]
    public async Task Startup_fails_when_evaluation_start_cannot_be_recorded()
    {
        var rig = Rig.Unstarted(high: 8.0);
        rig.Journal.Failing = true;

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Engine.StartAsync(null, CancellationToken.None));

        // And it does not evaluate as though it had started.
        rig.Journal.Failing = false;
        await rig.FeedAsync(9.0);
        Assert.Empty(rig.Engine.GetCurrent());
    }

    [Fact]
    public async Task A_clean_stop_is_recorded()
    {
        var rig = await Rig.StartedAsync(high: 8.0);

        await rig.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(AlarmEventType.EvaluationStopped, rig.Journal.Events[^1].Type);
    }

    [Fact]
    public async Task An_acknowledgement_that_cannot_be_recorded_does_not_happen()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        rig.Journal.Failing = true;

        await Assert.ThrowsAsync<AlarmJournalUnavailableException>(
            () => rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None));

        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);

        // Refused, not lost: nothing happened, so recovery reports no gap.
        rig.Journal.Failing = false;
        await rig.FeedAsync(4.0);
        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.JournalGap);
    }

    [Fact]
    public async Task A_shelve_that_cannot_be_recorded_does_not_happen()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        rig.Journal.Failing = true;

        await Assert.ThrowsAsync<AlarmJournalUnavailableException>(
            () => rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None));

        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task Value_driven_transitions_go_ahead_while_the_journal_is_failing()
    {
        // The operator in front of the screen needs the alarm more than the database
        // needs the row (ADR-0013).
        var rig = await Rig.StartedAsync(high: 8.0);
        rig.Journal.Failing = true;

        await rig.FeedAsync(9.0);

        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);
        Assert.Contains(rig.Subscriber.Publications, p => p.Count == 1);
    }

    [Fact]
    public async Task Recovery_from_a_journal_failure_records_the_window_and_what_it_cost()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        rig.Journal.Failing = true;
        rig.Clock.Now = Start.AddMinutes(12);
        await rig.FeedAsync(9.0);
        rig.Clock.Now = Start.AddMinutes(15);
        await rig.FeedAsync(4.0);

        rig.Journal.Failing = false;
        rig.Clock.Now = Start.AddMinutes(19);
        await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);

        var gap = Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.JournalGap);
        Assert.Equal(Start.AddMinutes(12), gap.GapFromUtc);
        Assert.Equal(Start.AddMinutes(19), gap.GapUntilUtc);
        Assert.Equal(2, gap.UnrecordedTransitions);
        Assert.Null(gap.SiteId);

        // Written before the event that succeeded, so the journal reads in order.
        var gapIndex = rig.Journal.Events.IndexOf(gap);
        Assert.Equal(AlarmEventType.Acknowledged, rig.Journal.Events[gapIndex + 1].Type);
    }

    // ---- Shelving (ADR-0013) ----

    [Fact]
    public async Task A_shelf_ends_when_it_says()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        Assert.Equal(
            ShelveOutcome.Shelved,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromMinutes(90), CancellationToken.None));

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(Start.AddMinutes(90), alarm.ShelvedUntilUtc);

        var shelved = Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.Shelved);
        Assert.Equal(Start.AddMinutes(90), shelved.ShelvedUntilUtc);
        Assert.Equal(Operator, shelved.Actor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(24 * 60 + 1)]
    public async Task A_shelf_outside_the_allowed_duration_is_refused(int minutes)
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        Assert.Equal(
            ShelveOutcome.DurationNotAllowed,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromMinutes(minutes), CancellationToken.None));

        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);
        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.Shelved);
    }

    [Fact]
    public async Task A_shelf_of_exactly_the_maximum_is_allowed()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        Assert.Equal(
            ShelveOutcome.Shelved,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(24), CancellationToken.None));
    }

    [Fact]
    public async Task The_maximum_shelf_is_configurable()
    {
        var rig = await Rig.StartedAsync(high: 8.0, maxShelve: TimeSpan.FromHours(2));
        await rig.FeedAsync(9.0);

        Assert.Equal(
            ShelveOutcome.DurationNotAllowed,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(3), CancellationToken.None));
    }

    [Fact]
    public async Task A_recovered_alarm_cannot_be_shelved()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.FeedAsync(4.0);

        Assert.Equal(
            ShelveOutcome.NotInAlarm,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task Shelving_nothing_reports_not_found()
    {
        var rig = await Rig.StartedAsync(high: 8.0);

        Assert.Equal(
            ShelveOutcome.NotFound,
            await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_shelf_returns_the_alarm_as_active()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.AcknowledgeAsync(DefinitionId, Operator, CancellationToken.None);
        await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None);

        rig.Clock.Now = Start.AddHours(1);
        await rig.Engine.ExpireShelvesAsync(CancellationToken.None);

        // Active, not Acknowledged: what it was before the shelf is not remembered, so
        // hiding it costs a fresh acknowledgement (ADR-0013).
        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Null(alarm.ShelvedUntilUtc);
        Assert.Null(alarm.AcknowledgedBy);
        Assert.Equal(AlarmEventType.Unshelved, rig.Journal.Events[^1].Type);
    }

    [Fact]
    public async Task A_shelf_not_yet_expired_is_left_alone()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None);

        rig.Clock.Now = Start.AddMinutes(59);
        await rig.Engine.ExpireShelvesAsync(CancellationToken.None);

        Assert.Equal(AlarmState.Shelved, Assert.Single(rig.Engine.GetCurrent()).State);
        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.Unshelved);
    }

    [Fact]
    public async Task A_shelf_expires_even_while_the_device_is_offline()
    {
        // Unknown is not the same as fine: with no readings at all, only the clock can
        // bring the alarm back, and it must.
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);
        await rig.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None);
        await rig.FeedAsync(value: null, quality: Quality.Bad);

        rig.Clock.Now = Start.AddHours(2);
        await rig.Engine.ExpireShelvesAsync(CancellationToken.None);

        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_shelf_survives_a_restart_with_its_expiry()
    {
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);
        await first.Engine.ShelveAsync(DefinitionId, Operator, TimeSpan.FromHours(1), CancellationToken.None);

        var second = await Rig.StartedAsync(high: 8.0, journal: first.Journal);
        second.Clock.Now = Start.AddHours(1);
        await second.Engine.ExpireShelvesAsync(CancellationToken.None);

        Assert.Equal(AlarmState.Active, Assert.Single(second.Engine.GetCurrent()).State);
    }

    // ---- Retirement ----

    [Fact]
    public async Task Removing_a_definition_retires_its_standing_alarm_with_the_reason()
    {
        var rig = await Rig.StartedAsync(high: 8.0);
        await rig.FeedAsync(9.0);

        // Through the catalogue's own change notification, as configuration edits arrive.
        rig.CatalogSource.Set(Rig.Catalog(configured: false));

        Assert.Empty(rig.Engine.GetCurrent());
        var retired = Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.Retired);
        Assert.Equal(AlarmEngine.DefinitionRemovedReason, retired.Reason);
        Assert.Equal(SiteId, retired.SiteId);
    }

    [Fact]
    public async Task A_definition_removed_while_stopped_is_retired_at_startup()
    {
        var first = await Rig.StartedAsync(high: 8.0);
        await first.FeedAsync(9.0);

        var second = await Rig.StartedAsync(configured: false, journal: first.Journal);

        Assert.Empty(second.Engine.GetCurrent());
        Assert.Equal(
            AlarmEngine.DefinitionRemovedReason,
            Assert.Single(second.Journal.Events, e => e.Type == AlarmEventType.Retired).Reason);
        Assert.Empty(await second.Journal.ReadOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_retirement_interrupted_by_a_stop_is_completed_at_startup()
    {
        // Acknowledged and Cleared were written; the Gateway stopped before Retired.
        var journal = new RecordingAlarmJournal();
        var occurrence = Guid.NewGuid();
        journal.Seed(Raised(occurrence));
        journal.Seed(Raised(occurrence) with { Type = AlarmEventType.Acknowledged, Actor = Operator });
        journal.Seed(Raised(occurrence) with { Type = AlarmEventType.Cleared });

        var rig = await Rig.StartedAsync(high: 8.0, journal: journal);

        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Equal(
            AlarmEngine.CompletedAtStartupReason,
            Assert.Single(journal.Events, e => e.Type == AlarmEventType.Retired).Reason);
    }

    [Fact]
    public async Task Two_open_occurrences_on_one_definition_leave_only_the_newer_standing()
    {
        var journal = new RecordingAlarmJournal();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        journal.Seed(Raised(older));
        journal.Seed(Raised(newer));

        var rig = await Rig.StartedAsync(high: 8.0, journal: journal);

        Assert.Equal(newer, Assert.Single(rig.Engine.GetCurrent()).OccurrenceId);
        var retired = Assert.Single(journal.Events, e => e.Type == AlarmEventType.Retired);
        Assert.Equal(older, retired.OccurrenceId);
        Assert.Equal(AlarmEngine.SupersededReason, retired.Reason);
    }

    private static AlarmEvent Raised(Guid occurrence) => new()
    {
        Type = AlarmEventType.Raised,
        RecordedAtUtc = Start.AddHours(-1),
        OccurrenceId = occurrence,
        DefinitionId = DefinitionId,
        TagId = TagId,
        SiteId = SiteId,
        SourceTimeUtc = Start.AddHours(-1),
        Limit = AlarmLimit.High,
        LimitValue = 8.0,
        Value = 9.0,
        TagPath = "Skopje/Pump House/Discharge Pressure",
    };

    private sealed class Rig
    {
        private Rig(TagCatalogSource catalogSource, RecordingAlarmJournal journal, TimeSpan? maxShelve)
        {
            CatalogSource = catalogSource;
            Journal = journal;
            Engine = new AlarmEngine(
                catalogSource,
                [Subscriber],
                journal,
                Clock,
                maxShelve is { } max ? new AlarmEngineOptions { MaxShelveDuration = max } : new AlarmEngineOptions());
        }

        public StubTimeProvider Clock { get; } = new(Start);

        public TagCatalogSource CatalogSource { get; }

        public RecordingAlarmJournal Journal { get; }

        public RecordingAlarmSubscriber Subscriber { get; } = new();

        public AlarmEngine Engine { get; }

        public static Rig Unstarted(
            double? high = null,
            double? low = null,
            bool configured = true,
            RecordingAlarmJournal? journal = null,
            TimeSpan? maxShelve = null) =>
            new(new TagCatalogSource(Catalog(high, low, configured)), journal ?? new RecordingAlarmJournal(), maxShelve);

        public static async Task<Rig> StartedAsync(
            double? high = null,
            double? low = null,
            bool configured = true,
            RecordingAlarmJournal? journal = null,
            TimeSpan? maxShelve = null,
            DateTimeOffset? lastAlive = null)
        {
            var rig = Unstarted(high, low, configured, journal, maxShelve);
            var before = rig.Journal.Count;
            await rig.Engine.StartAsync(lastAlive, CancellationToken.None);
            rig.Journal.StartedAt(before);
            rig.Subscriber.Publications.Clear();
            return rig;
        }

        public Task FeedAsync(
            double? value,
            Quality quality = Quality.Good,
            DateTimeOffset? sourceTime = null) =>
            Engine.OnTagValuesAsync(
                [
                    new TagSnapshot(
                        TagId,
                        "Skopje/Pump House/Discharge Pressure",
                        value is null ? null : new TagValue.Numeric(value.Value),
                        sourceTime ?? Clock.Now,
                        quality,
                        "bar")
                ],
                CancellationToken.None).AsTask();

        public static TagCatalog Catalog(double? high = null, double? low = null, bool configured = true)
        {
            var tenant = new Tenant { Id = new Guid("55555555-2222-4555-8555-555555555555"), Name = "Darbo" };
            var site = new Site { Id = SiteId, TenantId = tenant.Id, Name = "Skopje" };
            var device = new Device
            {
                Id = new Guid("66666666-2222-4666-8666-666666666666"),
                SiteId = site.Id,
                Name = "Pump House",
                DriverKey = "modbus-tcp",
            };
            var tag = new Tag
            {
                Id = TagId,
                DeviceId = device.Id,
                Name = "Discharge Pressure",
                ValueKind = TagValueKind.Numeric,
                Unit = new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
                SourceAddress = "holding:0",
            };

            AlarmDefinition[] alarms = configured
                ? [new AlarmDefinition { Id = DefinitionId, TagId = TagId, HighLimit = high, LowLimit = low }]
                : [];

            return new TagCatalog(tenant, [site], [], [device], [tag], alarms);
        }
    }
}

/// <summary>
/// An in-memory journal that reads back open occurrences by the same rule as the database:
/// an occurrence is open until it has a Retired event.
/// </summary>
internal sealed class RecordingAlarmJournal : IAlarmJournal
{
    private readonly List<AlarmEvent> _all = [];
    private int _thisRunFrom;

    /// <summary>When set, every append throws, as a database outage would.</summary>
    public bool Failing { get; set; }

    /// <summary>How many times the engine called the journal; a pair written together counts once.</summary>
    public int Appends { get; private set; }

    /// <summary>Every event ever appended or seeded, across runs.</summary>
    public int Count => _all.Count;

    /// <summary>Events written by the engine under test during this run.</summary>
    public List<AlarmEvent> Events => _all.Skip(_thisRunFrom).ToList();

    /// <summary>
    /// Marks where this run began, so assertions see this run's events and not a previous
    /// run's. Everything from <paramref name="index"/> on, including EvaluationStarted.
    /// </summary>
    public void StartedAt(int index) => _thisRunFrom = index;

    public void Seed(AlarmEvent alarmEvent)
    {
        _all.Add(alarmEvent);
        _thisRunFrom = _all.Count;
    }

    /// <summary>This run's events that belong to an alarm, leaving out the engine's own.</summary>
    public List<AlarmEvent> OccurrenceEvents() => Events.Where(e => e.OccurrenceId is not null).ToList();

    public Task AppendAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken)
    {
        if (Failing)
        {
            throw new InvalidOperationException("The journal is unavailable.");
        }

        Appends++;
        _all.Add(alarmEvent);
        return Task.CompletedTask;
    }

    /// <summary>All or nothing, as the database's transaction is: a failure writes none of them.</summary>
    public Task AppendAsync(IReadOnlyList<AlarmEvent> events, CancellationToken cancellationToken)
    {
        if (Failing)
        {
            throw new InvalidOperationException("The journal is unavailable.");
        }

        Appends++;
        _all.AddRange(events);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlarmEvent>> ReadOpenAsync(CancellationToken cancellationToken)
    {
        var retired = _all
            .Where(e => e.Type == AlarmEventType.Retired && e.OccurrenceId is not null)
            .Select(e => e.OccurrenceId)
            .ToHashSet();

        return Task.FromResult<IReadOnlyList<AlarmEvent>>(
            _all.Where(e => e.OccurrenceId is not null && !retired.Contains(e.OccurrenceId)).ToList());
    }
}

internal sealed class RecordingAlarmSubscriber : IAlarmSubscriber
{
    public List<IReadOnlyList<Alarm>> Publications { get; } = [];

    public ValueTask OnAlarmsChangedAsync(IReadOnlyList<Alarm> alarms, CancellationToken cancellationToken)
    {
        Publications.Add(alarms);
        return ValueTask.CompletedTask;
    }
}
