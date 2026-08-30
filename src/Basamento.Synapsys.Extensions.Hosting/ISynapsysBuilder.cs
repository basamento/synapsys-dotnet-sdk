using Microsoft.Extensions.DependencyInjection;

namespace Basamento.Synapsys.Extensions.Hosting;

/// <summary>Builds Generic Host registrations for Synapsys processes.</summary>
public interface ISynapsysBuilder
{
    /// <summary>Gets the application service collection.</summary>
    IServiceCollection Services { get; }
}

