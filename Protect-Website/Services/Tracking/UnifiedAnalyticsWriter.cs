using Domain.Entities;
using Infrastructure.Data;

namespace ProtectWebsite.Services.Tracking;

public static class UnifiedAnalyticsWriter
{
    public const string PipelineStamp = "unified_event_mapper_v1";

    public static void Write(MasterAppDbContext db, AnalyticsEvent analyticsEvent)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(analyticsEvent);

        if (!string.Equals(analyticsEvent.PipelineStamp, PipelineStamp, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AnalyticsEvent must be created through BuildTrackingContext -> UnifiedEventMapper.ToAnalytics before it can be written.");
        }

        db.AnalyticsEvents.Add(analyticsEvent);
    }
}
