namespace Domain.JourneyCircles;

public static class JourneyCircleConnectionStatuses
{
    /// <summary>No connection record exists between the two members.</summary>
    public const string None = "None";
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Disconnected = "Disconnected";
    public const string Blocked = "Blocked";
}

public static class JourneyCircleTaxonomy
{
    public static readonly IReadOnlySet<string> Goals = Values(
        "Building an emergency fund", "Reducing debt", "Improving saving consistency", "Preparing to buy a home",
        "First-time homeownership", "Starting a business", "Growing a business", "Career transition",
        "New parenthood", "Blended family", "College planning", "Retirement preparation", "Recently retired",
        "Estate and legacy planning", "Faith-based stewardship", "Building healthy financial habits",
        "Developing accountability", "Personal growth", "Community service", "Entrepreneurship",
        "Leadership development", "Insurance education", "Protecting family income");

    public static readonly IReadOnlySet<string> Circles = Values(
        "Entrepreneurs Circle", "New Families Circle", "Debt Freedom Circle", "Homeownership Circle",
        "Retirement Readiness Circle", "Legacy Builders Circle", "Faith and Stewardship Circle",
        "Career Growth Circle", "Accountability Circle");

    public static readonly IReadOnlySet<string> LifeStages = Values(
        "Building foundations", "Career transition", "New parenthood", "Growing family", "Established family",
        "Business ownership", "Mid-career", "Empty nest", "Retirement preparation", "Recently retired");

    public static readonly IReadOnlySet<string> Locations = Values(
        "Southwest", "West", "Pacific Northwest", "Mountain region", "Midwest", "Northeast", "Southeast", "International");

    public static readonly IReadOnlySet<string> Interests = Values(
        "Personal finance", "Investing", "Small business", "Homeownership", "Family planning", "Parenting",
        "Career development", "Leadership", "Insurance education", "Retirement planning", "Estate planning",
        "Faith and stewardship", "Wellness", "Volunteering", "Community service", "Entrepreneurship",
        "Books and learning", "Technology", "Travel", "Fitness");

    public static readonly IReadOnlySet<string> ConnectionTypes = Values(
        "Accountability partner", "Learning partner", "Peer mentor", "Business peer", "Family-life peer",
        "Homeownership peer", "Retirement peer", "Faith and stewardship peer");

    public static readonly IReadOnlySet<string> CommunicationStyles = Values(
        "Text-first", "Email-first", "Scheduled check-ins", "Brief updates", "Detailed planning", "Encouragement-focused");

    public static readonly IReadOnlySet<string> AccountabilityFrequencies = Values(
        "Weekly", "Every two weeks", "Monthly", "Quarterly", "As needed");

    private static IReadOnlySet<string> Values(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

public sealed record JourneyCircleProfileInput(
    bool ConsentAffirmed,
    bool IsOptedIn,
    bool IsDiscoverable,
    bool AllowSuggestions,
    bool AllowConnectionRequests,
    string? Introduction,
    IReadOnlyList<string>? LifeStages,
    IReadOnlyList<string>? Locations,
    IReadOnlyList<string>? Goals,
    IReadOnlyList<string>? Interests,
    IReadOnlyList<string>? CircleCodes,
    IReadOnlyList<string>? ConnectionTypes,
    IReadOnlyList<string>? CommunicationStyles,
    IReadOnlyList<string>? AccountabilityFrequencies);

public sealed record JourneyCirclePublicProfile(
    Guid ClientProfileId,
    string DisplayName,
    string? Introduction,
    IReadOnlyList<string> LifeStages,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> CircleCodes,
    IReadOnlyList<string> ConnectionTypes,
    IReadOnlyList<string> CommunicationStyles,
    IReadOnlyList<string> AccountabilityFrequencies,
    string AvatarUrl);

public sealed record JourneyCircleRecommendation(JourneyCirclePublicProfile Profile, string Explanation);
public sealed record JourneyCircleConnectionSummary(Guid Id, JourneyCirclePublicProfile Profile, string Status, string? ConnectionReason, string? Introduction, DateTime CreatedUtc);
public sealed record JourneyCircleProfilePreferences(
    bool ConsentAffirmed,
    bool IsOptedIn,
    bool IsDiscoverable,
    bool AllowSuggestions,
    bool AllowConnectionRequests);
public sealed record JourneyCircleDashboard(
    JourneyCirclePublicProfile? Profile,
    JourneyCircleProfilePreferences? Preferences,
    IReadOnlyList<JourneyCircleRecommendation> Recommendations,
    IReadOnlyList<JourneyCircleConnectionSummary> Connections,
    IReadOnlyList<JourneyCircleConnectionSummary> Requests,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Circles,
    IReadOnlyList<string> LifeStages,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> ConnectionTypes,
    IReadOnlyList<string> CommunicationStyles,
    IReadOnlyList<string> AccountabilityFrequencies);
public sealed record JourneyCircleOperationResult(bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static JourneyCircleOperationResult Success() => new(true);
    public static JourneyCircleOperationResult Failure(string code, string message) => new(false, code, message);
}

public interface IJourneyCirclesService
{
    Task<JourneyCircleDashboard> GetDashboardAsync(string clientUserId, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> SaveProfileAsync(string clientUserId, JourneyCircleProfileInput input, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> RequestConnectionAsync(string clientUserId, Guid targetClientProfileId, string? reason, string? introduction, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> RespondToConnectionAsync(string clientUserId, Guid connectionId, bool accept, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> DisconnectAsync(string clientUserId, Guid connectionId, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> BlockAsync(string clientUserId, Guid targetClientProfileId, CancellationToken cancellationToken = default);
    Task<JourneyCircleOperationResult> ReportAsync(string clientUserId, Guid targetClientProfileId, string category, string? detail, CancellationToken cancellationToken = default);
    Task<bool> CanMessageAsync(string firstClientUserId, string secondClientUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string UserId, string DisplayName)>> ListConnectedPeersAsync(string clientUserId, CancellationToken cancellationToken = default);
}
