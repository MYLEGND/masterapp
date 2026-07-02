using System.Threading;
using System.Threading.Tasks;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IMetaAdsService
{
    Task<MetaCampaignsDto> GetCampaignsAsync(TimeRangeRequest range, ScopeContext scope, CancellationToken ct = default);
}
