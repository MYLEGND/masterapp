using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IMetaAdsService
{
    Task<MetaCampaignsDto> GetCampaignsAsync(TimeRangeRequest range, ScopeContext scope, CancellationToken ct = default);
}
