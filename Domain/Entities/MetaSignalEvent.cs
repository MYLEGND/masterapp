using System;

namespace Domain.Entities;

public class MetaSignalEvent
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string? EventCategory { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public Guid? LeadId { get; set; }
    public string? QuoteType { get; set; }
    public string? PageKey { get; set; }
    public string? EffectivePageKey { get; set; }
    public string? PageVariant { get; set; }
    public string? PageMode { get; set; }
    public string? TrafficType { get; set; }
    public int? FunnelStep { get; set; }
    public string? StepName { get; set; }
    public int IntentScore { get; set; }
    public int EngagementScore { get; set; }
    public int QualificationScore { get; set; }
    public int FrictionScore { get; set; }
    public int TotalSignalScore { get; set; }
    public string? ScoreTier { get; set; }
    public bool MetaBrowserSent { get; set; }
    public bool MetaServerSent { get; set; }
    public string? MetaDeduplicationKey { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? UtmContent { get; set; }
    public bool FbclidPresent { get; set; }
    public bool FbcPresent { get; set; }
    public bool FbpPresent { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgentHash { get; set; }
    public string? IpHash { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public string? Environment { get; set; }
    public string? Host { get; set; }
    public string? MetadataJson { get; set; }
}
