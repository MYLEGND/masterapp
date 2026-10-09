using Infrastructure.Analytics;
using Shared.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IMetaAdsOAuthService
{
    string BuildConnectUrl(MarketingOwnerScope owner, string? returnUrl, string? explicitRedirectUri = null);
    MarketingMetaOAuthState InspectState(string stateToken);
    Task<MarketingMetaOAuthResult> CompleteCallbackAsync(string code, string stateToken, CancellationToken ct = default);
}
