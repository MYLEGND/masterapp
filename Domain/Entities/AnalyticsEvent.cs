using System;

namespace Domain.Entities;

public class AnalyticsEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid? ClientEventId { get; set; }
    public string EventType { get; set; } = null!;
    public string? PageKey { get; set; }
    public string? SectionKey { get; set; }
    public string? ElementKey { get; set; }
    public string? ButtonLabel { get; set; }
    public string? FormKey { get; set; }
    public string? QuoteType { get; set; }
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public bool IsInternal { get; set; }
    public string? Environment { get; set; }
    public string? Host { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public DateTime EventUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public string? SubmitOutcome { get; set; }
    public string? MetadataJson { get; set; }
}
