using ScadaDarbox.Core.Alarms;
using Xunit;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// What a priority is, and where an alarm without one sits (ADR-0034).
/// </summary>
/// <remarks>
/// **The null cases are the ones to read first.** ADR-0034 §2 makes null mean *not yet rationalised* —
/// ISA-18.2's own lifecycle state rather than an absence of ours — and the whole value of that
/// decision is that the migration did not have to invent a consequence assessment for every existing
/// alarm. A test suite that only covered the three named values would pass just as happily if null
/// were quietly treated as Low.
/// </remarks>
public sealed class AlarmPriorityTests
{
    [Fact]
    public void Not_yet_rationalised_sorts_LAST_rather_than_among_the_lows()
    {
        // The distinction the whole decision rests on. An unrationalised alarm is not a low-priority
        // alarm; it is one nobody has assessed, and somebody has to be able to find them to do that
        // work. Mixed in among the Lows they are invisible.
        Assert.True(
            AlarmPriorityOrder.RankOf(null) > AlarmPriorityOrder.RankOf(AlarmPriority.Low),
            "an unrationalised alarm must sort after every rationalised one");
    }

    [Fact]
    public void The_three_sort_most_urgent_first()
    {
        Assert.True(AlarmPriorityOrder.RankOf(AlarmPriority.High) < AlarmPriorityOrder.RankOf(AlarmPriority.Medium));
        Assert.True(AlarmPriorityOrder.RankOf(AlarmPriority.Medium) < AlarmPriorityOrder.RankOf(AlarmPriority.Low));
    }

    [Fact]
    public void There_is_room_above_High_and_between_each_pair_for_a_tier_nobody_has_asked_for_yet()
    {
        // ADR-0034 §1 says a fourth tier costs an enum member and a sort. That is only true if the
        // ranks leave room — otherwise adding *Emergency* above High renumbers everything that reads
        // these, which is exactly the kind of change that gets made in a hurry and gets made wrong.
        Assert.True(AlarmPriorityOrder.RankOf(AlarmPriority.High) > 1, "nothing can sort above High");

        Assert.True(
            AlarmPriorityOrder.RankOf(AlarmPriority.Medium) - AlarmPriorityOrder.RankOf(AlarmPriority.High) > 1,
            "no room between High and Medium");
    }

    [Fact]
    public void Every_priority_has_a_rank_so_a_new_member_cannot_be_forgotten()
    {
        // The switch has a `_` arm, which means a fourth member would silently get the "unrationalised"
        // rank and sort last — the opposite of where an *Emergency* tier belongs, and a defect no
        // other test here would catch. This fails the moment a member is added without a rank.
        foreach (var priority in Enum.GetValues<AlarmPriority>())
        {
            Assert.True(
                AlarmPriorityOrder.RankOf(priority) < int.MaxValue,
                $"{priority} has no rank of its own and would sort as unrationalised");
        }
    }

    [Fact]
    public void A_severity_is_offered_for_each_priority_and_for_nothing_else()
    {
        // ADR-0034 §5: a mapping for anything that has to speak OPC UA Part 9, never a second stored
        // field. Nothing uses it yet, which is exactly why it is pinned — the first feature that needs
        // it must not get to decide it alone.
        Assert.Equal((ushort)700, AlarmPriorityOrder.SeverityOf(AlarmPriority.High));
        Assert.Equal((ushort)500, AlarmPriorityOrder.SeverityOf(AlarmPriority.Medium));
        Assert.Equal((ushort)300, AlarmPriorityOrder.SeverityOf(AlarmPriority.Low));

        // **Not zero.** Part 9's range starts at 1, so a published 0 would be a claim about urgency
        // where this product has none to make.
        Assert.Null(AlarmPriorityOrder.SeverityOf(null));
    }

    [Fact]
    public void Every_severity_is_inside_Part_9s_range_and_leaves_room_for_a_fourth_tier()
    {
        foreach (var priority in Enum.GetValues<AlarmPriority>())
        {
            var severity = AlarmPriorityOrder.SeverityOf(priority);

            Assert.NotNull(severity);
            Assert.InRange(severity!.Value, (ushort)1, (ushort)1000);
        }

        // Gaps, for the same reason the ranks have them.
        Assert.True(AlarmPriorityOrder.SeverityOf(AlarmPriority.High) < 1000, "no room above High");
    }

    [Fact]
    public void Standing_alarms_sort_by_priority_first_and_by_time_within_one()
    {
        var alarms = new[]
        {
            Standing("oldest low", AlarmPriority.Low, minutes: 0),
            Standing("unrationalised", null, minutes: 1),
            Standing("newest low", AlarmPriority.Low, minutes: 2),
            Standing("medium", AlarmPriority.Medium, minutes: 3),
            Standing("high", AlarmPriority.High, minutes: 4),
        };

        Assert.Equal(
            ["high", "medium", "newest low", "oldest low", "unrationalised"],
            AlarmPriorityOrder.ForDisplay(alarms).Select(alarm => alarm.TagPath));
    }

    [Fact]
    public void An_unrationalised_alarm_sorts_last_even_when_it_is_the_newest()
    {
        // The case that would pass by accident if null simply sorted as Low: here the unrationalised
        // one is the most recent, so a time-first order would put it top and a null-as-Low order would
        // put it above the older Lows. It belongs last either way.
        var alarms = new[]
        {
            Standing("low", AlarmPriority.Low, minutes: 0),
            Standing("brand new, unassessed", null, minutes: 99),
        };

        Assert.Equal(
            ["low", "brand new, unassessed"],
            AlarmPriorityOrder.ForDisplay(alarms).Select(alarm => alarm.TagPath));
    }

    [Fact]
    public void A_deployment_that_has_rationalised_NOTHING_gets_exactly_the_previous_order()
    {
        // **The control, and ADR-0034 §4 asks for it by name.** Priority changes the order of a screen,
        // and a change that quietly reorders a screen nobody asked to reorder is the kind that gets
        // noticed during an incident. With every alarm unrationalised, every rank is equal and the
        // tie-break by time decides everything — which is what this line did before priority existed.
        var alarms = new[]
        {
            Standing("oldest", null, minutes: 0),
            Standing("middle", null, minutes: 5),
            Standing("newest", null, minutes: 9),
        };

        Assert.Equal(
            alarms.OrderByDescending(alarm => alarm.RaisedAtUtc).Select(alarm => alarm.TagPath),
            AlarmPriorityOrder.ForDisplay(alarms).Select(alarm => alarm.TagPath));
    }

    private static Alarm Standing(string path, AlarmPriority? priority, int minutes) => new(
        OccurrenceId: Guid.NewGuid(),
        DefinitionId: Guid.NewGuid(),
        TagId: Guid.NewGuid(),
        SiteId: Guid.NewGuid(),
        TagPath: path,
        Limit: AlarmLimit.High,
        LimitValue: 4.5,
        ValueAtRaise: 5.0,
        UnitSymbol: "bar",
        RaisedAtUtc: new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutes),
        State: AlarmState.Active,
        AcknowledgedAtUtc: null,
        ClearedAtUtc: null)
    {
        Priority = priority,
    };

    [Fact]
    public void The_order_of_the_members_is_itself_the_order_of_urgency()
    {
        // Defensive, and cheap. Someone reordering the enum for tidiness would change every
        // `OrderBy(p => p)` written anywhere without touching a rank, and the ranks above would still
        // pass. Pinning the declaration order means the two cannot disagree silently.
        Assert.Equal(
            [AlarmPriority.High, AlarmPriority.Medium, AlarmPriority.Low],
            Enum.GetValues<AlarmPriority>());
    }
}
