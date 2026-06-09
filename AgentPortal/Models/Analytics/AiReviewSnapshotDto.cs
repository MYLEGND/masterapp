using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class AiReviewSnapshotDto
{
    public string SnapshotText { get; set; } = "";
    public string GeneratedAtLocal { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public string RangeLabel { get; set; } = "";
    public string TrafficFilterLabel { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}
