using System.Text.Json;

namespace Shared.Analytics;

public static class OpenAiAdsAccountRoles
{
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Viewer = "viewer";

    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is Admin or Member or Viewer
            ? normalized
            : throw new ArgumentException("OpenAI Ads account role must be admin, member, or viewer.", nameof(value));
    }
}

public static class OpenAiAdsReviewStatuses
{
    public const string InReview = "in_review";
    public const string Rejected = "rejected";
    public const string Approved = "approved";

    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is InReview or Rejected or Approved
            ? normalized
            : throw new ArgumentException("OpenAI Ads review status must be in_review, rejected, or approved.", nameof(value));
    }
}

public static class OpenAiAdsAuthorizationMethods
{
    public const string InvitedOperator = "invited_operator";
    public const string ApiKey = "api_key";
    public const string ConnectedApp = "connected_app";

    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is InvitedOperator or ApiKey or ConnectedApp
            ? normalized
            : throw new ArgumentException("Unsupported OpenAI Ads authorization method.", nameof(value));
    }
}

/// <summary>
/// Provider-verified account evidence. Callers must construct this from a trusted provider
/// read; browser/user-pasted identifiers are not ownership evidence.
/// </summary>
public sealed record VerifiedOpenAiAdsAccount(
    string AccountId,
    string AccountName,
    string? Role,
    string ReviewStatus,
    string AuthorizationMethod,
    string? ProviderUserId = null,
    string? ProviderUserEmail = null,
    IReadOnlyCollection<string>? Permissions = null,
    string? PixelId = null,
    string? ConversionDataSourceId = null,
    DateTime? VerifiedUtc = null);

public sealed record OpenAiAdsConnectionSecrets(
    string? ManagementApiKey = null,
    string? ConversionsApiKey = null);

public sealed record OpenAiAdsConnectionSnapshot(
    MarketingOwnerScope Owner,
    bool Exists,
    bool Connected,
    Guid Revision,
    string? AccountId,
    string? AccountName,
    string? Role,
    string? ReviewStatus,
    string? AuthorizationMethod,
    string? ProviderUserId,
    string? ProviderUserEmail,
    IReadOnlyList<string> Permissions,
    string? PixelId,
    string? ConversionDataSourceId,
    bool HasManagementCredential,
    bool HasConversionsApiCredential,
    DateTime? ConnectedUtc,
    DateTime? DisconnectedUtc,
    DateTime? LastVerifiedUtc)
{
    public bool AccountApproved => string.Equals(ReviewStatus, OpenAiAdsReviewStatuses.Approved, StringComparison.Ordinal);
    public bool PixelConfigured => !string.IsNullOrWhiteSpace(PixelId);
    public bool ConversionsApiConfigured => HasConversionsApiCredential;
}
