using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0017 and ADR-0018 on the buffer itself: bounded, dropping oldest first, and never silent
/// about what it dropped — the delete, the recorded window and the cursor are one transaction.
/// </summary>
public sealed class SampleBufferTests : IDisposable
{
    private static readonly Guid Tag = new("66666666-6666-4666-8666-666666666601");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_full_buffer_drops_its_oldest_and_records_exactly_what_it_lost()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 10);

        buffer.Append(Samples(0, 15));

        var account = buffer.Account();
        Assert.Empty(account.Problems());
        Assert.Equal((Appended: 15L, Pending: 10L, Lost: 5L, Cursor: 5L), (account.Appended, account.Pending, account.Lost, account.Cursor));

        // The oldest five, and the source time they spanned.
        var window = Assert.Single(buffer.LostWindows());
        Assert.Equal((1L, 5L, 5L), (window.FromSequence, window.ToSequence, window.Count));
        Assert.Equal(Start, window.FromSourceUtc);
        Assert.Equal(Start.AddSeconds(4), window.ToSourceUtc);

        // And what waits is the newest ten, in order.
        Assert.Equal(Enumerable.Range(6, 10).Select(i => (long)i), buffer.Peek(100).Select(sample => sample.Sequence));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("cursor moved")]
    public void A_crash_in_the_middle_of_a_drop_leaves_the_buffer_as_it_was(string crashAfter)
    {
        using (var buffer = SampleBuffer.Open(_path, maxPending: 10))
        {
            buffer.Append(Samples(0, 10));

            // The next append overflows; the process dies part-way through the drop.
            buffer.BetweenDropSteps = step =>
            {
                if (step == crashAfter)
                {
                    throw new SimulatedCrash();
                }
            };

            Assert.Throws<SimulatedCrash>(() => buffer.Append(Samples(10, 5)));
        }

        // Reopened, as the next start of the agent would.
        using var reopened = SampleBuffer.Open(_path, maxPending: 10);
        var account = reopened.Account();

        // Everything is accounted for: nothing is gone that is neither waiting, acknowledged nor
        // recorded lost, and the cursor has not passed anything it cannot account for. Split into
        // separate transactions, this is where the buffer claims to have sent what it dropped.
        Assert.Empty(account.Problems());

        // Exactly as before the append: the whole append and its drop are one transaction.
        Assert.Equal((Appended: 10L, Pending: 10L, Lost: 0L, Cursor: 0L), (account.Appended, account.Pending, account.Lost, account.Cursor));
        Assert.Empty(reopened.LostWindows());
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), reopened.Peek(100).Select(sample => sample.Sequence));
    }

    [Fact]
    public void Acknowledged_samples_leave_and_the_accounts_still_add_up()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 100);
        buffer.Append(Samples(0, 20));

        var sent = buffer.Peek(8);
        buffer.Acknowledge(sent[^1].Sequence);

        var account = buffer.Account();
        Assert.Empty(account.Problems());
        Assert.Equal((Pending: 12L, Acknowledged: 8L, Cursor: 8L), (account.Pending, account.Acknowledged, account.Cursor));
        Assert.Equal(9L, buffer.Peek(1)[0].Sequence);
    }

    [Fact]
    public void Every_kind_of_value_comes_back_exactly_as_it_went_in()
    {
        var samples = new[]
        {
            new TagReading(Tag, new TagValue.Numeric(4.25), Start.AddTicks(7), Quality.Good),
            new TagReading(Tag, new TagValue.Boolean(true), Start.AddSeconds(1), Quality.Uncertain),
            new TagReading(Tag, new TagValue.Text("Pump 3 tripped"), Start.AddSeconds(2), Quality.Good),
            new TagReading(Tag, new TagValue.Discrete(4, "Running"), Start.AddSeconds(3), Quality.Good),
            new TagReading(Tag, new TagValue.Discrete(5), Start.AddSeconds(4), Quality.Good),
            new TagReading(Tag, null, Start.AddSeconds(5), Quality.Bad),
        };

        using var buffer = SampleBuffer.Open(_path, maxPending: 100);
        buffer.Append(samples);

        Assert.Equal(samples, buffer.Peek(100).Select(sample => sample.Reading));
    }

    private static List<TagReading> Samples(int first, int count) =>
        Enumerable.Range(first, count)
            .Select(i => new TagReading(Tag, new TagValue.Numeric(i), Start.AddSeconds(i), Quality.Good))
            .ToList();

    private sealed class SimulatedCrash : Exception;
}
