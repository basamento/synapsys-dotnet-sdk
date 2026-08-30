using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Logging;

namespace Basamento.Synapsys.Extensions.Hosting.Internal;

internal sealed class SynapsysProcessLoggerProvider(ProcessLogHub logs)
    : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("Basamento.Synapsys", StringComparison.Ordinal)
            ? NullLogger.Instance
            : new ExecutionLogger(logs);

    public void Dispose()
    {
    }

    private sealed class ExecutionLogger(ProcessLogHub logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logs.Enabled && logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            logs.TryEnqueueCurrent(
                logLevel,
                ProcessLogHub.FormatLogState(state, exception, formatter),
                exception);
        }
    }

    private sealed class NullLogger : ILogger
    {
        internal static NullLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

