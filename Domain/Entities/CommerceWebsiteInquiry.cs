namespace Domain.Entities;

/// <summary>A public website inquiry owned permanently by the existing commerce business.</summary>
public sealed class CommerceWebsiteInquiry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }
    public Guid PublishedVersionId { get; set; }
    public Guid SubmissionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SourcePath { get; set; } = "/";
    public string Status { get; set; } = "New";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
