using Microsoft.Extensions.Logging;

namespace Basamento.Synapsys.Internal;

internal static partial class SynapsysDiagnostics
{
    [LoggerMessage(1, LogLevel.Information, "[Synapsys] Worker '{WorkerName}' registered {ProcessCount} process(es) | core={CoreUrl} host={Host} heartbeat={HeartbeatInterval}")]
    internal static partial void WorkerStarted(
        ILogger logger,
        string workerName,
        int processCount,
        Uri coreUrl,
        string host,
        TimeSpan heartbeatInterval);

    [LoggerMessage(2, LogLevel.Information, "[Synapsys] Worker '{WorkerName}' stopped")]
    internal static partial void WorkerStopped(ILogger logger, string workerName);

    [LoggerMessage(3, LogLevel.Warning, "[Synapsys] {Warning}")]
    internal static partial void ConfigurationWarning(ILogger logger, string warning);

    [LoggerMessage(4, LogLevel.Warning, "[Synapsys] Core is unreachable at startup; heartbeat retries will continue | core={CoreUrl}")]
    internal static partial void CoreUnreachableAtStartup(ILogger logger, Uri coreUrl, Exception? exception);

    [LoggerMessage(5, LogLevel.Warning, "[Synapsys] {Message}; heartbeat retries will continue")]
    internal static partial void CoreUnavailable(ILogger logger, string message, Exception? exception);

    [LoggerMessage(6, LogLevel.Debug, "[Synapsys] {Message}")]
    internal static partial void RepeatedCoreFailure(ILogger logger, string message, Exception? exception);

    [LoggerMessage(7, LogLevel.Information, "[Synapsys] Core connection restored")]
    internal static partial void CoreRestored(ILogger logger);

    [LoggerMessage(8, LogLevel.Information, "[Synapsys] Starting process '{ProcessName}' (type={ProcessType}, token={Token}, runId={RunId})")]
    internal static partial void ProcessStarting(
        ILogger logger,
        string processName,
        ProcessType processType,
        long token,
        long? runId);

    [LoggerMessage(9, LogLevel.Information, "[Synapsys] Stopping process '{ProcessName}' (type={ProcessType}, token={Token})")]
    internal static partial void ProcessStopping(
        ILogger logger,
        string processName,
        ProcessType processType,
        long token);

    [LoggerMessage(10, LogLevel.Debug, "[Synapsys] Command remains pending until the current transition completes | process={ProcessName} token={Token}")]
    internal static partial void CommandDeferred(ILogger logger, string processName, long token);

    [LoggerMessage(11, LogLevel.Error, "[Synapsys] Process '{ProcessName}' failed")]
    internal static partial void ProcessFailed(ILogger logger, string processName, Exception exception);

    [LoggerMessage(12, LogLevel.Error, "[Synapsys] Cleanup hook for process '{ProcessName}' failed")]
    internal static partial void CleanupFailed(ILogger logger, string processName, Exception exception);

    [LoggerMessage(13, LogLevel.Warning, "[Synapsys] Core sent a command for unknown process '{ProcessName}'; ignoring")]
    internal static partial void UnknownProcess(ILogger logger, string processName);

    [LoggerMessage(14, LogLevel.Debug, "[Synapsys] Ignoring unsupported desired state '{DesiredState}' | process={ProcessName}")]
    internal static partial void UnsupportedDesiredState(ILogger logger, string processName, string desiredState);

    [LoggerMessage(15, LogLevel.Error, "[Synapsys] Command handling failed | process={ProcessName}")]
    internal static partial void CommandFailed(ILogger logger, string processName, Exception exception);

    [LoggerMessage(16, LogLevel.Error, "[Synapsys] Unexpected heartbeat failure; heartbeat retries will continue")]
    internal static partial void UnexpectedHeartbeatFailure(ILogger logger, Exception exception);

    [LoggerMessage(17, LogLevel.Warning, "[Synapsys] Process '{ProcessName}' did not stop before the shutdown timeout")]
    internal static partial void ProcessShutdownTimedOut(ILogger logger, string processName);

    [LoggerMessage(18, LogLevel.Warning, "[Synapsys] Heartbeat loop did not stop before the shutdown timeout")]
    internal static partial void HeartbeatShutdownTimedOut(ILogger logger);

    [LoggerMessage(19, LogLevel.Debug, "[Synapsys] Shutdown budget exhausted; skipping the final heartbeat")]
    internal static partial void FinalHeartbeatSkipped(ILogger logger);

    [LoggerMessage(20, LogLevel.Debug, "[Synapsys] Final heartbeat failed")]
    internal static partial void FinalHeartbeatFailed(ILogger logger, Exception? exception);

    [LoggerMessage(21, LogLevel.Warning, "[Synapsys] Starting process without a run ID; execution logs cannot be attached | process={ProcessName}")]
    internal static partial void MissingRunId(ILogger logger, string processName);
}

