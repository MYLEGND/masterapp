using System;

namespace Domain.Entities;

public class WebsiteLead
{
    public long Id { get; set; }
    public Guid LeadId { get; set; }
    public string FirstName { get; set; } = null!;
    public string? LastName { get; set; }
    public string Email { get; set; } = null!;
    public string? Phone { get; set; }
    public string? PreferredContactMethod { get; set; }
    public string? InterestType { get; set; }
    public string? Notes { get; set; }
    public string? SourcePageKey { get; set; }
    public string? SourceCtaKey { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaAdId { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public bool MarketingEmailConsent { get; set; }
    public bool CallTextConsent { get; set; }
    public bool TermsAccepted { get; set; }
    public bool IsInternal { get; set; }
    public string? Environment { get; set; }
    public string? Host { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Status { get; set; } = "New";
    public string? MetadataJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedByUserId { get; set; }
    public string? DeleteReason { get; set; }

    /// <summary>Facebook click ID (fbclid) captured at lead submission.</summary>
    public string? Fbclid { get; set; }
}
