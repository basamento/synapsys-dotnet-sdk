using System.Diagnostics;
using System.Net;
using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basamento.Synapsys;

/// <summary>
/// Owns an explicit set of Synapsys processes, outbound heartbeats, cooperative
/// cancellation, process-state reporting, and bounded shutdown.
/// </summary>
public sealed class SynapsysWorker : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SynapsysOptions _options;
    private readonly ILogger _logger;
    private readonly ProcessLogHub _logs;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ISynapsysTransport> _transportFactory;
    private readonly List<ManagedProcess> _processes = new();
    private readonly Dictionary<string, ManagedProcess> _processesByName = new(StringComparer.Ordinal);
    private Task? _startTask;
    private Task? _stopTask;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private ISynapsysTransport? _transport;
    private bool _started;
    private bool _stopped;
    private bool _disposed;
    private bool _coreAvailable = true;

    /// <summary>Initializes a framework-neutral worker.</summary>
    /// <param name="options">Worker configuration. Explicit values override environment-derived defaults.</param>
    /// <param name="loggerFactory">The application's logger factory for SDK diagnostics.</param>
    /// <param name="httpClient">An optional caller-owned HTTP client. The worker never disposes it.</param>
    public SynapsysWorker(
        SynapsysOptions options,
        ILoggerFactory? loggerFactory = null,
        HttpClient? httpClient = null)
        : this(
            options,
            loggerFactory,
            new ProcessLogHub(),
            TimeProvider.System,
            transportFactory: null,
            httpClient)
    {
    }

    internal SynapsysWorker(
        SynapsysOptions options,
        ILoggerFactory? loggerFactory,
        ProcessLogHub logs,
        TimeProvider timeProvider,
        Func<ISynapsysTransport>? transportFactory,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Copy();
        _logs = logs;
        _timeProvider = timeProvider;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger("Basamento.Synapsys.Worker");
        _logs.Configure(_options.Enabled && _options.CaptureConsole, timeProvider);
        _transportFactory = transportFactory ??
            (() => new SynapsysHttpTransport(_options, httpClient));
    }

    /// <summary>Returns thread-safe snapshots of every registered process.</summary>
    public IReadOnlyList<ProcessStatus> Processes
    {
        get
        {
            lock (_gate)
            {
                return _processes.Select(static process => process.Status).ToArray();
            }
        }
    }

    /// <summary>Registers synchronous long-running business logic.</summary>
    public void RegisterEndless(string name, Action run) =>
        RegisterEndless(name, run, new SynapsysProcessOptions());

    /// <summary>Registers synchronous long-running business logic with per-process options.</summary>
    public void RegisterEndless(
        string name,
        Action run,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Endless, Normalize(run, longRunning: true), options);
    }

    /// <summary>Registers synchronous long-running business logic with cooperative cancellation.</summary>
    public void RegisterEndless(string name, Action<CancellationToken> run) =>
        RegisterEndless(name, run, new SynapsysProcessOptions());

    /// <summary>Registers synchronous cancellable long-running logic with per-process options.</summary>
    public void RegisterEndless(
        string name,
        Action<CancellationToken> run,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Endless, Normalize(run, longRunning: true), options);
    }

    /// <summary>Registers asynchronous long-running business logic that has its own stop mechanism.</summary>
    public void RegisterEndless(string name, Func<Task> runAsync) =>
        RegisterEndless(name, runAsync, new SynapsysProcessOptions());

    /// <summary>Registers asynchronous long-running business logic with per-process options.</summary>
    public void RegisterEndless(
        string name,
        Func<Task> runAsync,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Endless, _ => runAsync(), options);
    }

    /// <summary>Registers asynchronous long-running business logic with cooperative cancellation.</summary>
    public void RegisterEndless(string name, Func<CancellationToken, Task> runAsync) =>
        RegisterEndless(name, runAsync, new SynapsysProcessOptions());

    /// <summary>Registers asynchronous cancellable long-running logic with per-process options.</summary>
    public void RegisterEndless(
        string name,
        Func<CancellationToken, Task> runAsync,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Endless, runAsync, options);
    }

    /// <summary>Registers synchronous finite business logic.</summary>
    public void RegisterProgressive(string name, Action run) =>
        RegisterProgressive(name, run, new SynapsysProcessOptions());

    /// <summary>Registers synchronous finite business logic with per-process options.</summary>
    public void RegisterProgressive(
        string name,
        Action run,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Progressive, Normalize(run, longRunning: false), options);
    }

    /// <summary>Registers synchronous finite business logic with cooperative cancellation.</summary>
    public void RegisterProgressive(string name, Action<CancellationToken> run) =>
        RegisterProgressive(name, run, new SynapsysProcessOptions());

    /// <summary>Registers synchronous cancellable finite logic with per-process options.</summary>
    public void RegisterProgressive(
        string name,
        Action<CancellationToken> run,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Progressive, Normalize(run, longRunning: false), options);
    }

    /// <summary>Registers asynchronous finite business logic that runs once per accepted start.</summary>
    public void RegisterProgressive(string name, Func<Task> runAsync) =>
        RegisterProgressive(name, runAsync, new SynapsysProcessOptions());

    /// <summary>Registers asynchronous finite business logic with per-process options.</summary>
    public void RegisterProgressive(
        string name,
        Func<Task> runAsync,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Progressive, _ => runAsync(), options);
    }

    /// <summary>Registers asynchronous finite business logic with cooperative cancellation.</summary>
    public void RegisterProgressive(string name, Func<CancellationToken, Task> runAsync) =>
        RegisterProgressive(name, runAsync, new SynapsysProcessOptions());

    /// <summary>Registers asynchronous cancellable finite logic with per-process options.</summary>
    public void RegisterProgressive(
        string name,
        Func<CancellationToken, Task> runAsync,
        SynapsysProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        ArgumentNullException.ThrowIfNull(options);
        Register(name, ProcessType.Progressive, runAsync, options);
    }

    /// <summary>
    /// Creates a process-scoped logger that tees records to an optional application logger and
    /// queues records produced during that process's active run for Core.
    /// </summary>
    public ILogger CreateLogger(string processName, ILogger? passthrough = null)
    {
        ValidateProcessName(processName);
        return new ProcessCaptureLogger(_logs, processName.Trim(), passthrough);
    }

    /// <summary>
    /// Creates a process-scoped text writer. The caller owns the optional passthrough writer.
    /// This method never replaces the process-global console streams.
    /// </summary>
    public TextWriter CreateTextWriter(
        string processName,
        SynapsysLogSource source = SynapsysLogSource.StandardOutput,
        TextWriter? passthrough = null)
    {
        ValidateProcessName(processName);
        return _logs.CreateWriter(processName, source, passthrough);
    }

    /// <summary>
    /// Validates configuration, performs one bounded health probe, and starts the non-overlapping
    /// fixed-delay heartbeat loop. Business processes remain idle until commanded by Core.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped)
            {
                throw new InvalidOperationException("A stopped SynapsysWorker cannot be restarted.");
            }

            _startTask ??= StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    /// <summary>
    /// Stops heartbeats, requests cooperative process cancellation, waits within the configured
    /// hard shutdown budget, and sends at most one best-effort final heartbeat. This method is
    /// idempotent and does not throw for shutdown, process, or transport failures.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync(cancellationToken);
            return _stopTask;
        }
    }

    /// <summary>Stops and releases the worker without disposing caller-owned dependencies.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    internal Task<bool> HeartbeatOnceAsync(
        bool applyCommands = true,
        CancellationToken cancellationToken = default) =>
        SafeTickAsync(applyCommands, cancellationToken);

    private void Register(
        string name,
        ProcessType type,
        Func<CancellationToken, Task> runAsync,
        SynapsysProcessOptions? options)
    {
        var normalizedName = ValidateProcessName(name);
        var cleanup = NormalizeCleanup(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null)
            {
                throw new InvalidOperationException(
                    "Processes must be registered before StartAsync is called.");
            }

            if (_processesByName.ContainsKey(normalizedName))
            {
                throw new SynapsysConfigurationException(
                    $"Duplicate process name '{normalizedName}'. Process names must be unique within a worker.");
            }

            var process = new ManagedProcess(
                new ProcessDeclaration(normalizedName, type, runAsync, cleanup),
                _logs,
                _logger);
            _logs.Register(process);
            _processes.Add(process);
            _processesByName.Add(normalizedName, process);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        foreach (var warning in _options.ValidateForStartup())
        {
            SynapsysDiagnostics.ConfigurationWarning(_logger, warning);
        }

        var transport = _transportFactory();
        HttpStatusCode healthStatus = 0;
        Exception? healthError = null;
        try
        {
            healthStatus = await transport.HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            healthError = exception;
        }

        var reachable = healthError is null && (int)healthStatus is >= 200 and < 300;
        if (!reachable && _options.FailFast)
        {
            transport.Dispose();
            throw new SynapsysStartupException(
                healthError is null
                    ? $"Core health check returned status {(int)healthStatus}."
                    : "Core health check failed.",
                healthError);
        }

        if (!reachable)
        {
            lock (_gate)
            {
                _coreAvailable = false;
            }

            SynapsysDiagnostics.CoreUnreachableAtStartup(
                _logger,
                _options.CoreUrl!,
                healthError);
        }

        var heartbeatCancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _transport = transport;
            _heartbeatCancellation = heartbeatCancellation;
            _started = true;
            _heartbeatTask = HeartbeatLoopAsync(heartbeatCancellation.Token);
        }

        SynapsysDiagnostics.WorkerStarted(
            _logger,
            _options.WorkerName!,
            _processes.Count,
            _options.CoreUrl!,
            string.IsNullOrWhiteSpace(_options.Host) ? "<unset>" : _options.Host,
            _options.HeartbeatInterval);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SafeTickAsync(applyCommands: true, cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(
                    _options.HeartbeatInterval,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> SafeTickAsync(
        bool applyCommands,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TickAsync(applyCommands, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            SynapsysDiagnostics.UnexpectedHeartbeatFailure(_logger, exception);
            return false;
        }
    }

    private async Task<bool> TickAsync(
        bool applyCommands,
        CancellationToken cancellationToken)
    {
        ISynapsysTransport? transport;
        IReadOnlyList<ManagedProcess> processes;
        lock (_gate)
        {
            transport = _transport;
            processes = _processes.ToArray();
        }

        if (transport is null)
        {
            return false;
        }

        var drainedLogs = _logs.Drain();
        var request = new HeartbeatRequest
        {
            WorkerName = _options.WorkerName!,
            Host = string.IsNullOrWhiteSpace(_options.Host) ? null : _options.Host,
            HeartbeatIntervalSeconds = checked((long)_options.HeartbeatInterval.TotalSeconds),
            Processes = processes.Select(static process => process.Snapshot()).ToArray(),
            Logs = drainedLogs.Count == 0 ? null : drainedLogs,
        };

        HeartbeatTransportResult result;
        using (_logs.Suppress())
        {
            result = await transport.HeartbeatAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (result.Error is not null || !result.IsHttpSuccess)
        {
            _logs.RequeueFront(drainedLogs);
            if ((int)result.StatusCode is >= 200 and < 300)
            {
                MarkCoreUnavailable("Core returned a malformed heartbeat response", result.Error);
            }
            else if (result.Error is not null)
            {
                MarkCoreUnavailable("Failed to contact Core", result.Error);
            }
            else
            {
                MarkCoreUnavailable(
                    $"Heartbeat rejected by Core with status {(int)result.StatusCode}",
                    null);
            }

            return false;
        }

        if (result.IsMalformedSuccess)
        {
            _logs.RequeueFront(drainedLogs);
            MarkCoreUnavailable("Core returned a malformed heartbeat response", null);
            return false;
        }

        MarkCoreAvailable();
        if (applyCommands)
        {
            ApplyCommands(result.Response!.Processes!);
        }

        return true;
    }

    private void ApplyCommands(IEnumerable<ProcessUpdate> updates)
    {
        foreach (var update in updates)
        {
            ManagedProcess? process;
            lock (_gate)
            {
                _processesByName.TryGetValue(update.Name, out process);
            }

            if (process is null)
            {
                SynapsysDiagnostics.UnknownProcess(_logger, update.Name);
                continue;
            }

            try
            {
                ApplyCommand(process, update);
            }
            catch (Exception exception)
            {
                process.MarkCommandFailure(exception);
                SynapsysDiagnostics.CommandFailed(_logger, process.Name, exception);
            }
        }
    }

    private void ApplyCommand(ManagedProcess process, ProcessUpdate update)
    {
        if (update.DesiredToken is null)
        {
            process.SynchronizeRunId(update.RunId);
            return;
        }

        var token = update.DesiredToken.Value;
        if (token <= process.AcknowledgedToken)
        {
            process.SynchronizeRunId(update.RunId);
            return;
        }

        CommandOutcome outcome;
        switch (update.DesiredState)
        {
            case "idle":
                process.SynchronizeRunId(update.RunId);
                outcome = CommandOutcome.Handled;
                break;

            case "running":
                SynapsysDiagnostics.ProcessStarting(
                    _logger,
                    process.Name,
                    process.Type,
                    token,
                    update.RunId);
                if (update.RunId is null)
                {
                    SynapsysDiagnostics.MissingRunId(_logger, process.Name);
                }

                outcome = process.Start(update.RunId);
                break;

            case "stopping":
                process.SynchronizeRunId(update.RunId);
                SynapsysDiagnostics.ProcessStopping(
                    _logger,
                    process.Name,
                    process.Type,
                    token);
                outcome = process.Stop();
                break;

            default:
                process.SynchronizeRunId(update.RunId);
                SynapsysDiagnostics.UnsupportedDesiredState(
                    _logger,
                    process.Name,
                    update.DesiredState);
                outcome = CommandOutcome.Handled;
                break;
        }

        if (outcome == CommandOutcome.Handled)
        {
            process.Acknowledge(token);
        }
        else
        {
            SynapsysDiagnostics.CommandDeferred(_logger, process.Name, token);
        }
    }

    private async Task StopCoreAsync(CancellationToken callerCancellation)
    {
        Task? startTask;
        lock (_gate)
        {
            startTask = _startTask;
        }

        if (startTask is not null)
        {
            try
            {
                await startTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SynapsysDiagnostics.RepeatedCoreFailure(
                    _logger,
                    "Worker startup did not complete; shutdown has nothing to stop",
                    exception);
                MarkStopped();
                return;
            }
        }

        lock (_gate)
        {
            if (!_started)
            {
                _stopped = true;
                return;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        if (_options.ShutdownTimeout == TimeSpan.Zero)
        {
            shutdown.Cancel();
        }
        else
        {
            shutdown.CancelAfter(_options.ShutdownTimeout);
        }

        CancellationTokenSource? heartbeatCancellation;
        Task? heartbeatTask;
        IReadOnlyList<ManagedProcess> processes;
        ISynapsysTransport? transport;
        lock (_gate)
        {
            heartbeatCancellation = _heartbeatCancellation;
            heartbeatTask = _heartbeatTask;
            processes = _processes.ToArray();
            transport = _transport;
        }

        heartbeatCancellation?.Cancel();
        if (heartbeatTask is not null &&
            !await WaitWithoutThrowingAsync(heartbeatTask, shutdown.Token).ConfigureAwait(false))
        {
            SynapsysDiagnostics.HeartbeatShutdownTimedOut(_logger);
        }

        foreach (var process in processes)
        {
            process.Stop();
        }

        var completions = Task.WhenAll(processes.Select(static process => process.Completion));
        await WaitWithoutThrowingAsync(completions, shutdown.Token).ConfigureAwait(false);
        foreach (var process in processes.Where(static process => process.IsInFlight))
        {
            SynapsysDiagnostics.ProcessShutdownTimedOut(_logger, process.Name);
        }

        var remaining = _options.ShutdownTimeout - stopwatch.Elapsed;
        if (!callerCancellation.IsCancellationRequested &&
            remaining > TimeSpan.Zero &&
            transport is not null)
        {
            var finalBudget = remaining < _options.RequestTimeout
                ? remaining
                : _options.RequestTimeout;
            using var finalCancellation = new CancellationTokenSource(finalBudget);
            var finalSucceeded = await SafeTickAsync(
                applyCommands: false,
                finalCancellation.Token).ConfigureAwait(false);
            if (!finalSucceeded && !finalCancellation.IsCancellationRequested)
            {
                SynapsysDiagnostics.FinalHeartbeatFailed(_logger, null);
            }
        }
        else
        {
            SynapsysDiagnostics.FinalHeartbeatSkipped(_logger);
        }

        transport?.Dispose();
        heartbeatCancellation?.Dispose();
        MarkStopped();
        SynapsysDiagnostics.WorkerStopped(
            _logger,
            _options.WorkerName ?? "<disabled>");
    }

    private void MarkStopped()
    {
        lock (_gate)
        {
            _started = false;
            _stopped = true;
            _transport = null;
            _heartbeatCancellation = null;
            _heartbeatTask = null;
        }
    }

    private void MarkCoreUnavailable(string message, Exception? exception)
    {
        bool wasAvailable;
        lock (_gate)
        {
            wasAvailable = _coreAvailable;
            _coreAvailable = false;
        }

        if (wasAvailable)
        {
            SynapsysDiagnostics.CoreUnavailable(_logger, message, exception);
        }
        else
        {
            SynapsysDiagnostics.RepeatedCoreFailure(_logger, message, exception);
        }
    }

    private void MarkCoreAvailable()
    {
        bool wasAvailable;
        lock (_gate)
        {
            wasAvailable = _coreAvailable;
            _coreAvailable = true;
        }

        if (!wasAvailable)
        {
            SynapsysDiagnostics.CoreRestored(_logger);
        }
    }

    private static async Task<bool> WaitWithoutThrowingAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || task.IsFaulted)
        {
            _ = task.Exception;
            return false;
        }
    }

    private static Func<CancellationToken, Task> Normalize(Action action, bool longRunning) =>
        cancellationToken => longRunning
            ? Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            : Task.Run(action, CancellationToken.None);

    private static Func<CancellationToken, Task> Normalize(
        Action<CancellationToken> action,
        bool longRunning) =>
        cancellationToken => longRunning
            ? Task.Factory.StartNew(
                () => action(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            : Task.Run(() => action(cancellationToken), CancellationToken.None);

    private static Func<Task>? NormalizeCleanup(SynapsysProcessOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (options.OnStop is not null && options.OnStopAsync is not null)
        {
            throw new SynapsysConfigurationException(
                "A process may configure either OnStop or OnStopAsync, not both.");
        }

        if (options.OnStop is not null)
        {
            return () => Task.Run(options.OnStop);
        }

        return options.OnStopAsync is null
            ? null
            : () => options.OnStopAsync(CancellationToken.None);
    }

    private static string ValidateProcessName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            throw new SynapsysConfigurationException("Process name must not be blank.");
        }

        return normalized;
    }
}
