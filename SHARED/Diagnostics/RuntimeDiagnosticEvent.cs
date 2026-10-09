using System.Text.Json.Serialization;

namespace Shared.Diagnostics;

// Privacy-safe browser structural evidence. These values are canonical identifiers only;
// DOM text, field values, arbitrary attributes, URLs with query strings, and user content
// are not part of this contract.
public sealed record RuntimeDiagnosticStructuralReproducer
{
    [JsonPropertyName("componentIds")] public IReadOnlyList<string>? ComponentIds { get; init; }
    [JsonPropertyName("actionKeys")] public IReadOnlyList<string>? ActionKeys { get; init; }
    [JsonPropertyName("compositionIds")] public IReadOnlyList<string>? CompositionIds { get; init; }
    [JsonPropertyName("modalIds")] public IReadOnlyList<string>? ModalIds { get; init; }
}

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
    [JsonPropertyName("structuralReproducer")] public RuntimeDiagnosticStructuralReproducer? StructuralReproducer { get; init; }
}

public interface IRuntimeDiagnosticSink
{
    Task RecordAsync(RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}
