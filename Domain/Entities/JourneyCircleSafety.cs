namespace Domain.Entities;

/// <summary>
/// The single persisted community block authority. Its original client-profile
/// columns preserve Journey Circles compatibility; the typed identity columns
/// apply the same block to every Legend community surface.
/// </summary>
public sealed class JourneyCircleBlock
{
    public Guid Id { get; set; }
    public Guid? BlockerClientProfileId { get; set; }
    public Guid? BlockedClientProfileId { get; set; }
    public string? BlockerUserId { get; set; }
    public string? BlockerParticipantType { get; set; }
    public string? BlockedUserId { get; set; }
    public string? BlockedParticipantType { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// The single persisted community report authority. Legacy Journey fields are
/// retained for existing reports while all new reports carry typed targets.
/// </summary>
public sealed class JourneyCircleReport
{
    public Guid Id { get; set; }
    public Guid? ReporterClientProfileId { get; set; }
    public Guid? ReportedClientProfileId { get; set; }
    public string? ReporterUserId { get; set; }
    public string? ReporterParticipantType { get; set; }
    public string? ReportedUserId { get; set; }
    public string? ReportedParticipantType { get; set; }
    public string? TargetKind { get; set; }
    public Guid? TargetEntityId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime CreatedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? Resolution { get; set; }
}

public sealed class JourneyCircleModerationEvent
{
    public Guid Id { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public bool RequiresReview { get; set; }
    public DateTime CreatedUtc { get; set; }
}
