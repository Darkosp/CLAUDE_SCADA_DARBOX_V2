using System.Collections.Concurrent;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// Stands in for a protocol driver: every read returns a fresh Good value, and every write
/// is recorded.
/// </summary>
/// <remarks>
/// Registered under the same key as the demo device's real driver, so the seeded
/// configuration scans without a simulator. Values change on every read, so each scan
/// produces a push — a test waiting for "no more values" is then waiting on something that
/// would otherwise certainly arrive.
/// </remarks>
public sealed class FakeDriverFactory(string driverKey) : IDeviceDriverFactory
{
    public const string Key = "modbus-tcp";

    /// <summary>A second driver key, for devices that must not be served by <see cref="Key"/>.</summary>
    public const string OtherKey = "fake-other";

    private long _reads;

    public string DriverKey { get; } = driverKey;

    public ConcurrentQueue<RecordedWrite> Writes { get; } = new();

    public IDeviceDriver Create(Device device) => new FakeDriver(this, device);

    public sealed record RecordedWrite(Guid DeviceId, DriverTag Tag, TagValue Value);

    private sealed class FakeDriver(FakeDriverFactory factory, Device device) : IDeviceDriver
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TagReading>> ReadAsync(IReadOnlyList<DriverTag> tags, CancellationToken cancellationToken)
        {
            var reading = Interlocked.Increment(ref factory._reads);
            var now = DateTimeOffset.UtcNow;

            return Task.FromResult<IReadOnlyList<TagReading>>(tags
                .Select(tag => new TagReading(
                    tag.TagId,
                    tag.ValueKind == TagValueKind.Boolean ? new TagValue.Boolean(reading % 2 == 0) : new TagValue.Numeric(reading),
                    now,
                    Quality.Good))
                .ToList());
        }

        public Task WriteAsync(DriverTag tag, TagValue value, CancellationToken cancellationToken)
        {
            factory.Writes.Enqueue(new RecordedWrite(device.Id, tag, value));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
