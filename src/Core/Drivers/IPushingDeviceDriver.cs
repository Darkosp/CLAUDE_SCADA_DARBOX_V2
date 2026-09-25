using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Drivers;

/// <summary>
/// A connection to a device, or a link, that <em>pushes</em>: samples arrive when the source
/// sends them, singly or in batches, possibly minutes after they were measured (ADR-0016).
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no method to read a value. A pushing source has no answer to "what is
/// it now" — only what arrived, or nothing — and a cached last value answered on request would
/// invent the one thing this system refuses to invent: a value nobody measured (ADR-0003).
/// </para>
/// <para>
/// There is no scan interval either. What a pushing driver declares instead is how long
/// silence may last before it means loss: past <see cref="StalenessLimit"/>, a tag that has
/// received nothing reads Bad, with the time of its last real sample, until something arrives.
/// </para>
/// <para>Implemented by driver modules, never by core (ADR-0002).</para>
/// </remarks>
public interface IPushingDeviceDriver : IAsyncDisposable
{
    /// <summary>
    /// How long a tag may go without a sample before its silence reads as loss.
    /// </summary>
    TimeSpan StalenessLimit { get; }

    /// <summary>
    /// Runs the driver until <paramref name="cancellationToken"/> is cancelled, handing every
    /// sample for <paramref name="tags"/> to <paramref name="sink"/> as it arrives.
    /// </summary>
    /// <remarks>
    /// Reconnecting after a lost link is the driver's own business; while it is down it simply
    /// hands nothing over, and the staleness rule makes that visible. Each sample carries the
    /// source's own timestamp and quality (ADR-0003), never the time it arrived.
    /// </remarks>
    Task RunAsync(IReadOnlyList<DriverTag> tags, IPushedSampleSink sink, CancellationToken cancellationToken);
}

/// <summary>Where a pushing driver hands over what arrived.</summary>
public interface IPushedSampleSink
{
    /// <summary>
    /// Accepts one or many samples, in any order and possibly older than ones already
    /// accepted. Late is not wrong: every sample is kept in history, and a late one never
    /// moves a tag's current value backwards in time (ADR-0016).
    /// </summary>
    Task AcceptAsync(IReadOnlyList<TagReading> samples, CancellationToken cancellationToken);

    /// <summary>
    /// The source reports samples it measured but could not deliver — a buffer that filled
    /// during an outage and dropped its oldest (ADR-0017). Recorded, so the hole in history is a
    /// stated one; the same report arriving again is recorded once.
    /// </summary>
    Task ReportLossAsync(SourceLoss loss, CancellationToken cancellationToken);

    /// <summary>
    /// The source says what its clock read when it sent what just arrived. Compared with the
    /// Gateway's own clock; a disagreement beyond tolerance is recorded, and no sample's time is
    /// changed because of it (ADR-0017).
    /// </summary>
    Task ReportSourceClockAsync(DateTimeOffset sourceClockUtc, CancellationToken cancellationToken);
}

/// <summary>Samples a source dropped rather than delivered (ADR-0017).</summary>
/// <param name="LossId">The source's own id for this loss: the key that makes a repeated report one entry.</param>
/// <param name="Count">How many samples; at least one.</param>
/// <param name="FromSourceUtc">The source time of the oldest sample dropped.</param>
/// <param name="ToSourceUtc">The source time of the newest sample dropped.</param>
public sealed record SourceLoss(Guid LossId, long Count, DateTimeOffset FromSourceUtc, DateTimeOffset ToSourceUtc);

/// <summary>
/// Creates pushing drivers for the devices of one source. Matched to a device by its opaque
/// driver key, like <see cref="IDeviceDriverFactory"/>, and registered at compile time (ADR-0002).
/// </summary>
public interface IPushingDeviceDriverFactory
{
    /// <summary>The value of <see cref="Device.DriverKey"/> this factory serves.</summary>
    string DriverKey { get; }

    /// <summary>Creates a driver for one device from that device's connection settings.</summary>
    IPushingDeviceDriver Create(Device device);
}
