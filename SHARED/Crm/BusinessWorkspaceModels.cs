using Shared.Analytics;
using System.ComponentModel.DataAnnotations;

namespace Shared.Crm;

public sealed class BusinessWorkspaceModel
{
    public Guid BusinessId { get; set; }
    public string BusinessName { get; set; } = string.Empty;
    public string Tab { get; set; } = "crm";
    public string Kind { get; set; } = "Lead";
    public string Search { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int Total { get; set; }
    public int Days { get; set; } = 30;
    public BusinessWorkspacePreferences Preferences { get; set; } = new();
    public Guid SettingsRevision { get; set; }
    public List<BusinessIntakeRecipient> Recipients { get; set; } = [];
    public bool CanCustomize { get; set; }
    public List<AgentPortal.Models.ClientListItemViewModel> CanonicalContacts { get; set; } = new();
    public List<BusinessCrmContact> Contacts { get; set; } = new();
    public BusinessCrmContact? Selected { get; set; }
    public SummaryKpiDto? Summary { get; set; }
    public MarketingHealthDto? Health { get; set; }
    public List<BusinessWebsiteEventMapRow> EventMap { get; set; } = new();
    public List<BusinessWebsiteEventRow> RecentEvents { get; set; } = new();
}

public sealed record BusinessWebsiteEventMapRow(string Page, string Element, string Trigger, string Event,
    string Mode, bool Published, long Revision);
public sealed record BusinessWebsiteEventRow(DateTime OccurredUtc, string Event, string? Page, bool ServerSent);

public sealed class BusinessCrmContact
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Kind { get; set; } = "Lead";
    public string Stage { get; set; } = "NewLead";
    public string Notes { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<ClientCrmActivity> History { get; set; } = new();
    public List<BusinessCrmSource> Sources { get; set; } = new();
    public List<BusinessCrmInquiry> Inquiries { get; set; } = new();
}

public sealed record BusinessCrmInquiry(DateTime CreatedUtc, string Message, string SourcePath, string NotificationStatus);

public sealed record BusinessCrmSource(DateTime SubmittedUtc, string? Page, string? Campaign);
public sealed class BusinessCrmEdit
{
    [MaxLength(64)] public string Revision { get; set; } = string.Empty;
    public DateTime ExpectedUpdatedUtc { get; set; }
    [Required, MaxLength(20)] public string Kind { get; set; } = "Lead";
    [Required, MaxLength(80)] public string Stage { get; set; } = "NewLead";
    [MaxLength(12000)] public string? Notes { get; set; }
}

public sealed record BusinessWorkspaceNavigationItem(Guid BusinessId, string BusinessName, string LeadLabel,
    string ClientLabel, bool CanCrm, bool CanAnalytics, bool CanCustomize);
