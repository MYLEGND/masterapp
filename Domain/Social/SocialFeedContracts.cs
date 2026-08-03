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
/// The post-level boundary between a durable creator draft and content that is
/// eligible for Legend audiences. Media processing is intentionally a separate
/// asset lifecycle; publishing never makes an unfinished video playable.
/// </summary>
public static class SocialPostPublicationStates
{
    public const string Draft = "Draft";
    public const string Published = "Published";

    public static bool IsPublished(string? state) =>
        string.Equals(state, Published, StringComparison.Ordinal);
}

/// <summary>
/// One persisted lifecycle for every social media asset. Video moves through
/// this state machine after its multipart source file has been durably saved;
/// images enter Ready immediately because they do not require FFmpeg.
/// </summary>
public static class SocialMediaProcessingStates
{
    public const string PendingProcessing = "PendingProcessing";
    public const string Processing = "Processing";
    public const string Ready = "Ready";
    public const string Failed = "Failed";

    public static bool IsReady(string? state) =>
        string.Equals(state, Ready, StringComparison.Ordinal);
}

/// <summary>
/// Canonical ingress limits for social media. The request allowance includes a
/// small multipart envelope so the maximum accepted video itself remains the
/// same size as the secure-media storage policy.
/// </summary>
public static class SocialMediaUploadLimits
{
    public const long MaximumMediaBytes = 100L * 1024L * 1024L;
    /// <summary>
    /// The single server-authoritative duration ceiling for every uploaded
    /// Legend video. Native clients apply the same limit before constructing a
    /// multipart body, while storage verifies it before FFmpeg is allowed to
    /// consume CPU.
    /// </summary>
    public const double MaximumVideoDurationSeconds = 600d;
    /// <summary>
    /// A creator-selected Hac poster is a small JPEG that travels with the
    /// video upload. Keeping this independently bounded preserves the existing
    /// video allowance.
    /// </summary>
    public const long MaximumPreviewImageBytes = 1L * 1024L * 1024L;
    public const long MultipartEnvelopeBytes = 1024L * 1024L;
    public const long MaximumMultipartRequestBytes =
        MaximumMediaBytes + MaximumPreviewImageBytes + MultipartEnvelopeBytes;
    public const int MaximumFormValueLength = 2_000;

    /// Hacs are normalized on iOS to H.264/AAC MP4 before upload. Requiring
    /// this container at the shared social boundary prevents an arbitrary MOV
    /// or device-specific codec from being published as a "ready" Hac that
    /// cannot be rendered by the vertical player.
    public static bool IsPortableHacVideoFileName(string? fileName) =>
        string.Equals(
            Path.GetExtension(fileName?.Trim() ?? string.Empty),
            ".mp4",
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Retained for post-level preferences and legacy data. Account privacy is the
/// outer visibility authority: public posts reach active members, while private
/// posts reach their owner and approved followers.
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

public static class SocialFollowStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";

    public static string? Normalize(string? value) => value?.Trim() switch
    {
        Pending => Pending,
        Accepted => Accepted,
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
    string? PublicEmail = null,
    bool IsPrivate = false,
    string? RoleLabel = null,
    // Kept inside the social authority so the mobile-profile visibility rule
    // can project it without creating a second contact record.
    string? Phone = null,
    string? PublicPhone = null);

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
    string? AudioUrl,
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
    string? AccessibilityText,
    bool HasPreviewImage);

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
    bool FollowRequestPending,
    bool SavedByCurrentActor,
    bool RepostedByCurrentActor,
    SocialPostMetrics Metrics,
    SocialPostMusicView? Music,
    IReadOnlyList<SocialMediaAssetView> Media,
    IReadOnlyList<SocialCommentView> Comments);

public sealed record SocialActivityView(Guid Id, string Kind, SocialAuthor Actor, Guid? PostId, DateTime OccurredUtc);

/// <summary>
/// Safe public invitation metadata for a promoted Founder-owned group. It
/// deliberately contains no conversation messages or participant identities.
/// </summary>
public sealed record SocialPromotedGroupView(
    Guid ConversationId,
    string Subject,
    SocialAuthor Owner,
    MessagingGroupImage? GroupImage,
    int ActiveMemberCount,
    bool IsJoinedByCurrentActor,
    DateTime PromotionStartedUtc);

public sealed record SocialFeedSnapshot(
    IReadOnlyList<SocialPostView> Stories,
    IReadOnlyList<SocialPostView> Posts,
    IReadOnlyList<SocialPostView> Hacs,
    IReadOnlyList<SocialActivityView> Activity,
    int ActivityCount,
    SocialProfileMetrics CurrentProfileMetrics,
    SocialCreatorInsights CreatorInsights)
{
    /// <summary>
    /// Active Founder-owned group invitations are ordered by the server ahead
    /// of normal posts. They are a projection of MessageConversation, never a
    /// SocialPost representation.
    /// </summary>
    public IReadOnlyList<SocialPromotedGroupView> PromotedGroups { get; init; } = [];
}

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
    SocialPostDetails? Details = null,
    SocialMediaUpload? PreviewImage = null,
    bool PublishImmediately = true);

/// <summary>
/// Finalizes the one durable media draft created while the member was editing.
/// It changes post visibility and optional metadata; it never uploads the
/// primary source media a second time.
/// </summary>
public sealed record PublishStagedSocialMediaPostCommand(
    SocialFeedActor Actor,
    Guid PostId,
    string Body,
    SocialMusicSelection? Music = null,
    SocialPostDetails? Details = null,
    SocialMediaUpload? PreviewImage = null);

public sealed record SocialMediaStream(
    Stream Content,
    string MimeType);

public sealed record SocialPostMutationCommand(SocialFeedActor Actor, Guid PostId);
public sealed record UpdateSocialPostCommand(SocialFeedActor Actor, Guid PostId, string Body);
public sealed record CreateSocialCommentCommand(SocialFeedActor Actor, Guid PostId, string Body, Guid? ParentCommentId = null);
public sealed record SocialFollowCommand(SocialFeedActor Actor, string FollowedUserId, string FollowedParticipantType, Guid? SourcePostId = null);
public sealed record SocialFollowRequestDecisionCommand(SocialFeedActor Actor, Guid FollowRequestId, bool Approve);
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
    string? AudioUrl);

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

public sealed record SocialFollowResult(bool IsFollowing, bool IsPending);

public sealed record SocialFollowRequestView(
    Guid Id,
    SocialAuthor Profile,
    DateTime RequestedUtc);

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
    Task<SocialOperationResult<IReadOnlyList<SocialPostView>>> GetPublicProfilePostsAsync(SocialFeedActor actor, SocialAuthor profile, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> CreatePostAsync(CreateSocialPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> CreateMediaPostAsync(CreateSocialMediaPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> PublishStagedMediaPostAsync(PublishStagedSocialMediaPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> UpdatePostAsync(UpdateSocialPostCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> DeletePostAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialMediaStream>> GetMediaAsync(SocialFeedActor actor, Guid mediaAssetId, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialMediaStream>> GetMediaPreviewAsync(SocialFeedActor actor, Guid mediaAssetId, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostView>> ToggleReactionAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialCommentView>> AddCommentAsync(CreateSocialCommentCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialFollowResult>> ToggleFollowAsync(SocialFollowCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleSaveAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> ToggleRepostAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<bool>> RecordShareAsync(SocialPostMutationCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialPostMetrics>> RecordViewAsync(RecordSocialPostViewCommand command, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialProfileMetrics>> GetProfileMetricsAsync(SocialFeedActor actor, SocialAuthor? profile = null, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>> GetCurrentProfileFollowListAsync(SocialFeedActor actor, string listKind, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<IReadOnlyList<SocialFollowRequestView>>> GetIncomingFollowRequestsAsync(SocialFeedActor actor, CancellationToken cancellationToken = default);
    Task<SocialOperationResult<SocialFollowResult>> DecideFollowRequestAsync(SocialFollowRequestDecisionCommand command, CancellationToken cancellationToken = default);
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
