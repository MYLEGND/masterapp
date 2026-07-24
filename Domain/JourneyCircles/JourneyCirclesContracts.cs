namespace Domain.JourneyCircles;

public static class JourneyCircleConnectionStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Disconnected = "Disconnected";
    public const string Blocked = "Blocked";
}

public static class JourneyCircleTaxonomy
{
    public static readonly IReadOnlySet<string> Goals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Building an emergency fund", "Reducing debt", "Improving saving consistency", "Preparing to buy a home",
        "First-time homeownership", "Starting a business", "Growing a business", "Career transition",
        "New parenthood", "Blended family", "College planning", "Retirement preparation", "Recently retired",
        "Estate and legacy planning", "Faith-based stewardship", "Building healthy financial habits",
        "Developing accountability", "Personal growth", "Community service", "Entrepreneurship",
        "Leadership development", "Insurance education", "Protecting family income"
    };

    public static readonly IReadOnlySet<string> Circles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Entrepreneurs Circle", "New Families Circle", "Debt Freedom Circle", "Homeownership Circle",
        "Retirement Readiness Circle", "Legacy Builders Circle", "Faith and Stewardship Circle",
        "Career Growth Circle", "Accountability Circle"
    };
}

public sealed record JourneyCircleProfileInput(
    bool ConsentAffirmed,
    bool IsOptedIn,
    bool IsDiscoverable,
    bool AllowSuggestions,
    bool AllowConnectionRequests,
    string? DisplayName,
    string? LifeStage,
    string? LocationLabel,
    string? Introduction,
    IReadOnlyList<string>? Goals,
    IReadOnlyList<string>? Interests,
    IReadOnlyList<string>? CircleCodes,
    IReadOnlyList<string>? ConnectionTypes,
    string? CommunicationStyle,
    string? AccountabilityFrequency);

public sealed record JourneyCirclePublicProfile(
    Guid ClientProfileId,
    string DisplayName,
    string? LifeStage,
    string? LocationLabel,
    string? Introduction,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> CircleCodes,
    IReadOnlyList<string> ConnectionTypes,
    string? CommunicationStyle,
    string? AccountabilityFrequency,
    string AvatarUrl);

public sealed record JourneyCircleRecommendation(JourneyCirclePublicProfile Profile, string Explanation);
public sealed record JourneyCircleConnectionSummary(Guid Id, JourneyCirclePublicProfile Profile, string Status, string? ConnectionReason, string? Introduction, DateTime CreatedUtc);
public sealed record JourneyCircleDashboard(
    JourneyCirclePublicProfile? Profile,
    IReadOnlyList<JourneyCircleRecommendation> Recommendations,
    IReadOnlyList<JourneyCircleConnectionSummary> Connections,
    IReadOnlyList<JourneyCircleConnectionSummary> Requests,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Circles);
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
