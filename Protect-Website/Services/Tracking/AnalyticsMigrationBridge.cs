using Domain.Entities;

namespace ProtectWebsite.Services.Tracking;

public static class AnalyticsMigrationBridge
{
    public static AnalyticsEvent FromLegacy(
        WebsiteLead lead,
        string eventType,
        string pageKey,
        string quoteType,
        object? metadata = null)
    {
        var ctx = UnifiedEventContextBuilder.Build(
            httpContext: null,
            sessionId: lead.SessionId,
            visitorId: lead.VisitorId,
            utmSource: lead.UtmSource,
            utmMedium: lead.UtmMedium,
            utmCampaign: lead.UtmCampaign,
            utmId: lead.UtmId,
            fbclid: lead.Fbclid,
            agentSlug: lead.AgentSlug,
            agentTrackingProfileId: lead.AgentTrackingProfileId,
            quoteType: quoteType
        );

        return UnifiedEventMapper.ToAnalytics(ctx);
    }
}
