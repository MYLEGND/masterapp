using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>
/// Canonical SQL-backed Meta Ads connection adapter for hosts that do not need
/// legacy app-specific cache migration. AgentPortal may override this registration
/// with its compatibility adapter while preserving the same MarketingConnectionStore.
/// </summary>
public sealed class CanonicalMetaAdsConnectionStore(MarketingConnectionStore connections) : IMetaAdsConnectionStore
{
    public Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default) =>
        connections.GetAdsAsync(MarketingOwnerScope.Agent(agentTrackingProfileId), ct);

    public Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default) =>
        connections.SaveAdsAsync(MarketingOwnerScope.Agent(record.AgentTrackingProfileId), record, ct);

    public Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default) =>
        connections.DisconnectAsync(MarketingOwnerScope.Agent(agentTrackingProfileId), ct);
}
