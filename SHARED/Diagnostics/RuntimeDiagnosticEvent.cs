using System.Text.Json.Serialization;

namespace Shared.Diagnostics;

// Observation transport only. Client metadata never grants release, repair or
// defect-confirmation authority. Raw text is reduced before durable retention.
public sealed record RuntimeDiagnosticEvent
{
    [JsonPropertyName("appIdentifier")] public string? AppIdentifier { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("route")] public string? Route { get; init; }
    [JsonPropertyName("sourceFilePath")] public string? SourceFilePath { get; init; }
    [JsonPropertyName("errorName")] public string? ErrorName { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("stackTrace")] public string? StackTrace { get; init; }
    [JsonPropertyName("gitCommitHash")] public string? GitCommitHash { get; init; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; init; }
    [JsonPropertyName("operation")] public string? Operation { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("statusCode")] public int? StatusCode { get; init; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }
}

public interface IRuntimeDiagnosticSink
{
    Task RecordAsync(RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}
