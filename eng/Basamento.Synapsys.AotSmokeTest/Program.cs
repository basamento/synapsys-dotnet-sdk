using Basamento.Synapsys;

await using var worker = new SynapsysWorker(new SynapsysOptions
{
    Enabled = false,
    WorkerName = "aot-smoke-test",
});

worker.RegisterProgressive("noop", static () => { });
worker.RegisterEndless("cancellable", static cancellationToken =>
    cancellationToken.ThrowIfCancellationRequested());

await worker.StartAsync();
await worker.StopAsync();

Console.WriteLine($"AOT smoke test registered {worker.Processes.Count} processes.");

