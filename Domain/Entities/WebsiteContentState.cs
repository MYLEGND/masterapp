namespace Domain.Entities;

public sealed class WebsiteContentState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerKey { get; set; } = "";
    public string SiteKey { get; set; } = "";
    public Guid? CommerceBusinessId { get; set; }
    public string DraftJson { get; set; } = "{}";
    public string NamedDraftsJson { get; set; } = "[]";
    public string? ImportReportJson { get; set; }
    public long Revision { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DateTime? ScheduledPublishUtc { get; set; }
    public long? ScheduledRevision { get; set; }
    public string? ScheduledActorJson { get; set; }
    public string? ScheduleError { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WebsiteContentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StateId { get; set; }
    public long Revision { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public string? CompiledPagesJson { get; set; }
    public string? ImportReportJson { get; set; }
    public string ActorUserId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
