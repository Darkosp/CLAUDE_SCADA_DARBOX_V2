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
        var retired = Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.Retired);
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

        Assert.DoesNotContain(rig.Journal.Events, e => e.Type == AlarmEventType.Retired);
        Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.Raised);
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

    [Fact]
    public async Task Rationalising_a_STANDING_alarm_changes_the_list_without_waiting_for_it_to_re_raise()
    {
        // **The walk of 2026-10-09 found this on a screen.** The priority used to be copied onto the
        // alarm at raise and never revisited, so a threshold rationalised while its alarm was standing
        // showed `Priority High` on the tag's detail while the standing list went on ordering it as
        // unrationalised. Measured there: 100 seconds, because the demo flaps. On an alarm that stands
        // for days it would have been days.
        var rig = await Rig.StartedAsync(high: 4.5);

        await rig.FeedAsync(5.0);

        var before = Assert.Single(rig.Engine.GetCurrent());
        Assert.Null(before.Priority);

        // Rationalised while standing — the engine reconciles on a catalogue change, which is what a
        // threshold being saved produces.
        rig.CatalogSource.Set(Rig.Catalog(high: 4.5, priority: AlarmPriority.High));

        var after = Assert.Single(rig.Engine.GetCurrent());

        Assert.Equal(AlarmPriority.High, after.Priority);

        // And it is the *same* alarm, not a new one: rationalising must not retire and re-raise, which
        // would put a spurious pair in the journal and clear an acknowledgement.
        Assert.Equal(before.OccurrenceId, after.OccurrenceId);
    }

    [Fact]
    public async Task A_definition_deleted_under_a_standing_alarm_leaves_its_priority_alone()
    {
        // The case the resolution has to not get wrong. "Nobody has judged this" is a statement about
        // an alarm awaiting rationalisation; an alarm whose threshold somebody deleted is a different
        // thing, and blanking it would say something false about the only record left.
        var rig = await Rig.StartedAsync(high: 4.5);

        rig.CatalogSource.Set(Rig.Catalog(high: 4.5, priority: AlarmPriority.Medium));
        await rig.FeedAsync(5.0);

        Assert.Equal(AlarmPriority.Medium, Assert.Single(rig.Engine.GetCurrent()).Priority);

        rig.CatalogSource.Set(Rig.Catalog(configured: false));

        var standing = rig.Engine.GetCurrent().SingleOrDefault();

        if (standing is not null)
        {
            Assert.Equal(AlarmPriority.Medium, standing.Priority);
        }
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

    // ---- ADR-0025: an alarm waits before it announces itself, and a deadband shifts where it clears.

    [Fact]
    public async Task A_breach_that_stops_inside_the_delay_raises_nothing_at_all()
    {
        // This is the whole point of an on-delay and it is the hardest half to test, because it is a
        // fact about something NOT happening. The Phase 5.5 walk recorded the shape it removes: one
        // Site writing three journal rows every ~25 seconds while a value oscillated across a limit.
        var rig = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60));

        await rig.FeedAsync(5.0);
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.FeedAsync(4.0);

        // Nothing raised, and -- the half that matters to an operator -- nothing in the journal.
        Assert.Empty(rig.Engine.GetCurrent());
        Assert.Equal(0, rig.Raises);

        // And the wait really was running rather than the breach never having been noticed, which is
        // the control: the same value held past the delay does raise (the test below), so a zero here
        // is the delay working and not the engine being blind.
        Assert.Empty(rig.Journal.OccurrenceEvents());
    }

    [Fact]
    public async Task A_breach_held_past_the_delay_raises_once_the_delay_is_over()
    {
        // The control for the test above. Without it, "raises nothing" would pass just as well on an
        // engine that never raised anything.
        var rig = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60));

        await rig.FeedAsync(5.0);

        // Not yet: this is the delay doing its work.
        rig.Advance(TimeSpan.FromSeconds(59));
        await rig.FeedAsync(5.1);
        Assert.Empty(rig.Engine.GetCurrent());

        // And now it is.
        rig.Advance(TimeSpan.FromSeconds(2));
        await rig.FeedAsync(5.2);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(1, rig.Raises);
    }

    [Fact]
    public async Task The_raise_carries_the_reading_that_confirmed_the_breach_and_the_wait_it_waited()
    {
        // ADR-0025 §2: SourceTimeUtc is the sample that CONFIRMED the breach, not the one that began
        // it and not the moment the wait ended -- a reader comparing the alarm against the historian
        // has to find the reading that caused it. And the alarm says how long it waited, because a
        // raise with no note of a wait looks exactly like an instant one.
        var rig = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60));

        var began = rig.Clock.Now;
        await rig.FeedAsync(5.0, sourceTime: began);

        var confirming = began.AddSeconds(65);
        rig.Advance(TimeSpan.FromSeconds(65));
        await rig.FeedAsync(5.2, sourceTime: confirming);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(confirming, alarm.RaisedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(60), alarm.OnDelay);

        var raised = Assert.Single(rig.Journal.Events, e => e.Type == AlarmEventType.Raised);
        Assert.Equal(confirming, raised.SourceTimeUtc);
        Assert.Equal(5.2, raised.Value);
    }

    [Fact]
    public async Task A_value_that_crosses_to_the_other_limit_starts_its_wait_again()
    {
        // A High excursion becoming a Low one is a different condition, not a continuation of the
        // first, so the wait does not carry over. Carrying it over would let a value that spent 59
        // seconds high and one second low raise a Low alarm it never earned.
        var rig = await Rig.StartedAsync(high: 4.5, low: 2.0, onDelay: TimeSpan.FromSeconds(60));

        await rig.FeedAsync(5.0);
        rig.Advance(TimeSpan.FromSeconds(59));
        await rig.FeedAsync(1.0);

        rig.Advance(TimeSpan.FromSeconds(2));
        await rig.FeedAsync(1.0);

        // Past the delay by now, but only from the switch: nothing has raised.
        Assert.Empty(rig.Engine.GetCurrent());

        rig.Advance(TimeSpan.FromSeconds(60));
        await rig.FeedAsync(1.0);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmLimit.Low, alarm.Limit);
    }

    [Fact]
    public async Task A_reading_that_is_not_Good_abandons_the_wait_rather_than_pausing_it()
    {
        // ADR-0025 §6. A standing alarm is unaffected by an outage -- a device going offline must not
        // look like the value returning to normal, which is the rule the engine already follows. But a
        // WAIT is not a standing alarm, and "paused" is the answer that looks right and is not:
        // pausing would have the Gateway counting ten minutes in which it was not watching, and the
        // alarm would raise on the strength of an outage.
        var rig = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60));

        await rig.FeedAsync(5.0);

        var began = rig.Clock.Now;

        // A ten-minute outage. Bad readings carry no value to compare, so they are not evaluations.
        rig.Advance(TimeSpan.FromMinutes(10));
        await rig.FeedAsync(null, quality: Quality.Bad);

        Assert.Empty(rig.Engine.GetCurrent());

        // Readings resume and the wait starts again from here, so a moment later is still too soon --
        // which is the assertion that fails if the outage was counted.
        await rig.FeedAsync(5.0);
        rig.Advance(TimeSpan.FromSeconds(30));
        await rig.FeedAsync(5.0);
        Assert.Empty(rig.Engine.GetCurrent());

        // And a full delay after the outage, measured from when readings resumed, it raises.
        rig.Advance(TimeSpan.FromSeconds(31));
        await rig.FeedAsync(5.0);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(1, rig.Raises);
        Assert.True(alarm.RaisedAtUtc > began.AddMinutes(10));
    }

    [Fact]
    public async Task Without_a_delay_a_breach_raises_immediately_and_that_is_the_control()
    {
        // Every alarm configured before ADR-0025 behaves this way, and it has to keep behaving this
        // way: the migration is nullable precisely so that an upgrade cannot change what an existing
        // alarm means.
        var rig = await Rig.StartedAsync(high: 4.5);

        await rig.FeedAsync(5.0);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Null(alarm.OnDelay);
    }

    [Fact]
    public async Task A_deadband_does_not_delay_the_raise_it_only_moves_the_clear()
    {
        // ADR-0025 §3, and the reason the two settings are separate. An operator reading "High limit
        // 4.50 bar" is reading the truth about when the alarm goes off; what the deadband changes is
        // where it comes back.
        var rig = await Rig.StartedAsync(high: 4.5, deadband: 0.2);

        await rig.FeedAsync(4.6);

        var alarm = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(0.2, alarm.Deadband);
    }

    [Fact]
    public async Task A_deadband_holds_the_alarm_until_the_value_is_back_past_the_limit()
    {
        // The chatter this removes: a value hovering just under the limit used to clear on the first
        // reading back inside and raise again on the next one, once a scan.
        var rig = await Rig.StartedAsync(high: 4.5, deadband: 0.2);

        await rig.FeedAsync(4.6);
        Assert.Single(rig.Engine.GetCurrent());

        // Back across the limit, not yet far enough to clear.
        await rig.FeedAsync(4.35);
        var held = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, held.State);

        // And now past it by the band.
        await rig.FeedAsync(4.25);
        var cleared = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Cleared, cleared.State);
    }

    [Fact]
    public async Task The_exact_clear_point_is_not_yet_clear()
    {
        // "Past the limit by the deadband" is strict, and this pins it: at exactly limit - band the
        // alarm still stands. The alternative -- clearing at the boundary -- makes a value sitting
        // precisely on the clear point flicker, which is the thing the band exists to stop.
        var rig = await Rig.StartedAsync(high: 4.5, deadband: 0.2);

        await rig.FeedAsync(4.6);

        await rig.FeedAsync(4.3);
        var held = Assert.Single(rig.Engine.GetCurrent());
        Assert.Equal(AlarmState.Active, held.State);

        await rig.FeedAsync(4.29);
        Assert.Equal(AlarmState.Cleared, Assert.Single(rig.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_deadband_on_a_low_alarm_clears_upwards_which_is_the_mirror()
    {
        // The direction is the whole decision (ADR-0025 §3), and it is easy to get right for High and
        // wrong for Low. A Low alarm clears when the value has come back UP past limit + deadband.
        var rig = await Rig.StartedAsync(low: 2.0, deadband: 0.2);

        await rig.FeedAsync(1.9);
        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);

        // Back above the limit, not yet far enough.
        await rig.FeedAsync(2.1);
        Assert.Equal(AlarmState.Active, Assert.Single(rig.Engine.GetCurrent()).State);

        await rig.FeedAsync(2.25);
        Assert.Equal(AlarmState.Cleared, Assert.Single(rig.Engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_restart_resets_a_wait_in_progress_because_a_wait_is_not_journalled()
    {
        // ADR-0025 §4, and the cost is stated there rather than hidden. The wait is state the engine
        // holds, not something that happened at the plant: the journal records the second. So a
        // breach 40 seconds into a 60-second wait needs another 60 afterwards -- and, the half worth
        // pinning, the abandoned wait left nothing behind.
        var journal = new RecordingAlarmJournal();

        var first = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60), journal: journal);
        await first.FeedAsync(5.0);
        first.Advance(TimeSpan.FromSeconds(40));
        await first.FeedAsync(5.0);

        // The abandoned wait is invisible: no raise, and nothing else about it either.
        Assert.Empty(first.Engine.GetCurrent());
        Assert.Empty(journal.OccurrenceEvents());

        // A restart. The same breach, and the wait starts again from nothing.
        var second = await Rig.StartedAsync(high: 4.5, onDelay: TimeSpan.FromSeconds(60), journal: journal);

        await second.FeedAsync(5.0);
        second.Advance(TimeSpan.FromSeconds(30));
        await second.FeedAsync(5.0);
        Assert.Empty(second.Engine.GetCurrent());

        second.Advance(TimeSpan.FromSeconds(31));
        await second.FeedAsync(5.0);
        Assert.Equal(1, second.Raises);
    }

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
            TimeSpan? maxShelve = null,
            TimeSpan? onDelay = null,
            double? deadband = null) =>
            new(
                new TagCatalogSource(Catalog(high, low, configured, onDelay, deadband)),
                journal ?? new RecordingAlarmJournal(),
                maxShelve);

        public static async Task<Rig> StartedAsync(
            double? high = null,
            double? low = null,
            bool configured = true,
            RecordingAlarmJournal? journal = null,
            TimeSpan? maxShelve = null,
            DateTimeOffset? lastAlive = null,
            TimeSpan? onDelay = null,
            double? deadband = null)
        {
            var rig = Unstarted(high, low, configured, journal, maxShelve, onDelay, deadband);
            var before = rig.Journal.Count;
            await rig.Engine.StartAsync(lastAlive, CancellationToken.None);
            rig.Journal.StartedAt(before);
            rig.Subscriber.Publications.Clear();
            return rig;
        }

        /// <summary>Moves the Gateway's clock, which is what an on-delay is measured against.</summary>
        public void Advance(TimeSpan by) => Clock.Now = Clock.Now + by;

        /// <summary>How many Raise events are in the journal. The count, not "did one happen".</summary>
        public int Raises => Journal.Events.Count(e => e.Type == AlarmEventType.Raised);

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

        public static TagCatalog Catalog(
            double? high = null,
            double? low = null,
            bool configured = true,
            TimeSpan? onDelay = null,
            double? deadband = null,
            AlarmPriority? priority = null)
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
                ? [
                    new AlarmDefinition
                    {
                        Id = DefinitionId,
                        TagId = TagId,
                        HighLimit = high,
                        LowLimit = low,
                        OnDelaySeconds = onDelay,
                        Deadband = deadband,
                        Priority = priority,
                    },
                ]
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

    public Task<bool> AppendLossOnceAsync(AlarmEvent loss, CancellationToken cancellationToken)
    {
        if (Failing)
        {
            throw new InvalidOperationException("The journal is unavailable.");
        }

        if (_all.Any(recorded => recorded.LossId == loss.LossId))
        {
            return Task.FromResult(false);
        }

        Appends++;
        _all.Add(loss);
        return Task.FromResult(true);
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

    /// <summary>Same rule as the database: a row with no Site and no occurrence is everyone's.</summary>
    public Task<IReadOnlyList<AlarmEvent>> ReadHistoryAsync(
        AlarmJournalQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlarmEvent>>(_all
            .Where(e => query.SiteIds is null
                        || (e.SiteId is { } site && query.SiteIds.Contains(site))
                        || (e.SiteId is null && e.OccurrenceId is null))
            .Where(e => query.FromUtc is not { } from || e.RecordedAtUtc >= from)
            .Where(e => query.ToUtc is not { } to || e.RecordedAtUtc < to)
            .OrderByDescending(e => e.RecordedAtUtc)
            .Take(query.Limit)
            .ToList());

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
