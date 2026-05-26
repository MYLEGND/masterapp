using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public sealed class KpiDetailBreakdownService : IKpiDetailBreakdownService
{
    public KpiDetailBreakdownDto BuildLeadBreakdown(LeadSnapshotDto leads)
    {
        var breakdown = new KpiDetailBreakdownDto();

        breakdown.TopSources = leads.Leads
            .Where(l => !string.IsNullOrWhiteSpace(l.ResolvedSource))
            .GroupBy(l => l.ResolvedSource!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
            .ToList();

        breakdown.TopCampaigns = leads.Leads
            .Where(l => !string.IsNullOrWhiteSpace(l.ResolvedCampaign))
            .GroupBy(l => l.ResolvedCampaign!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
            .ToList();

        breakdown.TopPages = leads.Leads
            .Where(l => !string.IsNullOrWhiteSpace(l.SourcePage))
            .GroupBy(l => l.SourcePage!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
            .ToList();

        breakdown.RecentLeads = leads.Leads.Take(15)
            .Select(l => new KpiDetailBreakdownItemDto
            {
                Label = string.IsNullOrWhiteSpace(l.Name) ? l.Email : l.Name,
                Value = 1,
                Meta = l.ResolvedSource ?? "Direct"
            })
            .ToList();

        return breakdown;
    }
}
