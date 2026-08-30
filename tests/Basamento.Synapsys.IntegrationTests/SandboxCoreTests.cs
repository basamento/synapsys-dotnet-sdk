using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basamento.Synapsys.IntegrationTests;

public sealed class SandboxCoreTests
{
    [Fact]
    public async Task DisposableWorkerCompletesLiveStartStopAndProgressiveRuns()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SYNAPSYS_SANDBOX_E2E"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set SYNAPSYS_SANDBOX_E2E=true and sandbox credentials to run this live test.");
        }

        var username = RequiredEnvironment("SYNAPSYS_SANDBOX_USERNAME");
        var password = RequiredEnvironment("SYNAPSYS_SANDBOX_PASSWORD");
        var baseUri = NormalizeCoreUrl(
            Environment.GetEnvironmentVariable("SYNAPSYS_SANDBOX_CORE_URL") ??
            "http://localhost:8080");
        var cancellationToken = TestContext.Current.CancellationToken;
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies,
        };
        using var operatorClient = new HttpClient(handler) { BaseAddress = baseUri };

        await LoginAsync(operatorClient, username, password, cancellationToken);
        var csrf = cookies.GetCookies(baseUri)["synapsys_csrf"]?.Value
            ?? throw new InvalidOperationException("Core login did not issue a CSRF cookie.");
        var workerName = $"dotnet-e2e-{Guid.NewGuid():N}";
        var token = await CreateWorkerTokenAsync(
            operatorClient,
            csrf,
            workerName,
            cancellationToken);
        SynapsysWorker? worker = null;
        Exception? failure = null;

        try
        {
            var endlessEntered = NewSignal();
            var endlessExited = NewSignal();
            var progressiveRan = NewSignal();
            worker = new SynapsysWorker(
                new SynapsysOptions
                {
                    CoreUrl = baseUri,
                    CoreToken = token.Secret,
                    WorkerName = workerName,
                    Host = "dotnet-e2e",
                    HeartbeatInterval = TimeSpan.FromSeconds(1),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    RequestTimeout = TimeSpan.FromSeconds(10),
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    FailFast = true,
                },
                NullLoggerFactory.Instance);
            var endlessLogger = worker.CreateLogger("live-endless");
            var progressiveLogger = worker.CreateLogger("live-progressive");
            worker.RegisterEndless(
                "live-endless",
                async processCancellation =>
                {
                    endlessLogger.LogInformation("Live Endless process entered");
                    endlessEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, processCancellation);
                    }
                    finally
                    {
                        endlessLogger.LogInformation("Live Endless process stopped cooperatively");
                        endlessExited.TrySetResult();
                    }
                });
            worker.RegisterProgressive(
                "live-progressive",
                async cancellation =>
                {
                    progressiveLogger.LogInformation("Live Progressive process ran once");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation);
                    progressiveRan.TrySetResult();
                });

            await worker.StartAsync(cancellationToken);
            var endless = await WaitForProcessAsync(
                operatorClient,
                workerName,
                "live-endless",
                static _ => true,
                cancellationToken);
            Assert.True(
                endless.Framework is "dotnet" or "unknown",
                $"Unexpected sandbox framework value '{endless.Framework}'.");

            await SetDesiredStateAsync(
                operatorClient,
                csrf,
                endless.Id,
                "running",
                cancellationToken);
            await endlessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForProcessAsync(
                operatorClient,
                workerName,
                "live-endless",
                static process => process.CurrentState == "running" && process.AcknowledgedToken >= process.DesiredToken,
                cancellationToken);

            await SetDesiredStateAsync(
                operatorClient,
                csrf,
                endless.Id,
                "stopping",
                cancellationToken);
            await endlessExited.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForProcessAsync(
                operatorClient,
                workerName,
                "live-endless",
                static process => process.CurrentState == "idle" && process.AcknowledgedToken >= process.DesiredToken,
                cancellationToken);

            var progressive = await WaitForProcessAsync(
                operatorClient,
                workerName,
                "live-progressive",
                static _ => true,
                cancellationToken);
            await SetDesiredStateAsync(
                operatorClient,
                csrf,
                progressive.Id,
                "running",
                cancellationToken);
            await progressiveRan.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForProcessAsync(
                operatorClient,
                workerName,
                "live-progressive",
                static process => process.CurrentState == "idle" && process.AcknowledgedToken >= process.DesiredToken,
                cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    await worker.DisposeAsync();
                }

                await RevokeWorkerTokenAsync(
                    operatorClient,
                    csrf,
                    token.Id,
                    cancellationToken);
            }
            catch (Exception cleanupException)
            {
                failure = failure is null
                    ? cleanupException
                    : new AggregateException(failure, cleanupException);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<WorkerToken> CreateWorkerTokenAsync(
        HttpClient client,
        string csrf,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = JsonRequest(HttpMethod.Post, "/api/auth/worker-tokens", csrf, new { name });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        return new WorkerToken(
            document.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Token response omitted id."),
            document.RootElement.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("Token response omitted secret."));
    }

    private static async Task RevokeWorkerTokenAsync(
        HttpClient client,
        string csrf,
        string id,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/auth/worker-tokens/{Uri.EscapeDataString(id)}");
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SetDesiredStateAsync(
        HttpClient client,
        string csrf,
        long processId,
        string desiredState,
        CancellationToken cancellationToken)
    {
        using var request = JsonRequest(
            HttpMethod.Put,
            $"/api/processes/{processId}/desired-state",
            csrf,
            new { desiredState });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<RemoteProcess> WaitForProcessAsync(
        HttpClient client,
        string workerName,
        string processName,
        Func<RemoteProcess, bool> predicate,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/api/processes", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            foreach (var item in document.RootElement.GetProperty("processes").EnumerateArray())
            {
                if (!string.Equals(
                        item.GetProperty("workerName").GetString(),
                        workerName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        item.GetProperty("name").GetString(),
                        processName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var process = new RemoteProcess(
                    item.GetProperty("id").GetInt64(),
                    item.GetProperty("framework").GetString() ?? "unknown",
                    item.GetProperty("currentState").GetString() ?? "",
                    item.GetProperty("desiredToken").GetInt64(),
                    item.GetProperty("ackToken").GetInt64());
                if (predicate(process))
                {
                    return process;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException(
            $"Sandbox process '{workerName}/{processName}' did not reach the expected state.");
    }

    private static HttpRequestMessage JsonRequest<T>(
        HttpMethod method,
        string path,
        string csrf,
        T body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static Uri NormalizeCoreUrl(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        return string.Equals(uri.AbsolutePath.TrimEnd('/'), "/workers", StringComparison.OrdinalIgnoreCase)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute)
            : uri;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name} to run the sandbox test.");

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record WorkerToken(string Id, string Secret);

    private sealed record RemoteProcess(
        long Id,
        string Framework,
        string CurrentState,
        long DesiredToken,
        long AcknowledgedToken);
}
