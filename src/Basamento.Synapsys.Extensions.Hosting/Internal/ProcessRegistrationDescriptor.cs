using Microsoft.Extensions.DependencyInjection;

namespace Basamento.Synapsys.Extensions.Hosting.Internal;

internal sealed class ProcessRegistrationDescriptor
{
    private readonly Func<IServiceProvider, CancellationToken, Task> _executeAsync;

    private ProcessRegistrationDescriptor(
        string name,
        ProcessType type,
        Func<IServiceProvider, CancellationToken, Task> executeAsync)
    {
        Name = name;
        Type = type;
        _executeAsync = executeAsync;
    }

    internal string Name { get; }

    internal ProcessType Type { get; }

    internal Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        _executeAsync(services, cancellationToken);

    internal static ProcessRegistrationDescriptor Async<TService>(
        string name,
        ProcessType type,
        Func<TService, CancellationToken, Task> executeAsync)
        where TService : notnull =>
        new(name, type, (services, cancellationToken) =>
            ExecuteInScopeAsync(services, executeAsync, cancellationToken));

    internal static ProcessRegistrationDescriptor Synchronous<TService>(
        string name,
        ProcessType type,
        Action<TService, CancellationToken> execute)
        where TService : notnull =>
        new(name, type, (services, cancellationToken) =>
            ScheduleScopedSynchronous(services, execute, type, cancellationToken));

    private static async Task ExecuteInScopeAsync<TService>(
        IServiceProvider services,
        Func<TService, CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        await executeAsync(service, cancellationToken).ConfigureAwait(false);
    }

    private static Task ScheduleScopedSynchronous<TService>(
        IServiceProvider services,
        Action<TService, CancellationToken> execute,
        ProcessType type,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        var options = type == ProcessType.Endless
            ? TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach
            : TaskCreationOptions.DenyChildAttach;
        return Task.Factory.StartNew(
            () => ExecuteSynchronousInScopeAsync(
                services,
                execute,
                cancellationToken),
            CancellationToken.None,
            options,
            TaskScheduler.Default).Unwrap();
    }

    private static async Task ExecuteSynchronousInScopeAsync<TService>(
        IServiceProvider services,
        Action<TService, CancellationToken> execute,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        execute(service, cancellationToken);
    }
}

