using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IVisitorConcentrationService
{
    Task<List<VisitorConcentrationDto>> GetVisitorConcentrationAsync(
        TimeRangeRequest range,
        CancellationToken ct = default);

    Task<VisitorConcentrationPayload> GetVisitorConcentrationPayloadAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        CancellationToken ct = default);
}
