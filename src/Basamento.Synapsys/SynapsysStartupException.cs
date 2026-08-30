namespace Basamento.Synapsys;

/// <summary>Represents a fail-fast startup failure caused by an unreachable Core.</summary>
public sealed class SynapsysStartupException : Exception
{
    /// <summary>Initializes a startup exception.</summary>
    /// <param name="message">The startup failure.</param>
    /// <param name="innerException">The underlying transport error, when available.</param>
    public SynapsysStartupException(string message, Exception? innerException = null)
        : base($"[Synapsys] {message}", innerException)
    {
    }
}

