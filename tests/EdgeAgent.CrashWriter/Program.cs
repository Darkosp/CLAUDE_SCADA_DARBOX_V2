using System.Globalization;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;

// Writes the edge buffer in a separate process, so it can be killed for real.
//
//   slow <path> <batchSize> <rowDelayMs>   append batches for ever, slowly, announcing each on stdout
//   known <path> <batches> <batchSize>     append a known, deterministic content, then exit
//
// Every sample in batch k carries the value k, so a reader can tell whether a batch is whole.

var mode = args[0];
var path = args[1];
using var buffer = SampleBuffer.Open(path, maxPending: long.MaxValue);

if (mode == "known")
{
    var batches = int.Parse(args[2], CultureInfo.InvariantCulture);
    var size = int.Parse(args[3], CultureInfo.InvariantCulture);
    for (var k = 1; k <= batches; k++)
    {
        buffer.Append(KnownContent.Batch(k, size));
    }

    Console.WriteLine("known content written");
    return 0;
}

var batchSize = int.Parse(args[2], CultureInfo.InvariantCulture);
var rowDelay = int.Parse(args[3], CultureInfo.InvariantCulture);
var current = 0;

buffer.AfterRowWritten = row =>
{
    if (row == batchSize / 2)
    {
        Console.WriteLine($"batch {current} half");
        Console.Out.Flush();
    }

    Thread.Sleep(rowDelay);
};

for (current = 1; ; current++)
{
    Console.WriteLine($"batch {current} begin");
    Console.Out.Flush();
    buffer.Append(Enumerable.Range(0, batchSize)
        .Select(row => new TagReading(
            KnownContent.Tag,
            new TagValue.Numeric(current),
            KnownContent.Start.AddSeconds(current).AddTicks(row),
            Quality.Good))
        .ToList());
    Console.WriteLine($"batch {current} committed");
    Console.Out.Flush();
}

/// <summary>The content "known" writes; the tests build the same list to compare against.</summary>
public static class KnownContent
{
    public static readonly Guid Tag = new("55555555-5555-4555-8555-555555555501");
    public static readonly Guid Other = new("55555555-5555-4555-8555-555555555502");
    public static readonly DateTimeOffset Start = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Every kind of value, and a Bad sample with none.</summary>
    public static IReadOnlyList<TagReading> Batch(int k, int size) =>
        Enumerable.Range(0, size).Select(row => (row % 5) switch
        {
            0 => new TagReading(Tag, new TagValue.Numeric(k + row / 1000.0), Start.AddSeconds(k * 1000 + row).AddTicks(7), Quality.Good),
            1 => new TagReading(Other, new TagValue.Boolean(row % 2 == 0), Start.AddSeconds(k * 1000 + row), Quality.Uncertain),
            2 => new TagReading(Other, new TagValue.Text($"batch {k} row {row}"), Start.AddSeconds(k * 1000 + row), Quality.Good),
            3 => new TagReading(Other, new TagValue.Discrete(row, row % 2 == 0 ? "Running" : null), Start.AddSeconds(k * 1000 + row), Quality.Good),
            _ => new TagReading(Tag, null, Start.AddSeconds(k * 1000 + row), Quality.Bad),
        }).ToList();
}
