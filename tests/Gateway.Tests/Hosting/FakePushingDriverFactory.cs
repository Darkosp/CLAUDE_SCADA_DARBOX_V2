using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// A stand-in pushing source (ADR-0016): when started it hands over one sample per tag, then
/// goes silent — the shape of a link that dropped after its last message.
/// </summary>
public sealed class FakePushingDriverFactory : IPushingDeviceDriverFactory
{
    public const string Key = "fake-push";

    /// <summary>Short, so a test sees silence become loss in about a second.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>The value every first sample carries.</summary>
    public const double FirstValue = 7.5;

    public string DriverKey => Key;

    public IPushingDeviceDriver Create(Device device) => new Driver();

    private sealed class Driver : IPushingDeviceDriver
    {
        public TimeSpan StalenessLimit => Limit;

        public async Task RunAsync(IReadOnlyList<DriverTag> tags, IPushedSampleSink sink, CancellationToken cancellationToken)
        {
            // Measured a moment before it arrived, as anything that crossed a link was.
            var measuredAt = DateTimeOffset.UtcNow.AddMilliseconds(-500);
            await sink.AcceptAsync(
                tags.Select(tag => new TagReading(tag.TagId, new TagValue.Numeric(FirstValue), measuredAt, Quality.Good)).ToList(),
                cancellationToken);

            // Then nothing, until stopped.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
