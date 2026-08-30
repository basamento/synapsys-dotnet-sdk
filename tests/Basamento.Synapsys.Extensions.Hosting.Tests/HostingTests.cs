using Basamento.Synapsys.Extensions.Hosting.Internal;
using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Basamento.Synapsys.Extensions.Hosting.Tests;

public sealed class HostingTests
{
    [Fact]
    public void RejectsUnknownConfigurationKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Synapsys:core-url"] = "https://core.example.test",
                ["Synapsys:worker-name"] = "worker",
                ["Synapsys:heartbeet-interval"] = "5s",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSynapsys(configuration.GetSection("Synapsys"));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<SynapsysConfigurationException>(
            () => _ = provider.GetRequiredService<IOptions<SynapsysOptions>>().Value);
    }

    [Fact]
    public void AcceptsCanonicalAndDotnetConfigurationNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Synapsys:core-url"] = "https://core.example.test",
                ["Synapsys:WorkerName"] = "worker",
                ["Synapsys:heartbeat-interval"] = "5s",
                ["Synapsys:ShutdownTimeout"] = "250ms",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSynapsys(configuration.GetSection("Synapsys"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SynapsysOptions>>().Value;

        Assert.Equal("worker", options.WorkerName);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.ShutdownTimeout);
    }

    [Fact]
    public async Task GenericRegistrationCreatesAndDisposesExecutionScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ScopedProbe>();
        var synapsys = services.AddSynapsys(options => options.Enabled = false);
        synapsys.AddProgressive<ScopedProbe>(
            "scoped-process",
            static probe => probe.RunAsync());
        await using var provider = services.BuildServiceProvider();
        var descriptor = provider.GetRequiredService<ProcessRegistrationDescriptor>();

        await descriptor.ExecuteAsync(provider, CancellationToken.None);

        Assert.Equal(1, ScopedProbe.Executions);
        Assert.Equal(1, ScopedProbe.Disposals);
    }

    [Fact]
    public async Task HostedWorkerRegistersGenericProcessesWithoutStartingThemWhenDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ScopedProbe>();
        services.AddSynapsys(options => options.Enabled = false)
            .AddEndless<ScopedProbe>("consumer", static probe => probe.RunAsync());
        await using var provider = services.BuildServiceProvider();

        var worker = provider.GetRequiredService<SynapsysWorker>();
        await worker.StartAsync();

        Assert.Equal("consumer", Assert.Single(worker.Processes).Name);
        Assert.Equal(0, ScopedProbe.Executions);
    }

    [Fact]
    public async Task LoggingProviderCapturesOnlyInsideExecutionScope()
    {
        var logs = new ProcessLogHub();
        logs.Configure(true, TimeProvider.System);
        using var provider = new SynapsysProcessLoggerProvider(logs);
        var logger = provider.CreateLogger("Application.Orders");
        logger.LogInformation("outside");
        var process = new ManagedProcess(
            new ProcessDeclaration(
                "orders",
                ProcessType.Progressive,
                _ =>
                {
                    logger.LogInformation("inside {OrderId}", 42);
                    return Task.CompletedTask;
                },
                null),
            logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        logs.Register(process);

        process.Start(100);
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var entry = Assert.Single(logs.Drain());
        Assert.Contains("inside", entry.Message, StringComparison.Ordinal);
        Assert.Equal(100, entry.RunId);
    }

    private sealed class ScopedProbe : IAsyncDisposable
    {
        private readonly object _identity = new();

        internal static int Executions;
        internal static int Disposals;

        public ScopedProbe()
        {
            Interlocked.Exchange(ref Executions, 0);
            Interlocked.Exchange(ref Disposals, 0);
        }

        internal Task RunAsync()
        {
            _ = _identity;
            Interlocked.Increment(ref Executions);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            return ValueTask.CompletedTask;
        }
    }
}
