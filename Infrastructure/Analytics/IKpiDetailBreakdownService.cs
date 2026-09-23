using Shared.Analytics;
using Infrastructure.Analytics;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IKpiDetailBreakdownService
{
    KpiDetailBreakdownDto BuildLeadBreakdown(LeadSnapshotDto leads);
}
