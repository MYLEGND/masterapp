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
