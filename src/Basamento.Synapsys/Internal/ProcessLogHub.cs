using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Basamento.Synapsys.Internal;

internal sealed class ProcessLogHub
{
    internal const int MaximumEntries = 10_000;
    internal const int MaximumLineRunes = 16_000;
    internal const int MaximumBatchEntries = 100;
    internal const int MaximumBatchMessageBytes = 96_000;

    private readonly AsyncLocal<ManagedProcess?> _current = new();
    private readonly AsyncLocal<int> _suppression = new();
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<WeakReference<ProcessTextWriter>>> _writers = new(StringComparer.Ordinal);
    private readonly ProcessLogQueue _queue = new();
    private TimeProvider _timeProvider = TimeProvider.System;
    private volatile bool _enabled;

    internal long DroppedCount => _queue.DroppedCount;

    internal int Count => _queue.Count;

    internal bool Enabled => _enabled;

    internal void Configure(bool enabled, TimeProvider timeProvider)
    {
        _enabled = enabled;
        _timeProvider = timeProvider;
    }

    internal void Register(ManagedProcess process)
    {
        if (!_processes.TryAdd(process.Name, process))
        {
            throw new SynapsysConfigurationException(
                $"Duplicate process name '{process.Name}'. Process names must be unique within a worker.");
        }
    }

    internal IDisposable Enter(ManagedProcess process)
    {
        var previous = _current.Value;
        _current.Value = process;
        return new CallbackScope(() => _current.Value = previous);
    }

    internal IDisposable Suppress()
    {
        _suppression.Value++;
        return new CallbackScope(() => _suppression.Value--);
    }

    internal bool TryEnqueueCurrent(LogLevel level, string message, Exception? exception = null)
    {
        if (!_enabled || _suppression.Value > 0 || level < LogLevel.Information || level == LogLevel.None)
        {
            return false;
        }

        var process = _current.Value;
        if (process is null)
        {
            return false;
        }

        var source = level >= LogLevel.Warning
            ? SynapsysLogSource.StandardError
            : SynapsysLogSource.StandardOutput;
        Enqueue(process, source, message);
        if (exception is not null)
        {
            foreach (var line in NormalizeLines(exception.ToString()))
            {
                Enqueue(process, SynapsysLogSource.StandardError, line);
            }
        }

        return true;
    }

    internal bool TryEnqueue(string processName, SynapsysLogSource source, string message)
    {
        if (!_enabled || _suppression.Value > 0 ||
            !_processes.TryGetValue(processName.Trim(), out var process))
        {
            return false;
        }

        Enqueue(process, source, message);
        return true;
    }

    internal IReadOnlyList<ProcessLogEntry> Drain() =>
        _queue.Drain(MaximumBatchEntries, MaximumBatchMessageBytes);

    internal void RequeueFront(IReadOnlyList<ProcessLogEntry> entries) =>
        _queue.RequeueFront(entries);

    internal TextWriter CreateWriter(
        string processName,
        SynapsysLogSource source,
        TextWriter? passthrough)
    {
        var normalized = processName.Trim();
        var writer = new ProcessTextWriter(this, normalized, source, passthrough ?? TextWriter.Null);
        var collection = _writers.GetOrAdd(normalized, static _ => new List<WeakReference<ProcessTextWriter>>());
        lock (collection)
        {
            collection.Add(new WeakReference<ProcessTextWriter>(writer));
        }

        return writer;
    }

    internal void FlushWriters(string processName)
    {
        if (!_writers.TryGetValue(processName, out var collection))
        {
            return;
        }

        lock (collection)
        {
            collection.RemoveAll(reference =>
            {
                if (!reference.TryGetTarget(out var writer))
                {
                    return true;
                }

                writer.FlushCapturedLine();
                return false;
            });
        }
    }

    internal static string FormatLogState<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return message;
        }

        var output = new StringBuilder(message);
        foreach (var property in properties)
        {
            if (property.Key == "{OriginalFormat}")
            {
                continue;
            }

            output.Append(' ');
            output.Append(property.Key);
            output.Append('=');
            AppendValue(output, property.Value);
        }

        return output.ToString();
    }

    private static void AppendValue(StringBuilder output, object? value)
    {
        if (value is null)
        {
            output.Append("null");
        }
        else if (value is string text)
        {
            output.Append('"');
            output.Append(text.Replace("\"", "\\\"", StringComparison.Ordinal));
            output.Append('"');
        }
        else if (value is IFormattable formattable)
        {
            output.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
        }
        else
        {
            output.Append(value);
        }
    }

    private void Enqueue(ManagedProcess process, SynapsysLogSource source, string raw)
    {
        foreach (var line in NormalizeLines(raw))
        {
            if (string.IsNullOrWhiteSpace(line) || !process.TryNextLogContext(out var runId, out var sequence))
            {
                continue;
            }

            _queue.Enqueue(new ProcessLogEntry
            {
                ProcessName = process.Name,
                RunId = runId,
                Sequence = sequence,
                Timestamp = _timeProvider.GetUtcNow(),
                Source = source == SynapsysLogSource.StandardError ? "stderr" : "stdout",
                Level = source == SynapsysLogSource.StandardError ? "error" : "info",
                Message = TruncateRunes(line.TrimEnd('\r'), MaximumLineRunes),
            });
        }
    }

    private static string[] NormalizeLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string TruncateRunes(string value, int maximum)
    {
        var count = 0;
        var output = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maximum)
            {
                output.Append('…');
                return output.ToString();
            }

            output.Append(rune.ToString());
            count++;
        }

        return output.ToString();
    }

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}

internal sealed class ProcessLogQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<ProcessLogEntry> _entries = new();
    private long _droppedCount;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal void Enqueue(ProcessLogEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Count >= ProcessLogHub.MaximumEntries)
            {
                _entries.RemoveFirst();
                Interlocked.Increment(ref _droppedCount);
            }

            _entries.AddLast(entry);
        }
    }

    internal IReadOnlyList<ProcessLogEntry> Drain(int maximumEntries, int maximumMessageBytes)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return Array.Empty<ProcessLogEntry>();
            }

            var result = new List<ProcessLogEntry>(Math.Min(maximumEntries, _entries.Count));
            var bytes = 0;
            while (result.Count < maximumEntries && _entries.First is { } first)
            {
                var nextBytes = Encoding.UTF8.GetByteCount(first.Value.Message);
                if (result.Count > 0 && bytes + nextBytes > maximumMessageBytes)
                {
                    break;
                }

                bytes += nextBytes;
                result.Add(first.Value);
                _entries.RemoveFirst();
            }

            return result;
        }
    }

    internal void RequeueFront(IReadOnlyList<ProcessLogEntry> entries)
    {
        lock (_gate)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                _entries.AddFirst(entries[index]);
            }

            while (_entries.Count > ProcessLogHub.MaximumEntries)
            {
                _entries.RemoveLast();
                Interlocked.Increment(ref _droppedCount);
            }
        }
    }
}
