using Microsoft.Extensions.Hosting;

namespace Basamento.Synapsys.Extensions.Hosting.Internal;

internal sealed class SynapsysHostedService(SynapsysWorker worker) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        worker.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        worker.StopAsync(cancellationToken);
}

