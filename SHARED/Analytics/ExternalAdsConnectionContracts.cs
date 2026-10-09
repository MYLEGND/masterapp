namespace Shared.Analytics;

public static class MarketingProviderAuthorizationMethods
{
    public const string GoogleOAuthRefreshToken = "google_oauth_refresh";
    public const string TikTokOAuthAccessToken = "tiktok_oauth_access";
}

public sealed record MarketingProviderConnectionSnapshot(
    MarketingOwnerScope Owner,
    string Provider,
    bool Exists,
    bool Connected,
    bool Ready,
    bool RequiresAccountSelection,
    string? AccountId,
    string? AccountName,
    string? AuthorizationMethod,
    DateTime? ConnectedUtc,
    DateTime? LastVerifiedUtc,
    DateTime? CredentialExpiresUtc,
    Guid Revision,
    string? Status);

public sealed record MarketingProviderAccountOption(
    string AccountId,
    string Label);

public sealed record MarketingProviderOAuthCompletion(
    MarketingOwnerScope Owner,
    string Provider,
    string ReturnUrl,
    MarketingProviderConnectionSnapshot Connection,
    IReadOnlyList<MarketingProviderAccountOption> Accounts);

public sealed record MarketingProviderEventMapping(
    string CanonicalEventName,
    string ProviderEventName,
    string? DestinationId = null);

public sealed record MarketingProviderMeasurementConfiguration(
    MarketingOwnerScope Owner,
    string Provider,
    string? EventSourceId,
    string EventSourceType,
    IReadOnlyList<MarketingProviderEventMapping> Mappings,
    Guid Revision,
    bool MappingReady,
    string Status,
    bool HasMeasurementCredential = false);

public sealed record MarketingProviderMeasurementUpdate(
    string Provider,
    string? EventSourceId,
    string? EventSourceType,
    IReadOnlyList<MarketingProviderEventMapping> Mappings,
    Guid ExpectedRevision,
    string? MeasurementAccessToken = null);
