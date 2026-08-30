using System.Globalization;
using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basamento.Synapsys.Tests;

public sealed class ProcessLoggingTests
{
    [Fact]
    public async Task LoggerAddsExactRunIdSequenceAndStructuredValues()
    {
        var release = NewSignal();
        var logs = new ProcessLogHub();
        logs.Configure(true, TimeProvider.System);
        var process = new ManagedProcess(
            new ProcessDeclaration("orders", ProcessType.Endless, _ => release.Task, null),
            logs,
            NullLogger.Instance);
        logs.Register(process);
        process.Start(9_223_372_036_854_000_001);
        var logger = new ProcessCaptureLogger(logs, "orders", null);

        logger.LogInformation("Processing order {OrderId}", 42);
        var batch = logs.Drain();

        var entry = Assert.Single(batch);
        Assert.Equal(9_223_372_036_854_000_001, entry.RunId);
        Assert.Equal(1, entry.Sequence);
        Assert.Contains("OrderId=42", entry.Message, StringComparison.Ordinal);

        release.SetResult();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void QueueDropsOldestAtBoundAndRequeuesAtFront()
    {
        var queue = new ProcessLogQueue();
        for (var index = 0; index <= ProcessLogHub.MaximumEntries; index++)
        {
            queue.Enqueue(Entry(index + 1, index.ToString(CultureInfo.InvariantCulture)));
        }

        Assert.Equal(ProcessLogHub.MaximumEntries, queue.Count);
        Assert.Equal(1, queue.DroppedCount);

        var firstBatch = queue.Drain(2, int.MaxValue);
        Assert.Equal(2, firstBatch.Count);
        Assert.Equal(2, firstBatch[0].Sequence);
        queue.RequeueFront(firstBatch);
        Assert.Equal(2, queue.Drain(1, int.MaxValue)[0].Sequence);
    }

    [Fact]
    public void BatchUsesUtf8MessageBudgetAndStillAllowsOneOversizedEntry()
    {
        var queue = new ProcessLogQueue();
        queue.Enqueue(Entry(1, new string('é', 10)));
        queue.Enqueue(Entry(2, "next"));

        var first = queue.Drain(100, 5);
        Assert.Single(first);
        Assert.Equal(1, first[0].Sequence);
        Assert.Single(queue.Drain(100, 5));
    }

    [Fact]
    public async Task WriterFlushesPartialLineWhenProcessReturns()
    {
        TextWriter? writer = null;
        var logs = new ProcessLogHub();
        logs.Configure(true, TimeProvider.System);
        var process = new ManagedProcess(
            new ProcessDeclaration(
                "writer-process",
                ProcessType.Progressive,
                _ =>
                {
                    writer!.Write("partial line");
                    return Task.CompletedTask;
                },
                null),
            logs,
            NullLogger.Instance);
        logs.Register(process);
        writer = logs.CreateWriter("writer-process", SynapsysLogSource.StandardOutput, null);

        process.Start(5);
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("partial line", Assert.Single(logs.Drain()).Message);
    }

    private static ProcessLogEntry Entry(long sequence, string message) => new()
    {
        ProcessName = "process",
        RunId = 1,
        Sequence = sequence,
        Timestamp = DateTimeOffset.UtcNow,
        Source = "stdout",
        Level = "info",
        Message = message,
    };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
