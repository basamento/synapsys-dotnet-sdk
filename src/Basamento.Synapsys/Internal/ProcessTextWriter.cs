using System.Text;

namespace Basamento.Synapsys.Internal;

internal sealed class ProcessTextWriter : TextWriter
{
    private readonly ProcessLogHub _hub;
    private readonly string _processName;
    private readonly SynapsysLogSource _source;
    private readonly TextWriter _passthrough;
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();

    internal ProcessTextWriter(
        ProcessLogHub hub,
        string processName,
        SynapsysLogSource source,
        TextWriter passthrough)
    {
        _hub = hub;
        _processName = processName;
        _source = source;
        _passthrough = passthrough;
    }

    public override Encoding Encoding => _passthrough.Encoding;

    public override void Write(char value)
    {
        _passthrough.Write(value);
        Capture(value.ToString());
    }

    public override void Write(string? value)
    {
        _passthrough.Write(value);
        if (value is not null)
        {
            Capture(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _passthrough.Write(buffer, index, count);
        Capture(new string(buffer, index, count));
    }

    public override void Flush() => _passthrough.Flush();

    internal void FlushCapturedLine()
    {
        lock (_gate)
        {
            FlushCapturedLineLocked();
        }
    }

    private void Capture(string value)
    {
        lock (_gate)
        {
            foreach (var character in value)
            {
                if (character == '\n')
                {
                    FlushCapturedLineLocked();
                    continue;
                }

                _partial.Append(character);
                if (_partial.Length >= ProcessLogHub.MaximumLineRunes * 2)
                {
                    FlushCapturedLineLocked();
                }
            }
        }
    }

    private void FlushCapturedLineLocked()
    {
        if (_partial.Length == 0)
        {
            return;
        }

        _hub.TryEnqueue(_processName, _source, _partial.ToString());
        _partial.Clear();
    }
}

