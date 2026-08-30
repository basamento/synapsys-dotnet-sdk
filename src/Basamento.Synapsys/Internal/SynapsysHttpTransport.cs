using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Basamento.Synapsys.Internal;

internal interface ISynapsysTransport : IDisposable
{
    Task<HttpStatusCode> HealthAsync(CancellationToken cancellationToken);

    Task<HeartbeatTransportResult> HeartbeatAsync(
        HeartbeatRequest request,
        CancellationToken cancellationToken);
}

internal sealed record HeartbeatTransportResult(
    HttpStatusCode StatusCode,
    HeartbeatResponse? Response,
    Exception? Error)
{
    internal bool IsHttpSuccess =>
        Error is null && (int)StatusCode is >= 200 and < 300;

    internal bool IsMalformedSuccess =>
        IsHttpSuccess && Response?.Processes is null;
}

internal sealed class SynapsysHttpTransport : ISynapsysTransport
{
    private const int MaximumResponseBytes = 1 << 20;

    private readonly SynapsysOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal SynapsysHttpTransport(SynapsysOptions options, HttpClient? client = null)
    {
        _options = options;
        if (client is not null)
        {
            _client = client;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsClient = true;
    }

    public async Task<HttpStatusCode> HealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/api/health"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        return response.StatusCode;
    }

    public async Task<HeartbeatTransportResult> HeartbeatAsync(
        HeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/v1/workers/heartbeat"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(_options.CoreToken))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CoreToken);
            }

            message.Content = JsonContent.Create(
                request,
                SynapsysJsonContext.Default.HeartbeatRequest,
                new MediaTypeHeaderValue("application/json"));

            using var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return new HeartbeatTransportResult(response.StatusCode, null, null);
            }

            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (body.Length == 0)
            {
                return new HeartbeatTransportResult(response.StatusCode, null, null);
            }

            try
            {
                var decoded = JsonSerializer.Deserialize(
                    body,
                    SynapsysJsonContext.Default.HeartbeatResponse);
                return new HeartbeatTransportResult(response.StatusCode, decoded, null);
            }
            catch (JsonException exception)
            {
                return new HeartbeatTransportResult(response.StatusCode, null, exception);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new HeartbeatTransportResult(0, null, exception);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        return timeout;
    }

    private Uri Endpoint(string endpoint)
    {
        var baseUrl = _options.CoreUrl
            ?? throw new InvalidOperationException("CoreUrl was not validated before transport use.");
        return new Uri(
            baseUrl.AbsoluteUri.TrimEnd('/') + "/" + endpoint.TrimStart('/'),
            UriKind.Absolute);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new HttpRequestException(
                    $"Core response exceeded {MaximumResponseBytes} bytes.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}
