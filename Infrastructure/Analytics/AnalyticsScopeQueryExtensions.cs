using Domain.Entities;
using Infrastructure.Data;
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
            // canonical UPN authority. A requested site dimension narrows the same
            // Founder dataset without changing owner/CRM authority.
            query = query.Where(x => x.CommerceBusinessId == null);
            var founderSite = string.IsNullOrWhiteSpace(scope.SiteKey) ? null : scope.SiteKey.Trim();
            return founderSite is null
                ? query
                : query.Where(x => x.MetadataJson != null && MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == founderSite);
        }
        if (scope.CommerceBusinessId.HasValue || !Enum.IsDefined(scope.ScopeType) ||
            (scope.ScopeType == ScopeType.Agent && (!scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)))
            return query.Where(x => false);
        if (scope.ScopeType == ScopeType.Agent)
            query = query.Where(x => x.CommerceBusinessId == null);

        var site = string.IsNullOrWhiteSpace(scope.SiteKey) ? null : scope.SiteKey.Trim();
        var owner = string.IsNullOrWhiteSpace(scope.ReportingOwner) ? null : scope.ReportingOwner.Trim();

        if (site is null && owner is null)
            return query;

        if (site is not null && owner is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                (MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == site || MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.reportingOwner") == owner));
        }

        if (site is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == site);
        }

        return query.Where(x =>
            x.MetadataJson != null &&
            MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.reportingOwner") == owner);
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
            // canonical UPN authority. A requested site dimension narrows the same
            // Founder dataset without changing owner/CRM authority.
            query = query.Where(x => x.CommerceBusinessId == null);
            var founderSite = string.IsNullOrWhiteSpace(scope.SiteKey) ? null : scope.SiteKey.Trim();
            return founderSite is null
                ? query
                : query.Where(x => x.MetadataJson != null && MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == founderSite);
        }
        if (scope.CommerceBusinessId.HasValue || !Enum.IsDefined(scope.ScopeType) ||
            (scope.ScopeType == ScopeType.Agent && (!scope.AgentTrackingProfileId.HasValue || scope.AgentTrackingProfileId == Guid.Empty)))
            return query.Where(x => false);
        if (scope.ScopeType == ScopeType.Agent)
            query = query.Where(x => x.CommerceBusinessId == null);

        var site = string.IsNullOrWhiteSpace(scope.SiteKey) ? null : scope.SiteKey.Trim();
        var owner = string.IsNullOrWhiteSpace(scope.ReportingOwner) ? null : scope.ReportingOwner.Trim();

        if (site is null && owner is null)
            return query;

        if (site is not null && owner is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                (MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == site || MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.reportingOwner") == owner));
        }

        if (site is not null)
        {
            return query.Where(x =>
                x.MetadataJson != null &&
                MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.siteKey") == site);
        }

        return query.Where(x =>
            x.MetadataJson != null &&
            MasterAppDbContext.JsonValue(MasterAppDbContext.IsJson(x.MetadataJson) == 1 ? x.MetadataJson : "{}", "$.reportingOwner") == owner);
    }
}
