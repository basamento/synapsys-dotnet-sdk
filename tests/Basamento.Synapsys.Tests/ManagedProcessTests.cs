using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basamento.Synapsys.Tests;

public sealed class ManagedProcessTests
{
    [Fact]
    public async Task SetsRunningBeforeBodyIsScheduledAndPreventsOverlap()
    {
        var release = NewSignal();
        var runs = 0;
        var process = CreateProcess(
            ProcessType.Endless,
            async _ =>
            {
                Interlocked.Increment(ref runs);
                await release.Task;
            });

        Assert.Equal(CommandOutcome.Handled, process.Start(10));
        Assert.Equal(ProcessState.Running, process.Status.State);
        Assert.Equal(CommandOutcome.Handled, process.Start(10));

        release.SetResult();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runs);
        Assert.Equal(ProcessState.Idle, process.Status.State);
    }

    [Fact]
    public async Task DefersStartWhileStoppingAndPreservesRunId()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var process = CreateProcess(
            ProcessType.Endless,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
            });

        process.Start(9_223_372_036_854_000_001);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.Stop();

        Assert.Equal(CommandOutcome.Deferred, process.Start(9_223_372_036_854_000_002));
        Assert.Equal(9_223_372_036_854_000_001, process.Status.RunId);
        Assert.Equal(ProcessState.Stopping, process.Status.State);

        release.SetResult();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandOutcome.Handled, process.Start(9_223_372_036_854_000_002));
    }

    [Fact]
    public async Task CooperativeCancellationReturnsToIdle()
    {
        var entered = NewSignal();
        var process = CreateProcess(
            ProcessType.Endless,
            async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        process.Start(42);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.Stop();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ProcessState.Idle, process.Status.State);
    }

    [Fact]
    public async Task UnhandledExceptionBecomesFailedAndIsObserved()
    {
        var process = CreateProcess(
            ProcessType.Progressive,
            _ => throw new InvalidOperationException("boom"));

        process.Start(7);
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ProcessState.Failed, process.Status.State);
    }

    [Fact]
    public async Task EndlessCleanupRunsOnceAndDoesNotOverlapBody()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var cleanupCalls = 0;
        var process = CreateProcess(
            ProcessType.Endless,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
            },
            () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                release.TrySetResult();
                return Task.CompletedTask;
            });

        process.Start(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.Stop();
        process.Stop();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cleanupCalls);
        Assert.Equal(ProcessState.Idle, process.Status.State);
    }

    [Fact]
    public async Task IgnoredCancellationRemainsStoppingUntilBodyReturns()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var process = CreateProcess(
            ProcessType.Endless,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
            });

        process.Start(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.Stop();

        Assert.Equal(ProcessState.Stopping, process.Status.State);
        Assert.True(process.IsInFlight);

        release.SetResult();
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessState.Idle, process.Status.State);
    }

    private static ManagedProcess CreateProcess(
        ProcessType type,
        Func<CancellationToken, Task> body,
        Func<Task>? cleanup = null)
    {
        var logs = new ProcessLogHub();
        logs.Configure(true, TimeProvider.System);
        var process = new ManagedProcess(
            new ProcessDeclaration("test-process", type, body, cleanup),
            logs,
            NullLogger.Instance);
        logs.Register(process);
        return process;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

