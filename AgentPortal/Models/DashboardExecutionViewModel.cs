using Domain.Entities;

namespace AgentPortal.Models;

public class DashboardExecutionViewModel
{
    public IReadOnlyList<ActionItem> Today { get; set; } = Array.Empty<ActionItem>();
    public IReadOnlyList<ActionItem> Overdue { get; set; } = Array.Empty<ActionItem>();
    public IReadOnlyList<Blocker> Blockers { get; set; } = Array.Empty<Blocker>();
    public AgentPortal.Services.Analytics.DerivedAnalyticsSnapshot? DerivedAnalytics { get; set; }
}
