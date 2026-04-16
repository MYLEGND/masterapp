using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class KpiDetailTotalsDto
{
    public int Total { get; set; }
    public int PreviousTotal { get; set; }
    public int DeltaCount { get; set; }
    public decimal DeltaPct { get; set; }
    public decimal AvgPerDay { get; set; }
}

public sealed class KpiDetailBreakdownItemDto
{
    public string Label { get; set; } = "";
    public int Value { get; set; }
    public string? Meta { get; set; }
}

public sealed class KpiDetailBreakdownDto
{
    public List<KpiDetailBreakdownItemDto> TopPages { get; set; } = new();
    public List<KpiDetailBreakdownItemDto> TopSources { get; set; } = new();
    public List<KpiDetailBreakdownItemDto> TopCampaigns { get; set; } = new();
    public List<KpiDetailBreakdownItemDto> TopLandingPages { get; set; } = new();
    public List<KpiDetailBreakdownItemDto> RecentLeads { get; set; } = new();
}

public sealed class KpiDetailDto
{
    public string Metric { get; set; } = "";
    public string Label { get; set; } = "";
    public string StartDateLocal { get; set; } = "";
    public string EndDateLocal { get; set; } = "";
    public KpiDetailTotalsDto Totals { get; set; } = new();
    public List<TrendPointDto> Series { get; set; } = new();
    public KpiDetailBreakdownDto Breakdown { get; set; } = new();
}
