using Basamento.Synapsys.Extensions.Hosting.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Basamento.Synapsys.Extensions.Hosting;

/// <summary>Adds scoped service-method registrations to a Synapsys host builder.</summary>
public static class SynapsysBuilderExtensions
{
    /// <summary>Registers a synchronous Endless service method.</summary>
    public static ISynapsysBuilder AddEndless<TService>(
        this ISynapsysBuilder builder,
        string name,
        Action<TService> run)
        where TService : notnull =>
        AddSynchronous<TService>(builder, name, ProcessType.Endless, (service, _) => run(service));

    /// <summary>Registers a synchronous cancellable Endless service method.</summary>
    public static ISynapsysBuilder AddEndless<TService>(
        this ISynapsysBuilder builder,
        string name,
        Action<TService, CancellationToken> run)
        where TService : notnull =>
        AddSynchronous(builder, name, ProcessType.Endless, run);

    /// <summary>Registers an asynchronous Endless service method.</summary>
    public static ISynapsysBuilder AddEndless<TService>(
        this ISynapsysBuilder builder,
        string name,
        Func<TService, Task> runAsync)
        where TService : notnull =>
        AddAsynchronous<TService>(builder, name, ProcessType.Endless, (service, _) => runAsync(service));

    /// <summary>Registers an asynchronous cancellable Endless service method.</summary>
    public static ISynapsysBuilder AddEndless<TService>(
        this ISynapsysBuilder builder,
        string name,
        Func<TService, CancellationToken, Task> runAsync)
        where TService : notnull =>
        AddAsynchronous(builder, name, ProcessType.Endless, runAsync);

    /// <summary>Registers a synchronous Progressive service method.</summary>
    public static ISynapsysBuilder AddProgressive<TService>(
        this ISynapsysBuilder builder,
        string name,
        Action<TService> run)
        where TService : notnull =>
        AddSynchronous<TService>(builder, name, ProcessType.Progressive, (service, _) => run(service));

    /// <summary>Registers a synchronous cancellable Progressive service method.</summary>
    public static ISynapsysBuilder AddProgressive<TService>(
        this ISynapsysBuilder builder,
        string name,
        Action<TService, CancellationToken> run)
        where TService : notnull =>
        AddSynchronous(builder, name, ProcessType.Progressive, run);

    /// <summary>Registers an asynchronous Progressive service method.</summary>
    public static ISynapsysBuilder AddProgressive<TService>(
        this ISynapsysBuilder builder,
        string name,
        Func<TService, Task> runAsync)
        where TService : notnull =>
        AddAsynchronous<TService>(builder, name, ProcessType.Progressive, (service, _) => runAsync(service));

    /// <summary>Registers an asynchronous cancellable Progressive service method.</summary>
    public static ISynapsysBuilder AddProgressive<TService>(
        this ISynapsysBuilder builder,
        string name,
        Func<TService, CancellationToken, Task> runAsync)
        where TService : notnull =>
        AddAsynchronous(builder, name, ProcessType.Progressive, runAsync);

    private static ISynapsysBuilder AddSynchronous<TService>(
        ISynapsysBuilder builder,
        string name,
        ProcessType type,
        Action<TService, CancellationToken> run)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(run);
        builder.Services.AddSingleton(
            ProcessRegistrationDescriptor.Synchronous(name, type, run));
        return builder;
    }

    private static ISynapsysBuilder AddAsynchronous<TService>(
        ISynapsysBuilder builder,
        string name,
        ProcessType type,
        Func<TService, CancellationToken, Task> runAsync)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(runAsync);
        builder.Services.AddSingleton(
            ProcessRegistrationDescriptor.Async(name, type, runAsync));
        return builder;
    }
}
