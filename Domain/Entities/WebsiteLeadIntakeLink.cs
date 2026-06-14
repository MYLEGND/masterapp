using System;

namespace Domain.Entities;

public class WebsiteLeadIntakeLink
{
    public Guid Id { get; set; }

    public long WebsiteLeadRowId { get; set; }
    public Guid WebsiteLeadPublicId { get; set; }

    public string WorkstationLeadId { get; set; } = "";
    public string AgentUserId { get; set; } = "";
    public string Bucket { get; set; } = "";

    public DateTime SubmittedUtc { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    public string? SourcePageKey { get; set; }
    public string? SourceCtaKey { get; set; }
    public string? PageVariant { get; set; }
    public string? PageMode { get; set; }
    public string? PagePath { get; set; }
    public string? LandingPageUrl { get; set; }
    public string? ReferrerUrl { get; set; }

    public string? InterestType { get; set; }
    public string? OfferKey { get; set; }
    public string? ProductType { get; set; }

    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? UtmTerm { get; set; }
    public string? UtmContent { get; set; }
    public string? Fbclid { get; set; }
    public string? Fbp { get; set; }
    public string? Fbc { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ClientUserAgent { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaAdId { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }

    public string? DiscoverySummaryJson { get; set; }
    public string? EstimateSummary { get; set; }
    public string? RecommendationPrimaryKey { get; set; }
    public string? RecommendationPrimaryTitle { get; set; }
    public string? RecommendationSecondaryKey { get; set; }
    public string? RecommendationSecondaryTitle { get; set; }
    public string? SnapshotJson { get; set; }
}
