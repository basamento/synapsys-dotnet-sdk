using Microsoft.Extensions.DependencyInjection;

namespace Basamento.Synapsys.Extensions.Hosting.Internal;

internal sealed class SynapsysBuilder(IServiceCollection services) : ISynapsysBuilder
{
    public IServiceCollection Services { get; } = services;
}

