using System.Net;
using System.Text;
using System.Text.Json;
using Basamento.Synapsys.Internal;

namespace Basamento.Synapsys.IntegrationTests;

public sealed class HttpTransportTests
{
    [Fact]
    public async Task HealthIsUnauthenticatedAndHeartbeatUsesBearerToken()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"processes\":[]}");
        using var client = new HttpClient(handler);
        using var transport = new SynapsysHttpTransport(Options(), client);

        await transport.HealthAsync(CancellationToken.None);
        var result = await transport.HeartbeatAsync(Request(), CancellationToken.None);

        Assert.True(result.IsHttpSuccess);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("Bearer test-token", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task SerializesOpaqueRunIdAsExactJsonNumber()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"processes\":[]}");
        using var client = new HttpClient(handler);
        using var transport = new SynapsysHttpTransport(Options(), client);
        var request = Request(9_223_372_036_854_000_001);

        await transport.HeartbeatAsync(request, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Requests[0].Body!);
        var runId = document.RootElement
            .GetProperty("processes")[0]
            .GetProperty("runId")
            .GetInt64();
        Assert.Equal(9_223_372_036_854_000_001, runId);
        Assert.DoesNotContain("\"9223372036854000001\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    public async Task RejectsMalformedSuccessfulHeartbeat(string body)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, body);
        using var client = new HttpClient(handler);
        using var transport = new SynapsysHttpTransport(Options(), client);

        var result = await transport.HeartbeatAsync(Request(), CancellationToken.None);

        Assert.True(result.Error is not null || result.IsMalformedSuccess);
    }

    [Fact]
    public async Task IgnoresUnknownResponseFields()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(
            HttpStatusCode.OK,
            "{\"future\":true,\"processes\":[{\"name\":\"p\",\"desiredState\":\"future\",\"desiredToken\":7,\"runId\":1,\"another\":{}}]}");
        using var client = new HttpClient(handler);
        using var transport = new SynapsysHttpTransport(Options(), client);

        var result = await transport.HeartbeatAsync(Request(), CancellationToken.None);

        Assert.True(result.IsHttpSuccess);
        Assert.Equal("future", Assert.Single(result.Response!.Processes!).DesiredState);
    }

    [Fact]
    public async Task HonorsBasePathWhenConstructingEndpoints()
    {
        var options = Options();
        options.CoreUrl = new Uri("https://core.example.test/synapsys/");
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"processes\":[]}");
        using var client = new HttpClient(handler);
        using var transport = new SynapsysHttpTransport(options, client);

        await transport.HeartbeatAsync(Request(), CancellationToken.None);

        Assert.Equal(
            "https://core.example.test/synapsys/api/v1/workers/heartbeat",
            handler.Requests[0].Uri.AbsoluteUri);
    }

    private static SynapsysOptions Options() => new()
    {
        CoreUrl = new Uri("https://core.example.test"),
        CoreToken = "test-token",
        WorkerName = "worker",
        HeartbeatInterval = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        RequestTimeout = TimeSpan.FromSeconds(1),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
    };

    private static HeartbeatRequest Request(long? runId = null) => new()
    {
        WorkerName = "worker",
        HeartbeatIntervalSeconds = 5,
        Processes = new[]
        {
            new HeartbeatProcess
            {
                Name = "process",
                Type = "progressive",
                CurrentState = "idle",
                AcknowledgedToken = 0,
                RunId = runId,
            },
        },
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        internal List<RecordedRequest> Requests { get; } = new();

        internal void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body));
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(Uri Uri, string? Authorization, string? Body);
}

