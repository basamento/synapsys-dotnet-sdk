using Basamento.Synapsys.Extensions.Hosting.Internal;
using Basamento.Synapsys.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Basamento.Synapsys.Extensions.Hosting;

/// <summary>Registers Synapsys with the .NET Generic Host.</summary>
public static class SynapsysServiceCollectionExtensions
{
    private const string HttpClientName = "Basamento.Synapsys";

    /// <summary>Adds Synapsys using a configuration section such as <c>Synapsys</c>.</summary>
    public static ISynapsysBuilder AddSynapsys(
        this IServiceCollection services,
        IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        AddInfrastructure(services);
        services.AddOptions<SynapsysOptions>()
            .Configure(options => SynapsysConfiguration.Apply(section, options));
        return new SynapsysBuilder(services);
    }

    /// <summary>Adds Synapsys using a strongly typed options callback.</summary>
    public static ISynapsysBuilder AddSynapsys(
        this IServiceCollection services,
        Action<SynapsysOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        AddInfrastructure(services);
        services.AddOptions<SynapsysOptions>().Configure(configure);
        return new SynapsysBuilder(services);
    }

    private static void AddInfrastructure(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions();
        services.TryAddSingleton<ProcessLogHub>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, SynapsysProcessLoggerProvider>());
        services.AddHttpClient(HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<SynapsysOptions>>().Value;
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = options.ConnectTimeout,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 8,
                };
            });
        services.TryAddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SynapsysOptions>>().Value;
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logs = serviceProvider.GetRequiredService<ProcessLogHub>();
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(HttpClientName);
            var worker = new SynapsysWorker(
                options,
                loggerFactory,
                logs,
                TimeProvider.System,
                transportFactory: null,
                httpClient);
            foreach (var registration in serviceProvider.GetServices<ProcessRegistrationDescriptor>())
            {
                if (registration.Type == ProcessType.Endless)
                {
                    worker.RegisterEndless(
                        registration.Name,
                        cancellationToken => registration.ExecuteAsync(serviceProvider, cancellationToken));
                }
                else
                {
                    worker.RegisterProgressive(
                        registration.Name,
                        cancellationToken => registration.ExecuteAsync(serviceProvider, cancellationToken));
                }
            }

            return worker;
        });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SynapsysHostedService>());
    }
}

