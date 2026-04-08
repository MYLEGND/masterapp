using System;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IMetaAdsOAuthService
{
    string BuildConnectUrl(Guid agentTrackingProfileId, string? returnUrl, string? explicitRedirectUri = null);
    Task<MetaAdsConnectionRecord> CompleteCallbackAsync(string code, string stateToken, CancellationToken ct = default);
}
