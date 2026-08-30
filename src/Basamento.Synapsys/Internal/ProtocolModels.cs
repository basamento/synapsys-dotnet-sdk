using System.Text.Json.Serialization;

namespace Basamento.Synapsys.Internal;

internal sealed class HeartbeatProcess
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("currentState")]
    public required string CurrentState { get; init; }

    [JsonPropertyName("ackToken")]
    public long AcknowledgedToken { get; init; }

    [JsonPropertyName("runId")]
    public long? RunId { get; init; }
}

internal sealed class HeartbeatRequest
{
    [JsonPropertyName("workerName")]
    public required string WorkerName { get; init; }

    [JsonPropertyName("framework")]
    public string Framework { get; init; } = "dotnet";

    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; init; }

    [JsonPropertyName("heartbeatInterval")]
    public long HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("processes")]
    public required IReadOnlyList<HeartbeatProcess> Processes { get; init; }

    [JsonPropertyName("logs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProcessLogEntry>? Logs { get; init; }
}

internal sealed class ProcessLogEntry
{
    [JsonPropertyName("processName")]
    public required string ProcessName { get; init; }

    [JsonPropertyName("runId")]
    public long RunId { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

internal sealed class HeartbeatResponse
{
    [JsonPropertyName("processes")]
    public List<ProcessUpdate>? Processes { get; init; }
}

internal sealed class ProcessUpdate
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("desiredState")]
    public string DesiredState { get; init; } = string.Empty;

    [JsonPropertyName("desiredToken")]
    public long? DesiredToken { get; init; }

    [JsonPropertyName("runId")]
    public long? RunId { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
internal sealed partial class SynapsysJsonContext : JsonSerializerContext;

