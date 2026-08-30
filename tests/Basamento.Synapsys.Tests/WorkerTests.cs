using System.Net;
using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basamento.Synapsys.Tests;

public sealed class WorkerTests
{
    [Fact]
    public void RejectsDuplicateNamesAndRegistrationAfterStart()
    {
        var worker = CreateWorker(new TestTransport(), enabled: false);
        worker.RegisterProgressive("duplicate", static () => { });

        Assert.Throws<SynapsysConfigurationException>(
            () => worker.RegisterEndless("duplicate", static () => { }));

        worker.StartAsync();
        Assert.Throws<InvalidOperationException>(
            () => worker.RegisterProgressive("late", static () => { }));
    }

    [Fact]
    public async Task HeartbeatReportsDotnetAndExactLongRunId()
    {
        var transport = new TestTransport();
        transport.Enqueue(TestTransport.ValidResponse(new ProcessUpdate
        {
            Name = "orders",
            DesiredState = "running",
            DesiredToken = 1,
            RunId = 9_223_372_036_854_000_001,
        }));
        var entered = NewSignal();
        var worker = CreateWorker(transport);
        worker.RegisterEndless(
            "orders",
            async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await worker.StartAsync();
        await transport.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var request = transport.Requests[0];
        Assert.Equal("dotnet", request.Framework);
        Assert.Equal("orders", Assert.Single(request.Processes).Name);
        Assert.Equal(1, worker.Processes[0].AcknowledgedToken);
        Assert.Equal(9_223_372_036_854_000_001, worker.Processes[0].RunId);

        await worker.StopAsync();
    }

    [Fact]
    public async Task MalformedSuccessfulResponseRequeuesLogs()
    {
        var transport = new TestTransport();
        transport.Enqueue(TestTransport.ValidResponse(new ProcessUpdate
        {
            Name = "orders",
            DesiredState = "running",
            DesiredToken = 1,
            RunId = 99,
        }));
        var logs = new ProcessLogHub();
        var worker = CreateWorker(transport, logs: logs);
        var entered = NewSignal();
        worker.RegisterEndless(
            "orders",
            async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await worker.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        worker.CreateLogger("orders").LogInformation("line to retry");

        transport.Enqueue(new HeartbeatTransportResult(
            HttpStatusCode.OK,
            new HeartbeatResponse { Processes = null },
            null));
        Assert.False(await worker.HeartbeatOnceAsync());
        Assert.Equal(1, logs.Count);

        transport.Enqueue(TestTransport.ValidResponse());
        Assert.True(await worker.HeartbeatOnceAsync());
        Assert.Equal(0, logs.Count);
        Assert.Single(transport.Requests[^1].Logs!);

        await worker.StopAsync();
    }

    [Fact]
    public async Task StopIsConcurrentIdempotentAndSendsOneFinalHeartbeat()
    {
        var transport = new TestTransport();
        var worker = CreateWorker(transport);
        worker.RegisterProgressive("noop", static () => { });
        await worker.StartAsync();
        await transport.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var beforeStop = transport.Requests.Count;

        await Task.WhenAll(worker.StopAsync(), worker.StopAsync(), worker.StopAsync());

        Assert.Equal(beforeStop + 1, transport.Requests.Count);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task ShutdownDoesNotExceedBudgetForIgnoredCancellation()
    {
        var transport = new TestTransport();
        transport.Enqueue(TestTransport.ValidResponse(new ProcessUpdate
        {
            Name = "stubborn",
            DesiredState = "running",
            DesiredToken = 1,
            RunId = 1,
        }));
        var entered = NewSignal();
        var never = NewSignal();
        var worker = CreateWorker(
            transport,
            shutdownTimeout: TimeSpan.FromMilliseconds(75));
        worker.RegisterEndless(
            "stubborn",
            async _ =>
            {
                entered.SetResult();
                await never.Task;
            });
        await worker.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await worker.StopAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(ProcessState.Stopping, worker.Processes[0].State);
        never.SetResult();
    }

    [Fact]
    public async Task FailFastRejectsUnreachableCore()
    {
        var transport = new TestTransport
        {
            HealthStatus = HttpStatusCode.ServiceUnavailable,
        };
        var options = OptionsTests.ValidOptions();
        options.FailFast = true;
        var worker = CreateWorker(transport, options: options);

        await Assert.ThrowsAsync<SynapsysStartupException>(
            () => worker.StartAsync(TestContext.Current.CancellationToken));
        Assert.Empty(transport.Requests);
        Assert.Equal(1, transport.DisposeCount);
    }

    private static SynapsysWorker CreateWorker(
        TestTransport transport,
        bool enabled = true,
        TimeSpan? shutdownTimeout = null,
        ProcessLogHub? logs = null,
        SynapsysOptions? options = null)
    {
        options ??= OptionsTests.ValidOptions();
        options.Enabled = enabled;
        if (shutdownTimeout is not null)
        {
            options.ShutdownTimeout = shutdownTimeout.Value;
        }

        return new SynapsysWorker(
            options,
            NullLoggerFactory.Instance,
            logs ?? new ProcessLogHub(),
            TimeProvider.System,
            () => transport);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
