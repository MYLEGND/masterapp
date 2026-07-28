namespace Domain.Entities;

public sealed class SocialPostComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string AuthorUserId { get; set; } = string.Empty;
    public string AuthorParticipantType { get; set; } = string.Empty;
    public Guid AuthorProfileId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
    public SocialPost SocialPost { get; set; } = null!;
    public SocialPostComment? ParentComment { get; set; }
}
