using System;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IMetaAdsConnectionStore
{
    Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default);
    Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default);
    Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default);
}
