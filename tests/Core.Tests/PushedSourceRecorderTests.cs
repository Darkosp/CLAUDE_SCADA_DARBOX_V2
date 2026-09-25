using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// What a pushing source reports about itself reaches the journal (ADR-0017): a loss once per
/// report, a clock skew once per episode — not with every message, and not never again.
/// </summary>
public sealed class PushedSourceRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    private static readonly Device Edge = new()
    {
        Id = new Guid("99999999-9999-4999-8999-999999999901"),
        SiteId = new Guid("99999999-9999-4999-8999-999999999902"),
        Name = "Edge",
        DriverKey = "mqtt",
        ConnectionSettings = new Dictionary<string, string>(),
        ScanInterval = TimeSpan.FromSeconds(1),
    };

    private readonly RecordingAlarmJournal _journal = new();
    private readonly StubTimeProvider _clock = new(Now);

    [Fact]
    public async Task A_skew_is_journalled_once_while_it_lasts_and_again_after_the_clocks_have_agreed()
    {
        var recorder = Recorder();

        Assert.Equal(TimeSpan.FromMinutes(10), await recorder.ObserveSourceClockAsync(Now.AddMinutes(10), CancellationToken.None));
        Assert.Null(await recorder.ObserveSourceClockAsync(Now.AddMinutes(10), CancellationToken.None));
        Assert.Null(await recorder.ObserveSourceClockAsync(Now.AddMinutes(11), CancellationToken.None));

        // Corrected, then wrong again: a new episode.
        Assert.Null(await recorder.ObserveSourceClockAsync(Now.AddSeconds(2), CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(-7), await recorder.ObserveSourceClockAsync(Now.AddMinutes(-7), CancellationToken.None));

        Assert.Equal([600d, -420d], _journal.Events.Select(e => e.ClockSkewSeconds!.Value));
        Assert.All(_journal.Events, e =>
        {
            Assert.Equal(AlarmEventType.SourceClockSkew, e.Type);
            Assert.Equal((Edge.Id, Edge.SiteId, "Site/Edge"), (e.DeviceId!.Value, e.SiteId!.Value, e.TagPath));
            Assert.Equal(Now, e.RecordedAtUtc);
        });
        Assert.Equal(Now.AddMinutes(10), _journal.Events[0].SourceTimeUtc);
    }

    [Fact]
    public async Task A_skew_within_tolerance_is_not_journalled()
    {
        var recorder = Recorder();

        Assert.Null(await recorder.ObserveSourceClockAsync(Now.AddSeconds(30), CancellationToken.None));
        Assert.Null(await recorder.ObserveSourceClockAsync(Now.AddSeconds(-30), CancellationToken.None));

        Assert.Empty(_journal.Events);
    }

    [Fact]
    public async Task A_skew_the_journal_could_not_take_is_tried_again_with_the_next_message()
    {
        var recorder = Recorder();

        _journal.Failing = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.ObserveSourceClockAsync(Now.AddMinutes(10), CancellationToken.None));

        _journal.Failing = false;
        Assert.NotNull(await recorder.ObserveSourceClockAsync(Now.AddMinutes(10), CancellationToken.None));
        Assert.Single(_journal.Events);
    }

    [Fact]
    public async Task A_loss_is_journalled_with_what_the_source_said_and_once_however_often_it_says_it()
    {
        var recorder = Recorder();
        var loss = new SourceLoss(Guid.NewGuid(), 1200, Now.AddHours(-5), Now.AddHours(-4));

        Assert.True(await recorder.RecordLossAsync(loss, CancellationToken.None));
        Assert.False(await recorder.RecordLossAsync(loss, CancellationToken.None));

        var entry = Assert.Single(_journal.Events);
        Assert.Equal(AlarmEventType.SamplesLost, entry.Type);
        Assert.Equal(
            (loss.LossId, 1200L, loss.FromSourceUtc, loss.ToSourceUtc, Edge.Id, Edge.SiteId),
            (entry.LossId!.Value, entry.LostSamples!.Value, entry.GapFromUtc!.Value, entry.GapUntilUtc!.Value, entry.DeviceId!.Value, entry.SiteId!.Value));
    }

    private PushedSourceRecorder Recorder() => new(Edge, "Site/Edge", _journal, _clock, Tolerance);
}
