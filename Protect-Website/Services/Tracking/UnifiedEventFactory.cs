using Domain.Entities;

namespace ProtectWebsite.Services.Tracking;

public static class UnifiedEventFactory
{
    public static UnifiedEventEnvelope Create(
        WebsiteLead lead,
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        string eventType,
        string pageKey,
        string quoteType,
        object? metadata = null)
    {
        var context = UnifiedEventContextBuilder.Build(
            httpContext: httpContext,
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

        return new UnifiedEventEnvelope
        {
            Context = context,
            EventType = eventType,
            PageKey = pageKey,
            QuoteType = quoteType,
            Metadata = metadata
        };
    }
}

public class UnifiedEventEnvelope
{
    public UnifiedEventContext Context { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public string PageKey { get; set; } = default!;
    public string QuoteType { get; set; } = default!;
    public object? Metadata { get; set; }
}
