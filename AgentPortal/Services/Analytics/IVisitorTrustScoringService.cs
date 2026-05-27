using AgentPortal.Models.Analytics;
using Domain.Entities;

namespace AgentPortal.Services.Analytics;

public interface IVisitorTrustScoringService
{
    VisitorTrustScoreDto Calculate(
        IReadOnlyCollection<AnalyticsEvent> events,
        IReadOnlyCollection<MetaSignalEvent> metaSignals);
}
