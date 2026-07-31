using Domain.Messaging;

namespace Domain.Social;

public static class SocialPostContentTypes
{
    public const string Post = "Post";
    public const string Story = "Story";
    // Retained only as the persisted and API discriminator for Hacs.
    public const string Reel = "Reel";
}

/// <summary>
/// Audience narrows who inside the already-authorized network sees a post. It never
/// widens reach: the messaging recipient authority remains the outer boundary.
/// </summary>
public static class SocialPostAudiences
{
    /// <summary>Everyone the author is authorized to reach.</summary>
    public const string AuthorizedNetwork = "AuthorizedNetwork";

    /// <summary>Authorized participants who follow the author.</summary>
    public const string Followers = "Followers";

    /// <summary>Authorized participants the author and viewer both follow.</summary>
    public const string MutualConnections = "MutualConnections";

    public static string? Normalize(string? value) => value?.Trim() switch
    {
        null or "" => AuthorizedNetwork,
        AuthorizedNetwork => AuthorizedNetwork,
        Followers => Followers,
        MutualConnections => MutualConnections,
        _ => null
    };
}

public static class SocialReactionTypes
{
    public const string Appreciate = "Appreciate";
}

/// <summary>
/// Relationship-list selectors used by the mobile profile. These are API values,
/// not presentation labels, so the app can evolve its wording without changing
/// the server contract.
/// </summary>
public static class SocialFollowListKinds
{
    public const string Follows = "follows";
    public const string Followers = "followers";

    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Follows => Follows,
        Followers => Followers,
        _ => null
    };
}

public static class SocialStoryInteractionTypes
{
    public const string Exit = "Exit";
    public const string TapForward = "TapForward";
    public const string TapBackward = "TapBackward";
}

public sealed record SocialFeedActor(MessagingActor Identity, Guid ProfileId, string DisplayName);

public sealed record SocialAuthor(
    string UserId,
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? Location = null,
    string? PublicEmail = null);

public sealed record SocialCommentView(Guid Id, SocialAuthor Author, Guid? ParentCommentId, string Body, DateTime CreatedUtc);

public sealed record SocialPostMetrics(
    int ViewCount,
    int UniqueViewerCount,
    int ReactionCount,
    int CommentCount,
    int ReplyCount,
    int RepostCount,
    int SaveCount,
    int ShareCount,
    int ProfileVisitCount,
    int FollowsGenerated,
    decimal? AverageWatchDurationSeconds,
    decimal? AverageWatchCompletionPercentage,
    int StoryExitCount,
    int StoryTapForwardCount,
    int StoryTapBackwardCount);

public sealed record SocialPostMusicView(
    string ProviderId,
    string ProviderTrackId,
    string TrackTitle,
    string ArtistName,
    decimal TrackDurationSeconds,
    string? PreviewUrl,
    decimal TrimStartSeconds,
    decimal TrimEndSeconds,
    decimal MusicVolume,
    decimal OriginalAudioVolume);

public sealed record SocialMediaAssetView(
    Guid Id,
    int DisplayOrder,
    string MediaKind,
    string MimeType,
    long FileSizeBytes,
    int? Width,
    int? Height,
    decimal? AspectRatio,
    decimal? DurationSeconds,
    string ProcessingState,
    string? AccessibilityText);

public sealed record SocialPostView(
    Guid Id,
    SocialAuthor Author,
    string ContentType,
    string Body,
    string Audience,
    string? Location,
    bool CommentsEnabled,
    DateTime PostedUtc,
    DateTime? ExpiresUtc,
    int ReactionCount,
    int CommentCount,
    bool ReactedByCurrentActor,
    bool FollowedByCurrentActor,
    bool SavedByCurrentActor,
    bool RepostedByCurrentActor,
    SocialPostMetrics Metrics,
    SocialPostMusicView? Music,
    IReadOnlyList<SocialMediaAssetView> Media,
    IReadOnlyList<SocialCommentView> Comments);

public sealed record SocialActivityView(Guid Id, string Kind, SocialAuthor Actor, Guid? PostId, DateTime OccurredUtc);

public sealed record SocialFeedSnapshot(
    IReadOnlyList<SocialPostView> Stories,
    IReadOnlyList<SocialPostView> Posts,
    IReadOnlyList<SocialActivityView> Activity,
    int ActivityCount,
    SocialProfileMetrics CurrentProfileMetrics,
    SocialCreatorInsights CreatorInsights);

public sealed record SocialPostDetails(
    string? Audience = null,
    string? Location = null,
    bool CommentsEnabled = true);

public sealed record CreateSocialPostCommand(
    SocialFeedActor Actor,
    string ContentType,
    string Body,
    SocialPostDetails? Details = null);

public sealed record SocialMediaUpload(
    string OriginalFileName,
    long DeclaredSizeBytes,
    Stream Content,
    string? AccessibilityText);

public sealed record CreateSocialMediaPostCommand(
    SocialFeedActor Actor,
    string ContentType,
    string Body,
    IReadOnlyList<SocialMediaUpload> Media,
    SocialMusicSelection? Music = null,
    SocialPostDetails? Details = null);

public sealed record SocialMediaStream(
    Stream Content,
    string MimeType);

public sealed record SocialPostMutationCommand(SocialFeedActor Actor, Guid PostId);
public sealed record UpdateSocialPostCommand(SocialFeedActor Actor, Guid PostId, string Body);
public sealed record CreateSocialCommentCommand(SocialFeedActor Actor, Guid PostId, string Body, Guid? ParentCommentId = null);
public sealed record SocialFollowCommand(SocialFeedActor Actor, string FollowedUserId, string FollowedParticipantType, Guid? SourcePostId = null);
public sealed record RecordSocialPostViewCommand(
    SocialFeedActor Actor,
    Guid PostId,
    decimal? WatchDurationSeconds,
    decimal? WatchCompletionPercentage,
    string? StoryInteractionType);
public sealed record SocialProfileVisitCommand(
    SocialFeedActor Actor,
    string TargetUserId,
    string TargetParticipantType,
    Guid? SourcePostId);

public sealed record SocialMusicTrack(
    string ProviderId,
    string ProviderTrackId,
    string TrackTitle,
    string ArtistName,
    decimal TrackDurationSeconds,
    string? PreviewUrl);

public sealed record SocialMusicSelection(
    string ProviderId,
    string ProviderTrackId,
    decimal TrimStartSeconds,
    decimal TrimEndSeconds,
    decimal MusicVolume,
    decimal OriginalAudioVolume);

public sealed record SocialProfileMetrics(
    SocialAuthor Profile,
    int PostCount,
    int VideoCount,
    int StoryCount,
    int FollowerCount,
    int FollowingCount,
    int TotalReactionCount,
    int TotalContentViewCount,
    int TotalReachCount,
    int? PrivateProfileVisitCount);

/// <summary>
/// One server-authoritative relationship row for the current member's profile.
/// <paramref name="FollowedByCurrentActor"/> lets a Followers list render the
/// correct follow-back state without guessing from the feed.
/// </summary>
public sealed record SocialFollowListEntry(
    SocialAuthor Profile,
    bool FollowedByCurrentActor);

public sealed record SocialCreatorInsights(
    DateTime GeneratedUtc,
    int TotalViews,
    int TotalReach,
    int FollowerCount,
    int FollowingCount,
    int FollowersGained,
    int ProfileVisits,
    int TotalReactions,
    int TotalComments,
    int TotalReplies,
    int TotalShares,
    int TotalReposts,
    int TotalSaves,
    decimal EngagementRatePercentage,
    IReadOnlyList<SocialPostInsight> TopPosts,
    IReadOnlyList<SocialPostInsight> TopVideos,
    IReadOnlyList<SocialPostInsight> TopStories);

public sealed record SocialPostInsight(
    Guid PostId,
    string ContentType,
    DateTime PostedUtc,
    SocialPostMetrics Metrics,
    decimal EngagementRatePercentage);

public sealed record SocialOperationResult<T>(bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null, T? Value = default)
{
    public static SocialOperationResult<T> Success(T value) => new(true, null, null, value);
    public static SocialOperationResult<T> Failure(string code, string message) => new(false, code, message, default);
}

public interface ISocialFeedService
{
    Task<SocialOperationResult<SocialFeedSnapshot>> GetFeedAsync(SocialFeedActor actor, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<IReadOnlyList<SocialPostView>>> GetCurrentProfilePostsAsync(SocialFeedActor actor, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> CreatePostAsync(CreateSocialPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> CreateMediaPostAsync(CreateSocialMediaPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> UpdatePostAsync(UpdateSocialPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> DeletePostAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialMediaStream>> GetMediaAsync(SocialFeedActor actor, Guid mediaAssetId, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> ToggleReactionAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialCommentView>> AddCommentAsync(CreateSocialCommentCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleFollowAsync(SocialFollowCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleSaveAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleRepostAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> RecordShareAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostMetrics>> RecordViewAsync(RecordSocialPostViewCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialProfileMetrics>> GetProfileMetricsAsync(SocialFeedActor actor, SocialAuthor? profile = null, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>> GetCurrentProfileFollowListAsync(SocialFeedActor actor, string listKind, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialCreatorInsights>> GetCreatorInsightsAsync(SocialFeedActor actor, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostInsight>> GetPostInsightsAsync(SocialFeedActor actor, Guid postId, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> RecordProfileVisitAsync(SocialProfileVisitCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchMusicAsync(SocialFeedActor actor, string query, CancellationToken cancellationToken = default);
}

public interface ISocialMusicCatalog
{
    Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialMusicTrack>> ResolveAsync(string providerId, string providerTrackId, CancellationToken cancellationToken = default);
}
