namespace Domain.Entities;

/// <summary>Filed relationship history. Corrections and task changes are new linked entries.</summary>
public sealed class CrmHistoryEntry
{
    public Guid Id { get; set; }
    public string RelationshipKey { get; set; } = "";
    public string RecordedBy { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public DateTime FiledUtc { get; set; }
    public DateTime? OccurredUtc { get; set; }
    public string? OccurredDate { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public string TimePrecision { get; set; } = "unknown";
    public string Type { get; set; } = "Note";
    public string Source { get; set; } = "Manual";
    public string SourceId { get; set; } = "";
    public string ContentJson { get; set; } = "{}";
    public Guid? SubmissionId { get; set; }
    public Guid? RelatedEntryId { get; set; }
    public string? Product { get; set; }
    public string? FormName { get; set; }
    public string? FormVersion { get; set; }
    public string? CaseReference { get; set; }
    public string? AssignedOwner { get; set; }
    public DateTime? DueUtc { get; set; }
    public string Status { get; set; } = "Filed";
    public string EvidenceStatus { get; set; } = "Evidence unavailable";
}
