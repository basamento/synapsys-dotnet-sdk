namespace Basamento.Synapsys.Internal;

internal sealed record ProcessDeclaration(
    string Name,
    ProcessType Type,
    Func<CancellationToken, Task> RunAsync,
    Func<Task>? CleanupAsync);

internal enum CommandOutcome
{
    Handled,
    Deferred,
}

