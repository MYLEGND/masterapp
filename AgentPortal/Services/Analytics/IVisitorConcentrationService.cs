using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IVisitorConcentrationService
{
    Task<List<VisitorConcentrationDto>> GetVisitorConcentrationAsync(
        TimeRangeRequest range,
        CancellationToken ct = default);

    Task<VisitorConcentrationPayload> GetVisitorConcentrationPayloadAsync(
        DateTime fromUtc,
        DateTime toUtc,
        TimeZoneInfo viewerTimeZone,
        Guid? agentProfileId,
        TrafficType trafficType,
        CancellationToken ct = default);
}
