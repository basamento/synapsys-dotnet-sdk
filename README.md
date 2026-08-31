# Synapsys SDK for .NET

`Basamento.Synapsys` wires existing .NET business processes to Synapsys Core lifecycle control. Synapsys is not a scheduler: Core decides when a process starts or stops, and the SDK never repeatedly invokes finite work.

The repository contains two NuGet packages:

| Package | Purpose |
| --- | --- |
| `Basamento.Synapsys` | Framework-neutral worker, lifecycle, HTTP transport, process state, heartbeat, configuration, and explicit logging sinks. |
| `Basamento.Synapsys.Extensions.Hosting` | Optional Generic Host, dependency-injection, options/configuration, `IHostedService`, `IHttpClientFactory`, and `ILoggerProvider` integration. |

Both packages target `net8.0`, use nullable reference types, and are designed for trimming and Native AOT where their dependencies permit it. ASP.NET Core and Worker Services can use the hosting package; console apps and other environments can use the core package directly. Legacy .NET Framework 4.x is not an initial target.

## Install

Framework-neutral:

```shell
dotnet add package Basamento.Synapsys
```

Generic Host, ASP.NET Core, or Worker Service:

```shell
dotnet add package Basamento.Synapsys.Extensions.Hosting
```

```xml
<PackageReference Include="Basamento.Synapsys.Extensions.Hosting" Version="0.1.0-alpha.1" />
```

The hosting package references the core package transitively.

## Framework-neutral usage

Existing business logic does not need a Synapsys dependency:

```csharp
public sealed class OrderConsumer
{
    public async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ConsumeNextOrderAsync(cancellationToken);
        }
    }
}

public sealed class CatalogImporter
{
    public Task ImportAsync() => ImportCurrentCatalogAsync();
}
```

Wire the methods once at the application boundary:

```csharp
using Basamento.Synapsys;
using Microsoft.Extensions.Logging;

var options = new SynapsysOptions
{
    CoreUrl = new Uri("https://core.example.com"),
    CoreToken = "worker-token",
    WorkerName = "orders-worker-1",
};

using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
await using var worker = new SynapsysWorker(options, loggerFactory);

var consumer = new OrderConsumer();
var importer = new CatalogImporter();
worker.RegisterEndless("orders-consumer", consumer.ConsumeAsync);
worker.RegisterProgressive("catalog-import", importer.ImportAsync);

await worker.StartAsync();
await applicationStopping;
await worker.StopAsync();
```

Synchronous blocking Endless logic runs on a long-running task rather than occupying a thread-pool worker. If it has a native stop method, adapt that method explicitly:

```csharp
worker.RegisterEndless(
    "legacy-listener",
    listener.Run,
    new SynapsysProcessOptions { OnStop = listener.Close });
```

Progressive functions run exactly once per accepted Core start. They return naturally; the SDK does not schedule or loop them. All start/stop operations are idempotent and a process never overlaps itself. A start received while that process is stopping is deferred and starts after the old body returns.

## Generic Host and dependency injection

```csharp
using Basamento.Synapsys.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped<OrderService>();

builder.Services
    .AddSynapsys(builder.Configuration.GetSection("Synapsys"))
    .AddEndless<OrderService>(
        "orders-consumer",
        static (service, token) => service.ConsumeAsync(token))
    .AddProgressive<OrderService>(
        "daily-export",
        static service => service.ExportAsync());

await builder.Build().RunAsync();
```

Each accepted DI process run gets a fresh service scope. Scoped and transient services are disposed when the business method returns. The worker and registrations are singletons; the process service itself follows its normal DI registration lifetime.

`AddSynapsys` accepts canonical kebab-case names (`core-url`) and idiomatic .NET names (`CoreUrl`) but rejects unknown keys to catch misspellings.

For Generic Host configuration, use its standard double-underscore environment mapping, such as `Synapsys__CoreUrl` and `Synapsys__CoreToken`. A supplied configuration section is explicit and overrides the canonical `SYNAPSYS_*` defaults read by `SynapsysOptions`.

## Configuration

| Option | Environment variable | Default |
| --- | --- | --- |
| `Enabled` | `SYNAPSYS_ENABLED` | `true` |
| `CoreUrl` | `SYNAPSYS_CORE_URL` | required when enabled |
| `CoreToken` | `SYNAPSYS_CORE_TOKEN` | none; warned when missing |
| `WorkerName` | `SYNAPSYS_WORKER_NAME` | entry assembly name |
| `Host` | `SYNAPSYS_HOST` | machine host name |
| `HeartbeatInterval` | `SYNAPSYS_HEARTBEAT_INTERVAL` | `5s` |
| `FailFast` | `SYNAPSYS_FAIL_FAST` | `false` |
| `ConnectTimeout` | `SYNAPSYS_CONNECT_TIMEOUT` | `2s` |
| `RequestTimeout` | `SYNAPSYS_REQUEST_TIMEOUT` | `5s` |
| `ShutdownTimeout` | `SYNAPSYS_SHUTDOWN_TIMEOUT` | `10s` |
| `CaptureConsole` | `SYNAPSYS_CAPTURE_CONSOLE` | `true` |

Environment duration values require a unit, such as `250ms`, `5s`, `2m`, or `1h`. `HeartbeatInterval` must be a whole number of seconds.

## Logging

The core package depends only on `Microsoft.Extensions.Logging.Abstractions`. Diagnostics use the application's `ILoggerFactory`, so Serilog, NLog, and other `Microsoft.Extensions.Logging` providers work through their standard adapters.

The SDK intentionally never calls `Console.SetOut` or `Console.SetError`; those APIs mutate process-global state and are unsafe with concurrent processes. Direct users can intentionally forward records or text:

```csharp
var processLogger = worker.CreateLogger("orders-consumer", applicationLogger);
var stdout = worker.CreateTextWriter(
    "legacy-listener",
    SynapsysLogSource.StandardOutput,
    Console.Out);
```

The hosting package automatically captures application `ILogger` records produced inside an active process's execution context. SDK transport diagnostics are excluded, preventing recursive log submission. Logs use structured metadata, bounded buffering, UTF-8 batch limits, per-run sequence numbers, and retry after rejected or malformed heartbeats. If the bounded queue fills, the oldest records are dropped so business work is never blocked.

## Lifecycle behavior

- Endless logic remains active until it returns or cooperatively observes cancellation.
- Progressive logic is finite and runs once for each accepted start.
- Cancellation is cooperative. Ignored cancellation leaves the process in `stopping` until its body returns.
- Shutdown uses one hard budget for heartbeat termination, business cancellation, cleanup, and the final best-effort heartbeat.
- Unhandled business exceptions are observed, logged, and reported as `failed`; they never become unobserved task exceptions.
- The HTTP transport uses asynchronous `HttpClient` I/O. A caller-supplied client is never disposed; the hosting package uses `IHttpClientFactory`.

## Development

The repository pins the .NET 10 SDK while targeting the .NET 8 LTS runtime. Install both runtimes when testing locally.

```shell
dotnet restore --locked-mode
dotnet format Synapsys.slnx --verify-no-changes --no-restore
dotnet build Synapsys.slnx -c Release --no-restore
dotnet test Synapsys.slnx -c Release --no-build --no-restore
dotnet pack src/Basamento.Synapsys/Basamento.Synapsys.csproj -c Release --no-build
dotnet pack src/Basamento.Synapsys.Extensions.Hosting/Basamento.Synapsys.Extensions.Hosting.csproj -c Release --no-build
```

See [RELEASING.md](RELEASING.md) for NuGet.org and tag-release setup. Public APIs are tracked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`; breaking public API changes require an intentional semantic-version decision.

The optional live integration test creates and revokes a disposable Core worker token. Set `SYNAPSYS_SANDBOX_E2E=true`, `SYNAPSYS_SANDBOX_CORE_URL`, `SYNAPSYS_SANDBOX_USERNAME`, and `SYNAPSYS_SANDBOX_PASSWORD`, then filter for `SandboxCoreTests`. Never commit those values.

## License

Apache License 2.0. See [LICENSE](LICENSE).

Copyright 2026 Julian Marzoli (Basamento).
