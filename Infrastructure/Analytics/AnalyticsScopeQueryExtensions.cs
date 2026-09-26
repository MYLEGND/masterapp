using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

internal static class AnalyticsScopeQueryExtensions
{
    public static IQueryable<AnalyticsEvent> ApplySiteScope(
        this IQueryable<AnalyticsEvent> query,
        ScopeContext scope)
    {
        if (scope.ScopeType == ScopeType.Business)
            return scope.CommerceBusinessId is { } businessId && businessId != Guid.Empty && !scope.AgentTrackingProfileId.HasValue
                ? query.Where(x => x.CommerceBusinessId == businessId && x.AgentTrackingProfileId == null)
                : query.Where(x => false);
        if (scope.ScopeType == ScopeType.Founder)
        {
            if (scope.CommerceBusinessId.HasValue || !scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)
                return query.Where(x => false);

            // Founder profile aliases are resolved by the query service using the
            // canonical UPN authority. Keep this first-stage filter tenant-safe
            // without prematurely excluding a historical Founder profile row.
            return query.Where(x => x.CommerceBusinessId == null);
        }
        if (scope.CommerceBusinessId.HasValue || !Enum.IsDefined(scope.ScopeType) ||
            (scope.ScopeType == ScopeType.Agent && (!scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)))
            return query.Where(x => false);
        if (scope.ScopeType == ScopeType.Agent)
            query = query.Where(x => x.CommerceBusinessId == null);

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
        if (scope.ScopeType == ScopeType.Business)
            return scope.CommerceBusinessId is { } businessId && businessId != Guid.Empty && !scope.AgentTrackingProfileId.HasValue
                ? query.Where(x => x.CommerceBusinessId == businessId && x.AgentTrackingProfileId == null)
                : query.Where(x => false);
        if (scope.ScopeType == ScopeType.Founder)
        {
            if (scope.CommerceBusinessId.HasValue || !scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)
                return query.Where(x => false);

            // Founder profile aliases are resolved by the query service using the
            // canonical UPN authority. Keep this first-stage filter tenant-safe
            // without prematurely excluding a historical Founder profile row.
            return query.Where(x => x.CommerceBusinessId == null);
        }
        if (scope.CommerceBusinessId.HasValue || !Enum.IsDefined(scope.ScopeType) ||
            (scope.ScopeType == ScopeType.Agent && (!scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)))
            return query.Where(x => false);
        if (scope.ScopeType == ScopeType.Agent)
            query = query.Where(x => x.CommerceBusinessId == null);

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
