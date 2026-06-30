namespace AgentPortal.Models.Analytics;

public sealed class AnalyticsQueryOptions
{
    public string OrderBy { get; set; } = "leads"; // leads|conversions|session|intent
    public bool Desc { get; set; } = true;
    public int? Take { get; set; } = 50;
    public int? Skip { get; set; } = 0;
}
