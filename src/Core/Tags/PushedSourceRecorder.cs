using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// Journals what one pushing device reports about itself rather than about its tags: samples
/// it dropped, and a clock that disagrees with the Gateway's (ADR-0017).
/// </summary>
/// <remarks>
/// Both are written to the journal (ADR-0013) against the device and its Site, so a hole in
/// history or a trend that looks shifted has its cause beside it. Neither changes a sample.
/// </remarks>
public sealed class PushedSourceRecorder
{
    private readonly Guid _deviceId;
    private readonly Guid _siteId;
    private readonly string _devicePath;
    private readonly IAlarmJournal _journal;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _skewTolerance;

    private readonly Lock _gate = new();
    private bool _skewReported;

    public PushedSourceRecorder(
        Device device,
        string devicePath,
        IAlarmJournal journal,
        TimeProvider clock,
        TimeSpan skewTolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(skewTolerance, TimeSpan.Zero);

        _deviceId = device.Id;
        _siteId = device.SiteId;
        _devicePath = devicePath;
        _journal = journal;
        _clock = clock;
        _skewTolerance = skewTolerance;
    }

    /// <summary>
    /// Journals a loss the source reported, once however often it is reported.
    /// </summary>
    /// <returns>True if this report was recorded now; false if it already had been.</returns>
    public Task<bool> RecordLossAsync(SourceLoss loss, CancellationToken cancellationToken) =>
        _journal.AppendLossOnceAsync(
            new AlarmEvent
            {
                Type = AlarmEventType.SamplesLost,
                RecordedAtUtc = _clock.GetUtcNow(),
                SiteId = _siteId,
                DeviceId = _deviceId,
                TagPath = _devicePath,
                GapFromUtc = loss.FromSourceUtc,
                GapUntilUtc = loss.ToSourceUtc,
                LostSamples = loss.Count,
                LossId = loss.LossId,
            },
            cancellationToken);

    /// <summary>
    /// Compares the source's clock with the Gateway's and journals a disagreement beyond the
    /// tolerance — once when it begins, not with every message, and again only after the clocks
    /// have agreed in between.
    /// </summary>
    /// <returns>The disagreement journalled by this call, or null if nothing was journalled.</returns>
    /// <remarks>
    /// The comparison is with the moment the message arrived, so it includes the time the message
    /// took to get here. The Gateway's subscription does not ask the broker to hold messages while
    /// it is away, so that time is transit alone — seconds, well inside a tolerance of tens of
    /// seconds.
    /// </remarks>
    public async Task<TimeSpan?> ObserveSourceClockAsync(DateTimeOffset sourceClockUtc, CancellationToken cancellationToken)
    {
        var receivedAt = _clock.GetUtcNow();
        var skew = sourceClockUtc - receivedAt;

        lock (_gate)
        {
            if (skew.Duration() <= _skewTolerance)
            {
                _skewReported = false;
                return null;
            }

            if (_skewReported)
            {
                return null;
            }

            _skewReported = true;
        }

        try
        {
            await _journal.AppendAsync(
                new AlarmEvent
                {
                    Type = AlarmEventType.SourceClockSkew,
                    RecordedAtUtc = receivedAt,
                    SourceTimeUtc = sourceClockUtc,
                    SiteId = _siteId,
                    DeviceId = _deviceId,
                    TagPath = _devicePath,
                    ClockSkewSeconds = skew.TotalSeconds,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not recorded, so not reported: the next message tries again.
            lock (_gate)
            {
                _skewReported = false;
            }

            throw;
        }

        return skew;
    }
}
