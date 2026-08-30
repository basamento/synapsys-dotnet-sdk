using Microsoft.Extensions.Logging;

namespace Basamento.Synapsys.Internal;

internal sealed class ProcessCaptureLogger(
    ProcessLogHub hub,
    string processName,
    ILogger? passthrough) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => passthrough?.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) =>
        (passthrough?.IsEnabled(logLevel) ?? false) ||
        (hub.Enabled && logLevel >= LogLevel.Information && logLevel != LogLevel.None);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        passthrough?.Log(logLevel, eventId, state, exception, formatter);
        if (!hub.Enabled || logLevel < LogLevel.Information || logLevel == LogLevel.None)
        {
            return;
        }

        var message = ProcessLogHub.FormatLogState(state, exception, formatter);
        var source = logLevel >= LogLevel.Warning
            ? SynapsysLogSource.StandardError
            : SynapsysLogSource.StandardOutput;
        hub.TryEnqueue(processName, source, message);
        if (exception is not null)
        {
            hub.TryEnqueue(processName, SynapsysLogSource.StandardError, exception.ToString());
        }
    }
}

