namespace Basamento.Synapsys;

/// <summary>Configures one process registration.</summary>
public sealed class SynapsysProcessOptions
{
    /// <summary>
    /// Gets or sets a synchronous cleanup hook. For Endless processes it runs as soon as a
    /// stop is accepted; for Progressive processes it runs after a stopped body returns.
    /// </summary>
    public Action? OnStop { get; set; }

    /// <summary>
    /// Gets or sets an asynchronous cleanup hook. It is mutually exclusive with
    /// <see cref="OnStop"/>.
    /// </summary>
    public Func<CancellationToken, Task>? OnStopAsync { get; set; }
}

