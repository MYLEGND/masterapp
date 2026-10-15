using Infrastructure.Analytics;
using Shared.Analytics;

namespace AgentPortal.Services.Analytics;

/// <summary>Portal routing adapter; shared OAuth owns state, protocol and token exchange.</summary>
public sealed class MetaAdsOAuthService(MarketingMetaAdsOAuthService oauth, IConfiguration config) : IMetaAdsOAuthService
{
    public string BuildConnectUrl(MarketingOwnerScope owner, string? returnUrl, string? explicitRedirectUri = null) =>
        oauth.BuildConnectUrl(owner, returnUrl ?? "/WebsiteAnalytics/Index",
            explicitRedirectUri ?? config["MetaAds:RedirectUri"] ?? string.Empty);

    public MarketingMetaOAuthState InspectState(string stateToken) => oauth.InspectState(stateToken);

    public Task<MarketingMetaOAuthResult> CompleteCallbackAsync(string code, string stateToken, CancellationToken ct = default) =>
        oauth.CompleteCallbackAsync(code, stateToken, ct);
}
