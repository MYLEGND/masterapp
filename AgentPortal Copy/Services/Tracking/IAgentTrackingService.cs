using Domain.Entities;

namespace AgentPortal.Services.Tracking;

public interface IAgentTrackingService
{
    Task<AgentTrackingProfile?> GetByUserIdAsync(string agentUserId, CancellationToken ct = default);
    Task<AgentTrackingProfile?> GetByUpnAsync(string agentUpn, CancellationToken ct = default);
    Task<AgentTrackingProfile> EnsureProfileAsync(string agentUserId, string agentUpn, string? displayName = null, CancellationToken ct = default);
    Task<AgentUrlInfo> GetPersonalUrlsAsync(AgentTrackingProfile profile, CancellationToken ct = default);
    Task<AgentTrackingBackfillResult> BackfillAsync(bool dryRun = false, CancellationToken ct = default);
    Task<List<AgentTrackingProfile>> GetAllProfilesAsync(CancellationToken ct = default);
}

public sealed record AgentUrlInfo(string PrimaryUrl, string? AlternateSlugUrl = null, string? CampaignBaseUrl = null, string? ShortUrl = null);
