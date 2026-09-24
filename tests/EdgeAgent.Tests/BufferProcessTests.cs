using System.Diagnostics;
using ScadaDarbox.EdgeAgent.Buffer;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0017 and ADR-0018 across processes: another process writes the buffer and is killed for
/// real, or exits; this one reopens what it left.
/// </summary>
/// <remarks>
/// <see cref="Process.Kill(bool)"/> is SIGKILL on Linux and TerminateProcess on Windows: the
/// writer gets no chance to finish, roll back or close anything.
/// </remarks>
public sealed class BufferProcessTests : IDisposable
{
    private const int BatchSize = 200;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Killed_in_the_middle_of_a_batch_the_buffer_reopens_with_only_whole_batches()
    {
        // Slowly: one row every 2 ms, so the kill lands inside a transaction, not between two.
        using var writer = StartWriter("slow", _path, BatchSize.ToString(), "2");
        await WaitForLineAsync(writer, "batch 3 half");

        writer.Kill(entireProcessTree: true);
        await writer.WaitForExitAsync();

        using var buffer = SampleBuffer.Open(_path, maxPending: long.MaxValue);

        Assert.Equal("ok", buffer.IntegrityCheck());
        Assert.Empty(buffer.Account().Problems());

        // What would be sent: every sample of batch k carries the value k.
        var waiting = buffer.Peek(int.MaxValue);
        var perBatch = waiting
            .GroupBy(sample => (int)((Core.Model.TagValue.Numeric)sample.Reading.Value!).Value)
            .ToDictionary(group => group.Key, group => group.Count());

        // Batches 1 and 2 committed whole; batch 3 was half written when the process died, and
        // none of it is there — nothing half-written is waiting to be sent as if it were whole.
        Assert.Equal(new Dictionary<int, int> { [1] = BatchSize, [2] = BatchSize }, perBatch);
        Assert.Equal(2L * BatchSize, buffer.Account().Appended);
    }

    [Fact]
    public async Task What_one_process_buffered_the_next_reads_back_unchanged()
    {
        using (var writer = StartWriter("known", _path, "4", "50"))
        {
            await writer.WaitForExitAsync();
            Assert.Equal(0, writer.ExitCode);
        }

        var expected = Enumerable.Range(1, 4).SelectMany(k => KnownContent.Batch(k, 50)).ToList();

        // Read by this process, then again after another open: a restart changes nothing.
        for (var restart = 0; restart < 2; restart++)
        {
            using var buffer = SampleBuffer.Open(_path, maxPending: long.MaxValue);
            Assert.Equal(expected, buffer.Peek(int.MaxValue).Select(sample => sample.Reading));
            Assert.Empty(buffer.Account().Problems());
        }
    }

    private static Process StartWriter(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ScadaDarbox.EdgeAgent.CrashWriter.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private static async Task WaitForLineAsync(Process process, string line)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } read)
        {
            if (read == line)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The writer ended before '{line}': {await process.StandardError.ReadToEndAsync()}");
    }
}
