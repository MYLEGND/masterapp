using Domain.Messaging;

namespace Domain.Social;

public static class SocialPostContentTypes
{
    public const string Post = "Post";
    public const string Story = "Story";
    public const string Reel = "Reel";
}

public static class SocialPostAudiences
{
    public const string AuthorizedNetwork = "AuthorizedNetwork";
}

public static class SocialReactionTypes
{
    public const string Appreciate = "Appreciate";
}

public sealed record SocialFeedActor(MessagingActor Identity, Guid ProfileId, string DisplayName);

public sealed record SocialAuthor(string UserId, string ParticipantType, Guid ProfileId, string DisplayName);

public sealed record SocialCommentView(Guid Id, SocialAuthor Author, string Body, DateTime CreatedUtc);

public sealed record SocialPostView(
    Guid Id,
    SocialAuthor Author,
    string ContentType,
    string Body,
    DateTime PostedUtc,
    DateTime? ExpiresUtc,
    int ReactionCount,
    int CommentCount,
    bool ReactedByCurrentActor,
    bool FollowedByCurrentActor,
    IReadOnlyList<SocialCommentView> Comments);

public sealed record SocialActivityView(Guid Id, string Kind, SocialAuthor Actor, Guid? PostId, DateTime OccurredUtc);

public sealed record SocialFeedSnapshot(
    IReadOnlyList<SocialPostView> Stories,
    IReadOnlyList<SocialPostView> Posts,
    IReadOnlyList<SocialActivityView> Activity,
    int ActivityCount);

public sealed record CreateSocialPostCommand(SocialFeedActor Actor, string ContentType, string Body);
public sealed record SocialPostMutationCommand(SocialFeedActor Actor, Guid PostId);
public sealed record CreateSocialCommentCommand(SocialFeedActor Actor, Guid PostId, string Body);
public sealed record SocialFollowCommand(SocialFeedActor Actor, string FollowedUserId, string FollowedParticipantType);

public sealed record SocialOperationResult<T>(bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null, T? Value = default)
{
    public static SocialOperationResult<T> Success(T value) => new(true, null, null, value);
    public static SocialOperationResult<T> Failure(string code, string message) => new(false, code, message, default);
}

public interface ISocialFeedService
{
    Task<SocialOperationResult<SocialFeedSnapshot>> GetFeedAsync(SocialFeedActor actor, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> CreatePostAsync(CreateSocialPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> ToggleReactionAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialCommentView>> AddCommentAsync(CreateSocialCommentCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleFollowAsync(SocialFollowCommand command, CancellationToken cancellationToken = default);
}
