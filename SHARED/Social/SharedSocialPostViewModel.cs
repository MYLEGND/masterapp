using System;
using System.Collections.Generic;

namespace Shared.Social;

// Presentation-only shape for the canonical original-post Razor view.
// Domain authorization, mutations, and counters stay with ISocialFeedService.
public sealed record SharedSocialPostViewModel(
    Guid Id, SharedSocialAuthorView Author, string ContentType, string Body,
    string? Location, bool CommentsEnabled, DateTime PostedUtc,
    int ReactionCount, int CommentCount, bool ReactedByCurrentActor,
    bool SavedByCurrentActor, bool RepostedByCurrentActor,
    SharedSocialMetricsView Metrics, SharedSocialMusicView? Music,
    IReadOnlyList<SharedSocialMediaView> Media, IReadOnlyList<SharedSocialCommentView> Comments);
public sealed record SharedSocialAuthorView(string DisplayName, string? AvatarUrl = null);
public sealed record SharedSocialMetricsView(int ShareCount, int RepostCount);
public sealed record SharedSocialMusicView(string TrackTitle, string ArtistName, string? AudioUrl);
public sealed record SharedSocialMediaView(Guid Id, int DisplayOrder, string MediaKind, string? AccessibilityText);
public sealed record SharedSocialCommentView(Guid Id, SharedSocialAuthorView Author,
    Guid? ParentCommentId, string Body, DateTime CreatedUtc);
