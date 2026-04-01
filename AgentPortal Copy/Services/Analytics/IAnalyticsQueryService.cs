using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IAnalyticsQueryService
{
    Task<SummaryKpiDto> GetSummaryAsync(TimeRangeRequest range, ScopeContext scope);
    Task<TrafficOverviewDto> GetTrafficAsync(TimeRangeRequest range, ScopeContext scope);
    Task<PagePerformanceDto> GetPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope);
    Task<CtaPerformanceDto> GetCtaPerformanceAsync(TimeRangeRequest range, ScopeContext scope);
    Task<QuoteFunnelDto> GetQuoteFunnelAsync(TimeRangeRequest range, ScopeContext scope);
    Task<ConversionCenterDto> GetConversionsAsync(TimeRangeRequest range, ScopeContext scope);
    Task<LeadSnapshotDto> GetLeadsAsync(TimeRangeRequest range, ScopeContext scope, int take = 200);
    Task<AgentPerformanceDto> GetAgentPerformanceAsync(TimeRangeRequest range, ScopeContext scope, AnalyticsQueryOptions? options = null);
}
