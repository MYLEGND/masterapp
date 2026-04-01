using System;
using System.Collections.Generic;
using Domain.Entities;

namespace AgentPortal.Models;

public class WebsiteAnalyticsViewModel
{
    public int TotalPageViews { get; set; }
    public int UniqueVisitors { get; set; }
    public int Sessions { get; set; }
    public int VerifiedLeads { get; set; }

    public IReadOnlyList<CountItem> VisitsByPage { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem2> CtaClicks { get; set; } = Array.Empty<CountItem2>();
    public IReadOnlyList<CountItem> QuoteStarts { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> QuoteFormStarts { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> QuoteFormSubmits { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> RiskStarts { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> RiskSubmits { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> BookCallClicks { get; set; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> TopConversions { get; set; } = Array.Empty<CountItem>();

    public IReadOnlyList<AnalyticsEvent> RecentSubmissions { get; set; } = Array.Empty<AnalyticsEvent>();
    public IReadOnlyList<WebsiteLead> RecentLeads { get; set; } = Array.Empty<WebsiteLead>();
    public IReadOnlyList<WebsiteLead> LeadsForModal { get; set; } = Array.Empty<WebsiteLead>();
}

public record CountItem(string Key, int Count);
public record CountItem2(string Key1, string Key2, int Count);
