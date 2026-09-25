namespace Domain.Entities;

/// <summary>A public website inquiry owned permanently by the existing commerce business.</summary>
public sealed class CommerceWebsiteInquiry
{
    public string NotificationStatus { get; set; } = "Pending";
    public int NotificationAttempts { get; set; }
    public DateTime? NotificationNextAttemptUtc { get; set; }
    public DateTime? NotificationSentUtc { get; set; }
    public Guid NotificationRevision { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }
    public Guid PublishedVersionId { get; set; }
    public Guid SubmissionId { get; set; }
    public Guid? WebsiteLeadId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SourcePath { get; set; } = "/";
    public string Status { get; set; } = "New";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
