using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Basamento.Synapsys.Internal;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The per-run cancellation source is disposed at execution completion or replacement.")]
internal sealed class ManagedProcess
{
    private readonly object _gate = new();
    private readonly ProcessDeclaration _declaration;
    private readonly ProcessLogHub _logs;
    private readonly ILogger _logger;
    private ProcessState _state = ProcessState.Idle;
    private long _acknowledgedToken;
    private long? _runId;
    private long _sequence;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Task? _cleanupTask;
    private bool _stopRequested;
    private bool _cleanupStarted;

    internal ManagedProcess(
        ProcessDeclaration declaration,
        ProcessLogHub logs,
        ILogger logger)
    {
        _declaration = declaration;
        _logs = logs;
        _logger = logger;
    }

    internal string Name => _declaration.Name;

    internal ProcessType Type => _declaration.Type;

    internal ProcessStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new ProcessStatus(
                    Name,
                    Type,
                    _state,
                    _acknowledgedToken,
                    _runId);
            }
        }
    }

    internal HeartbeatProcess Snapshot()
    {
        var status = Status;
        return new HeartbeatProcess
        {
            Name = status.Name,
            Type = ToProtocol(status.Type),
            CurrentState = ToProtocol(status.State),
            AcknowledgedToken = status.AcknowledgedToken,
            RunId = status.RunId,
        };
    }

    internal long AcknowledgedToken
    {
        get
        {
            lock (_gate)
            {
                return _acknowledgedToken;
            }
        }
    }

    internal void Acknowledge(long token)
    {
        lock (_gate)
        {
            if (token > _acknowledgedToken)
            {
                _acknowledgedToken = token;
            }
        }
    }

    internal void SynchronizeRunId(long? runId)
    {
        lock (_gate)
        {
            if (_state is ProcessState.Idle or ProcessState.Failed || _runId == runId)
            {
                _runId = runId;
            }
        }
    }

    internal CommandOutcome Start(long? runId)
    {
        lock (_gate)
        {
            if (_state == ProcessState.Running)
            {
                return CommandOutcome.Handled;
            }

            if (_state == ProcessState.Stopping ||
                (_runTask is not null && !_runTask.IsCompleted))
            {
                return CommandOutcome.Deferred;
            }

            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            _runId = runId;
            _sequence = 0;
            _stopRequested = false;
            _cleanupStarted = false;
            _cleanupTask = null;
            _state = ProcessState.Running;
            _runTask = ExecuteAsync(_runCancellation.Token);
            return CommandOutcome.Handled;
        }
    }

    internal CommandOutcome Stop()
    {
        Task? cleanup = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_state is ProcessState.Idle or ProcessState.Failed or ProcessState.Stopping)
            {
                return CommandOutcome.Handled;
            }

            if (_state != ProcessState.Running)
            {
                return CommandOutcome.Deferred;
            }

            _state = ProcessState.Stopping;
            _stopRequested = true;
            cancellation = _runCancellation;
            if (_declaration.Type == ProcessType.Endless &&
                _declaration.CleanupAsync is not null &&
                !_cleanupStarted)
            {
                _cleanupStarted = true;
                cleanup = Task.Run(InvokeCleanupAsync, CancellationToken.None);
                _cleanupTask = cleanup;
            }
        }

        cancellation?.Cancel();
        Observe(cleanup);
        return CommandOutcome.Handled;
    }

    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _runTask ?? Task.CompletedTask;
            }
        }
    }

    internal bool IsInFlight
    {
        get
        {
            lock (_gate)
            {
                return _runTask is not null && !_runTask.IsCompleted;
            }
        }
    }

    internal void MarkCommandFailure(Exception exception)
    {
        lock (_gate)
        {
            _state = ProcessState.Failed;
        }

        CaptureException(exception);
    }

    internal bool TryNextLogContext(out long runId, out long sequence)
    {
        lock (_gate)
        {
            if (_state is not (ProcessState.Running or ProcessState.Stopping) || _runId is null)
            {
                runId = 0;
                sequence = 0;
                return false;
            }

            runId = _runId.Value;
            sequence = ++_sequence;
            return true;
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var failed = false;
        using (_logs.Enter(this))
        {
            try
            {
                await _declaration.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (WasStopRequested())
            {
                // Cooperative cancellation is a successful stop.
            }
            catch (Exception exception)
            {
                failed = true;
                CaptureException(exception);
                SynapsysDiagnostics.ProcessFailed(_logger, Name, exception);
            }

            Task? cleanup = null;
            lock (_gate)
            {
                if (_stopRequested &&
                    _declaration.Type == ProcessType.Progressive &&
                    _declaration.CleanupAsync is not null &&
                    !_cleanupStarted)
                {
                    _cleanupStarted = true;
                    cleanup = Task.Run(InvokeCleanupAsync, CancellationToken.None);
                    _cleanupTask = cleanup;
                }
                else
                {
                    cleanup = _cleanupTask;
                }
            }

            if (cleanup is not null)
            {
                await cleanup.ConfigureAwait(false);
            }

            _logs.FlushWriters(Name);
        }

        lock (_gate)
        {
            _state = failed ? ProcessState.Failed : ProcessState.Idle;
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private bool WasStopRequested()
    {
        lock (_gate)
        {
            return _stopRequested;
        }
    }

    private async Task InvokeCleanupAsync()
    {
        try
        {
            if (_declaration.CleanupAsync is not null)
            {
                await _declaration.CleanupAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            CaptureException(exception);
            SynapsysDiagnostics.CleanupFailed(_logger, Name, exception);
        }
    }

    private void CaptureException(Exception exception)
    {
        foreach (var line in exception.ToString()
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n'))
        {
            _logs.TryEnqueue(Name, SynapsysLogSource.StandardError, line);
        }
    }

    private static void Observe(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ToProtocol(ProcessType type) =>
        type == ProcessType.Endless ? "endless" : "progressive";

    private static string ToProtocol(ProcessState state) => state switch
    {
        ProcessState.Idle => "idle",
        ProcessState.Running => "running",
        ProcessState.Stopping => "stopping",
        ProcessState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported process state."),
    };
}
