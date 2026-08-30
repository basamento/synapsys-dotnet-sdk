using System.Collections.Concurrent;
using System.Net;
using Basamento.Synapsys.Internal;

namespace Basamento.Synapsys.Tests;

internal sealed class TestTransport : ISynapsysTransport
{
    private readonly ConcurrentQueue<HeartbeatTransportResult> _responses = new();
    private readonly object _gate = new();
    private readonly List<HeartbeatRequest> _requests = new();

    internal HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;

    internal Exception? HealthException { get; set; }

    internal TaskCompletionSource FirstHeartbeat { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Func<HeartbeatRequest, CancellationToken, Task>? BeforeHeartbeatAsync { get; set; }

    internal int DisposeCount { get; private set; }

    internal IReadOnlyList<HeartbeatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    internal void Enqueue(HeartbeatTransportResult response) => _responses.Enqueue(response);

    public Task<HttpStatusCode> HealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return HealthException is null
            ? Task.FromResult(HealthStatus)
            : Task.FromException<HttpStatusCode>(HealthException);
    }

    public async Task<HeartbeatTransportResult> HeartbeatAsync(
        HeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        FirstHeartbeat.TrySetResult();
        if (BeforeHeartbeatAsync is not null)
        {
            await BeforeHeartbeatAsync(request, cancellationToken);
        }

        return _responses.TryDequeue(out var response)
            ? response
            : ValidResponse();
    }

    public void Dispose() => DisposeCount++;

    internal static HeartbeatTransportResult ValidResponse(params ProcessUpdate[] updates) =>
        new(HttpStatusCode.OK, new HeartbeatResponse { Processes = updates.ToList() }, null);
}

