namespace Domain.Entities;

public sealed class JourneyCircleBlock
{
    public Guid Id { get; set; }
    public Guid BlockerClientProfileId { get; set; }
    public Guid BlockedClientProfileId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class JourneyCircleReport
{
    public Guid Id { get; set; }
    public Guid ReporterClientProfileId { get; set; }
    public Guid ReportedClientProfileId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime CreatedUtc { get; set; }
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
