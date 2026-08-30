namespace Basamento.Synapsys;

/// <summary>Represents invalid Synapsys worker configuration.</summary>
public sealed class SynapsysConfigurationException : Exception
{
    /// <summary>Initializes a new configuration exception.</summary>
    /// <param name="message">The validation failure.</param>
    public SynapsysConfigurationException(string message)
        : base($"[Synapsys] {message}")
    {
    }

    /// <summary>Initializes a new configuration exception with an inner exception.</summary>
    /// <param name="message">The validation failure.</param>
    /// <param name="innerException">The underlying exception.</param>
    public SynapsysConfigurationException(string message, Exception innerException)
        : base($"[Synapsys] {message}", innerException)
    {
    }
}

