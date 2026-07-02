using System;
using System.Threading;
using System.Threading.Tasks;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IMetaAdsConnectionStore
{
    Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default);
    Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default);
    Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default);
}
