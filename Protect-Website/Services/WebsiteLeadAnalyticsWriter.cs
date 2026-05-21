using System;
using System.Text.Json;
using Domain.Entities;

namespace ProtectWebsite.Services;

public static class WebsiteLeadAnalyticsWriter
{
    public static AnalyticsEvent CreateEvent(
        WebsiteLead lead,
        string eventType,
        string pageKey,
        string quoteType,
        object? metadata = null,
        DateTime? eventUtc = null)
    {
        var effectiveEventUtc = eventUtc ?? DateTime.UtcNow;

        return new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            PageKey = pageKey,
            FormKey = pageKey + "_form",
            QuoteType = quoteType,
            SessionId = lead.SessionId,
            VisitorId = lead.VisitorId,
            UtmSource = lead.UtmSource,
            UtmMedium = lead.UtmMedium,
            UtmCampaign = lead.UtmCampaign,
            UtmId = lead.UtmId,
            Fbclid = lead.Fbclid,
            MetaCampaignId = lead.MetaCampaignId,
            MetaAdSetId = lead.MetaAdSetId,
            MetaAdId = lead.MetaAdId,
            AgentTrackingProfileId = lead.AgentTrackingProfileId,
            AgentSlug = lead.AgentSlug,
            Environment = lead.Environment,
            Host = lead.Host,
            EventUtc = effectiveEventUtc,
            ReceivedUtc = DateTime.UtcNow,
            MetadataJson = metadata == null ? null : JsonSerializer.Serialize(metadata)
        };
    }
}
