using System;
using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class MetaCampaignRow
{
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Objective { get; set; } = "";
    public DateTime? StartTimeUtc { get; set; }
    public DateTime? StopTimeUtc { get; set; }
    public DateTime? UpdatedTimeUtc { get; set; }

    public decimal Spend { get; set; }
    public long Impressions { get; set; }
    public long Reach { get; set; }
    public long Clicks { get; set; }
    public decimal Ctr { get; set; }
    public decimal Cpc { get; set; }
    public decimal Cpm { get; set; }
    public decimal Frequency { get; set; }
    public long Leads { get; set; }
    public long WebsiteLeads { get; set; }
    public long WebsiteLeadGap { get; set; }

    public long QualifiedLeads { get; set; }
    public long Appointments { get; set; }
    public long Applications { get; set; }
    public long PoliciesIssued { get; set; }
    public long PoliciesPaid { get; set; }
    public decimal PaidPremium { get; set; }
    public decimal PremiumRoas { get; set; }
}

public sealed class MetaCampaignsDto
{
    public string AccountId { get; set; } = "";
    public string? AccountName { get; set; }
    public string RangeLabel { get; set; } = "";
    public string? TimeZoneLabel { get; set; }
    public string? ComparisonNote { get; set; }
    public DateTime SyncedUtc { get; set; }
    public List<MetaCampaignRow> Rows { get; set; } = new();
}
