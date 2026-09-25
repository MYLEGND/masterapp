namespace Domain.Entities;

/// <summary>
/// Private Website Studio collaboration metadata. Comments are anchored to the
/// canonical website state/revision and never serialized into public website content.
/// </summary>
public sealed class WebsiteStudioComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WebsiteContentStateId { get; set; }
    public Guid? WebsiteContentVersionId { get; set; }
    public long AnchorRevision { get; set; }
    public string PagePath { get; set; } = "/";
    public string? ElementId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public string AuthorUserId { get; set; } = string.Empty;
    public string? AuthorEmail { get; set; }
    public string AuthorRole { get; set; } = "editor";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? ResolvedByUserId { get; set; }
    public DateTime? ResolvedUtc { get; set; }
}
