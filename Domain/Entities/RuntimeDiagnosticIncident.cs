namespace Domain.Entities;

// Sanitized observations only. AnalyticsDriftAlert owns metric drift and cannot
// represent these cross-platform runtime incidents or their explicit review.
public sealed class RuntimeDiagnosticIncident
{
    public Guid Id { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public string AppIdentifier { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string ErrorName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Category { get; set; } = "Observation";
    public int? StatusCode { get; set; }
    public string? GitCommitHash { get; set; }
    public bool ReleaseVerified { get; set; }
    public string? AppVersion { get; set; }
    public string? SourceFilePath { get; set; }
    public string? StackTrace { get; set; }
    public string? CorrelationId { get; set; }
    public string Disposition { get; set; } = "Observed";
    public int ReviewVersion { get; set; }
    public bool Recurred { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public long Occurrences { get; set; } = 1;
}
