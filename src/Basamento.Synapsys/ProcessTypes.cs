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
/// <param name="Name">The unique process name.</param>
/// <param name="Type">The process execution model.</param>
/// <param name="State">The current execution state.</param>
/// <param name="AcknowledgedToken">The newest handled Core command token.</param>
/// <param name="RunId">The active or most recently assigned opaque Core run identifier.</param>
public sealed record ProcessStatus(
    string Name,
    ProcessType Type,
    ProcessState State,
    long AcknowledgedToken,
    long? RunId);

