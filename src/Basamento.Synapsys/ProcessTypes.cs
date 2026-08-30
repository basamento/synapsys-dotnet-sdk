namespace Basamento.Synapsys;

/// <summary>Identifies the execution model of a registered process.</summary>
public enum ProcessType
{
    /// <summary>Business logic that remains active until cooperatively stopped.</summary>
    Endless,

    /// <summary>Finite business logic that returns naturally.</summary>
    Progressive,
}

/// <summary>Describes the current state reported to Synapsys Core.</summary>
public enum ProcessState
{
    /// <summary>No invocation is active.</summary>
    Idle,

    /// <summary>The business function is executing.</summary>
    Running,

    /// <summary>A cooperative stop was requested and the function has not returned.</summary>
    Stopping,

    /// <summary>The last invocation ended with an unhandled exception.</summary>
    Failed,
}

/// <summary>Identifies the conventional output stream represented by a process log line.</summary>
public enum SynapsysLogSource
{
    /// <summary>Ordinary informational output.</summary>
    StandardOutput,

    /// <summary>Error output.</summary>
    StandardError,
}

/// <summary>Provides a thread-safe snapshot of one registered process.</summary>
public sealed class ProcessStatus
{
    /// <summary>Initializes a process-status snapshot.</summary>
    /// <param name="name">The unique process name.</param>
    /// <param name="type">The process execution model.</param>
    /// <param name="state">The current execution state.</param>
    /// <param name="acknowledgedToken">The newest handled Core command token.</param>
    /// <param name="runId">The active or most recently assigned opaque Core run identifier.</param>
    public ProcessStatus(
        string name,
        ProcessType type,
        ProcessState state,
        long acknowledgedToken,
        long? runId)
    {
        Name = name;
        Type = type;
        State = state;
        AcknowledgedToken = acknowledgedToken;
        RunId = runId;
    }

    /// <summary>Gets the unique process name.</summary>
    public string Name { get; }

    /// <summary>Gets the process execution model.</summary>
    public ProcessType Type { get; }

    /// <summary>Gets the current execution state.</summary>
    public ProcessState State { get; }

    /// <summary>Gets the newest handled Core command token.</summary>
    public long AcknowledgedToken { get; }

    /// <summary>Gets the active or most recently assigned opaque Core run identifier.</summary>
    public long? RunId { get; }
}
