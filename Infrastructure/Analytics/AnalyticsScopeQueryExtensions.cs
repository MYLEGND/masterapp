using AgentPortal.Models.Analytics;
using Domain.Entities;

namespace AgentPortal.Services.Analytics;

internal static class AnalyticsScopeQueryExtensions
{
    public static IQueryable<AnalyticsEvent> ApplySiteScope(
        this IQueryable<AnalyticsEvent> query,
        ScopeContext scope)
    {
        var siteMarker = BuildJsonMarker("siteKey", scope.SiteKey);
        var ownerMarker = BuildJsonMarker("reportingOwner", scope.ReportingOwner);

        if (siteMarker is null && ownerMarker is null)
            return query;

        if (siteMarker is not null && ownerMarker is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                (x.MetadataJson.Contains(siteMarker) || x.MetadataJson.Contains(ownerMarker)));
        }

        if (siteMarker is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                x.MetadataJson.Contains(siteMarker));
        }

        return query.Where(x =>
            x.MetadataJson != null &&
            x.MetadataJson.Contains(ownerMarker!));
    }

    public static IQueryable<MetaSignalEvent> ApplySiteScope(
        this IQueryable<MetaSignalEvent> query,
        ScopeContext scope)
    {
        var siteMarker = BuildJsonMarker("siteKey", scope.SiteKey);
        var ownerMarker = BuildJsonMarker("reportingOwner", scope.ReportingOwner);

        if (siteMarker is null && ownerMarker is null)
            return query;

        if (siteMarker is not null && ownerMarker is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                (x.MetadataJson.Contains(siteMarker) || x.MetadataJson.Contains(ownerMarker)));
        }

        if (siteMarker is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                x.MetadataJson.Contains(siteMarker));
        }

        return query.Where(x =>
            x.MetadataJson != null &&
            x.MetadataJson.Contains(ownerMarker!));
    }

    private static string? BuildJsonMarker(string propertyName, string? propertyValue)
    {
        if (string.IsNullOrWhiteSpace(propertyName) || string.IsNullOrWhiteSpace(propertyValue))
            return null;

        return $"\"{propertyName}\":\"{propertyValue.Trim()}\"";
    }
}
