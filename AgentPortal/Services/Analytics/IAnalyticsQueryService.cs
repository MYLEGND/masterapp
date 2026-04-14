using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IAnalyticsQueryService
{
    Task<SummaryKpiDto> GetSummaryAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<TrafficOverviewDto> GetTrafficAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<PagePerformanceDto> GetPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<CtaPerformanceDto> GetCtaPerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<QuoteFunnelDto> GetQuoteFunnelAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<ConversionCenterDto> GetConversionsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<LeadSnapshotDto> GetLeadsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All, int take = 200);
    Task<AgentPerformanceDto> GetAgentPerformanceAsync(TimeRangeRequest range, ScopeContext scope, AnalyticsQueryOptions? options = null);
    // ── Behavior Intelligence Engine ─────────────────────────────────────────
    Task<EngagementSummaryDto> GetEngagementSummaryAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<PageEngagementDto> GetPageEngagementAsync(TimeRangeRequest range, ScopeContext scope);
    Task<TimeOnPageDto> GetTimeOnPageAsync(TimeRangeRequest range, ScopeContext scope);
    Task<ExitAnalysisDto> GetExitAnalysisAsync(TimeRangeRequest range, ScopeContext scope);
    Task<ScrollAnalysisDto> GetScrollAnalysisAsync(TimeRangeRequest range, ScopeContext scope);
    Task<JourneyAnalysisDto> GetJourneyAnalysisAsync(TimeRangeRequest range, ScopeContext scope);
    Task<SourcePerformanceDto> GetSourcePerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
    Task<LandingPagePerformanceDto> GetLandingPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope);
    Task<FormFrictionDto> GetFormFrictionAsync(TimeRangeRequest range, ScopeContext scope);
    Task<FormAbandonmentDto> GetFormAbandonmentAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All);
}
