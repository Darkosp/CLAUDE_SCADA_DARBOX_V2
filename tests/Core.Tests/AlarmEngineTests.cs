using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

public class AlarmEngineTests
{
    private static readonly Guid TagId = new("11111111-2222-4111-8111-111111111111");
    private static readonly Guid DefinitionId = new("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_value_at_or_above_the_high_limit_raises_an_alarm()
    {
        var (engine, _) = Build(high: 8.0);

        await Feed(engine, 8.0);

        var alarm = Assert.Single(engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(AlarmLimit.High, alarm.Limit);
        Assert.Equal(8.0, alarm.LimitValue);
        Assert.Equal("Skopje/Pump House/Discharge Pressure", alarm.TagPath);
        Assert.Equal("bar", alarm.UnitSymbol);
    }

    [Fact]
    public async Task A_value_at_or_below_the_low_limit_raises_an_alarm()
    {
        var (engine, _) = Build(low: 2.0);

        await Feed(engine, 1.5);

        Assert.Equal(AlarmLimit.Low, Assert.Single(engine.GetCurrent()).Limit);
    }

    [Fact]
    public async Task A_value_inside_the_limits_raises_nothing()
    {
        var (engine, subscriber) = Build(high: 8.0, low: 2.0);

        await Feed(engine, 4.2);

        Assert.Empty(engine.GetCurrent());
        Assert.Empty(subscriber.Publications);
    }

    [Fact]
    public async Task A_bad_reading_raises_no_alarm()
    {
        // A Bad reading carries no value (ADR-0003). There is nothing to compare, so
        // inventing an alarm from it would be as wrong as inventing the value.
        var (engine, _) = Build(high: 8.0);

        await Feed(engine, value: null, quality: Quality.Bad);

        Assert.Empty(engine.GetCurrent());
    }

    [Fact]
    public async Task A_device_going_offline_does_not_clear_a_standing_alarm()
    {
        // The case worth guarding hardest: if losing the device looked like the value
        // returning to normal, an alarm would disappear at exactly the moment the
        // operator has least information.
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);

        await Feed(engine, value: null, quality: Quality.Bad);

        var alarm = Assert.Single(engine.GetCurrent());
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Null(alarm.ClearedAtUtc);
    }

    [Fact]
    public async Task An_unacknowledged_alarm_stays_listed_after_the_value_recovers()
    {
        // An excursion that corrected itself is precisely what an operator who stepped
        // away needs to see. Dropping it would erase the only record it happened.
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);

        await Feed(engine, 4.0);

        var alarm = Assert.Single(engine.GetCurrent());
        Assert.Equal(AlarmState.Cleared, alarm.State);
        Assert.Equal(9.0, alarm.ValueAtRaise);
        Assert.NotNull(alarm.ClearedAtUtc);
    }

    [Fact]
    public async Task An_acknowledged_alarm_disappears_once_the_value_recovers()
    {
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);
        await engine.AcknowledgeAsync(DefinitionId, Now, CancellationToken.None);

        await Feed(engine, 4.0);

        Assert.Empty(engine.GetCurrent());
    }

    [Fact]
    public async Task Acknowledging_an_active_alarm_marks_it_seen_without_removing_it()
    {
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);

        Assert.True(await engine.AcknowledgeAsync(DefinitionId, Now, CancellationToken.None));

        var alarm = Assert.Single(engine.GetCurrent());
        Assert.Equal(AlarmState.Acknowledged, alarm.State);
        Assert.Equal(Now, alarm.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Acknowledging_a_recovered_alarm_retires_it()
    {
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);
        await Feed(engine, 4.0);

        Assert.True(await engine.AcknowledgeAsync(DefinitionId, Now, CancellationToken.None));

        Assert.Empty(engine.GetCurrent());
    }

    [Fact]
    public async Task Acknowledging_something_that_is_not_in_alarm_reports_failure()
    {
        var (engine, _) = Build(high: 8.0);

        Assert.False(await engine.AcknowledgeAsync(DefinitionId, Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_shelved_alarm_stays_shelved_while_the_value_is_still_out_of_range()
    {
        var (engine, _) = Build(high: 8.0);
        await Feed(engine, 9.0);
        await engine.ShelveAsync(DefinitionId, CancellationToken.None);

        await Feed(engine, 9.5);

        Assert.Equal(AlarmState.Shelved, Assert.Single(engine.GetCurrent()).State);
    }

    [Fact]
    public async Task A_standing_alarm_is_not_republished_on_every_scan()
    {
        // Without this the banner would be rewritten once a second for as long as a
        // value stayed out of range, and every client would be woken for nothing.
        var (engine, subscriber) = Build(high: 8.0);

        await Feed(engine, 9.0);
        await Feed(engine, 9.1);
        await Feed(engine, 9.2);

        Assert.Single(subscriber.Publications);
    }

    [Fact]
    public async Task A_tag_with_no_alarm_configured_is_ignored()
    {
        var (engine, subscriber) = Build(high: null, low: null, configured: false);

        await Feed(engine, 9999.0);

        Assert.Empty(engine.GetCurrent());
        Assert.Empty(subscriber.Publications);
    }

    private static Task Feed(AlarmEngine engine, double? value, Quality quality = Quality.Good) =>
        engine.OnTagValuesAsync(
            [
                new TagSnapshot(
                    TagId,
                    "Skopje/Pump House/Discharge Pressure",
                    value is null ? null : new TagValue.Numeric(value.Value),
                    Now,
                    quality,
                    "bar")
            ],
            CancellationToken.None).AsTask();

    private static (AlarmEngine Engine, RecordingAlarmSubscriber Subscriber) Build(
        double? high = null,
        double? low = null,
        bool configured = true)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Darbo" };
        var site = new Site { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Skopje" };
        var device = new Device
        {
            Id = Guid.NewGuid(),
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

        var catalog = new TagCatalog(tenant, [site], [], [device], [tag], alarms);
        var subscriber = new RecordingAlarmSubscriber();

        return (new AlarmEngine(new TagCatalogSource(catalog), [subscriber]), subscriber);
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
