using Domain.Messaging;

namespace Domain.Social;

/// <summary>
/// How a discovery result set is ordered.
/// </summary>
public static class SocialDiscoverySortModes
{
    /// <summary>Compatibility-ranked suggestions. Ranking orders results; it never removes them.</summary>
    public const string Recommended = "Recommended";

    /// <summary>Text relevance against the searchable directory.</summary>
    public const string Relevance = "Relevance";

    /// <summary>Stable alphabetical browse over the whole directory.</summary>
    public const string Directory = "Directory";
}

/// <summary>
/// The scope a caller is searching. Derived from the actor's participant type on the
/// server; never accepted from the client.
/// </summary>
public static class SocialDiscoveryScopes
{
    /// <summary>A client searching the consented community directory.</summary>
    public const string Community = "Community";

    /// <summary>An agent searching the clients they own.</summary>
    public const string OwnedClients = "OwnedClients";
}

/// <summary>
/// One-way follow state plus the two-way Journey Circles connection state. They are
/// distinct: a follow is a feed preference, a connection is mutual consent.
/// </summary>
public sealed record SocialDiscoveryRelationship(
    bool FollowedByCurrentActor,
    bool FollowRequestPending,
    bool FollowsCurrentActor,
    string ConnectionStatus,
    Guid? ConnectionId,
    bool CanRequestConnection,
    bool CanFollow);

public sealed record SocialDiscoveryResult(
    Guid ClientProfileId,
    string UserId,
    string ParticipantType,
    string DisplayName,
    string? Headline,
    string? Location,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> CircleCodes,
    int CompatibilityScore,
    string? MatchExplanation,
    SocialDiscoveryRelationship Relationship,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? PublicEmail = null,
    bool IsPrivate = false);

public sealed record SocialDiscoveryPage(
    IReadOnlyList<SocialDiscoveryResult> Results,
    int TotalCount,
    int Offset,
    int PageSize,
    bool HasMore,
    string SortMode,
    string Scope);

public sealed record SocialDiscoveryQuery(
    SocialFeedActor Actor,
    string? SearchText,
    int Offset,
    int PageSize,
    string? SortMode = null);

/// <summary>
/// The community profile a discovery result opens into.
///
/// Post, short-form video, story and follower counts are deliberately absent: those belong to the
/// social feed authority, which applies its own visibility rules. The caller composes
/// the two so Discover never keeps a second copy of content statistics.
/// </summary>
public sealed record SocialDiscoveryProfile(
    SocialDiscoveryResult Summary,
    string? Introduction,
    IReadOnlyList<string> LifeStages,
    IReadOnlyList<string> ConnectionTypes);

public interface ISocialDiscoveryService
{
    Task<SocialOperationResult<SocialDiscoveryPage>> SearchAsync(
        SocialDiscoveryQuery query,
        CancellationToken cancellationToken = default);

    Task<SocialOperationResult<SocialDiscoveryProfile>> GetProfileAsync(
        SocialFeedActor actor,
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether one participant is reachable through the caller's discovery scope.
    /// Follow authorization uses this so a discovered person can actually be followed.
    /// </summary>
    Task<bool> IsDiscoverableByAsync(
        SocialFeedActor actor,
        string targetUserId,
        string targetParticipantType,
        CancellationToken cancellationToken = default);
}
