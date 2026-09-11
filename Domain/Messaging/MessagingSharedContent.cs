using Domain.Social;
namespace Domain.Messaging;

public sealed record MessagingSharedContent(Guid SourcePostId, string Status,
    string? ContentType, string? Body, string? AuthorDisplayName,
    IReadOnlyList<SocialMediaAssetView> Media, string Url);
