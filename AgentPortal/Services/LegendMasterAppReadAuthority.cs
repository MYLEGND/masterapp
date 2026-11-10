using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using AgentPortal.Security;
using AgentPortal.Services.Tracking;
using Infrastructure.Analytics;
using Shared.Analytics;
using Shared.Diagnostics;
using Shared.Auth;

namespace AgentPortal.Services;

internal sealed record LegendMasterAppReadRequest(
    string? Preset,
    string? TimeZoneId,
    int? TimeZoneOffsetMinutes,
    string? AssistantWorkspaceKey = null, string? BusinessGoal = null);

internal interface ILegendMasterAppReadProjection
{
    string Key { get; }
    string Application { get; }
    IReadOnlyList<string> Routes { get; }
    string Description { get; }
    Task<object> ReadAsync(
        ClaimsPrincipal founder,
        LegendMasterAppReadRequest request,
        CancellationToken cancellationToken);
}

internal sealed class LegendMasterAppReadAuthority(
    IEnumerable<ILegendMasterAppReadProjection> projections,
    IEnumerable<EndpointDataSource> endpointDataSources)
{
    private readonly IReadOnlyDictionary<string, ILegendMasterAppReadProjection> _projections =
        projections.ToDictionary(value => value.Key, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<object> _runtimeRoutes = endpointDataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(endpoint => new
        {
            route = endpoint.RoutePattern.RawText,
            methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                ?.Where(method => method is "GET" or "HEAD")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(method => method, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>(),
            displayName = endpoint.DisplayName
        })
        .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.route) && endpoint.methods.Length > 0)
        .GroupBy(endpoint => endpoint.route!, StringComparer.OrdinalIgnoreCase)
        .Select(group => new
        {
            route = group.Key,
            methods = group.SelectMany(item => item.methods)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(method => method, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            displayName = group.Select(item => item.displayName)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            access = "runtime_route_metadata_only"
        })
        .OrderBy(item => item.route, StringComparer.OrdinalIgnoreCase)
        .Take(500)
        .Select(item => (object)item)
        .ToArray();

    internal object Catalog() => new
    {
        schemaVersion = 1,
        authority = nameof(LegendMasterAppReadAuthority),
        sourceOfTruth = "canonical_page_service_projections",
        providerSpecificRegistry = false,
        structuralPageAuthority = new
        {
            tool = LegendSiteToolDisclosureAuthority.CurrentPageToolName,
            sharedBridge = "/api/legend-site-tools",
            inventory = LegendSiteToolDisclosureAuthority.SystemInventory(),
            runtimeRoutes = _runtimeRoutes,
            coverage = "registered_masterapp_surfaces_plus_live_agent_portal_route_metadata"
        },
        projections = _projections.Values
            .OrderBy(value => value.Application, StringComparer.Ordinal)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new
            {
                key = value.Key,
                application = value.Application,
                routes = value.Routes,
                description = value.Description,
                access = "founder_governed_privacy_safe_read"
            })
            .ToArray(),
        privacy = new
        {
            rawSql = false,
            credentials = false,
            authenticationMaterial = false,
            rawRequestBodies = false,
            rawResponseBodies = false,
            privateCustomerRecords = false
        }
    };

    internal async Task<object> ReadAsync(
        ClaimsPrincipal founder,
        string surface,
        LegendMasterAppReadRequest request,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        if (!_projections.TryGetValue(surface, out var projection))
            return new
            {
                ok = false,
                error = "masterapp_read_projection_not_found",
                requestedSurface = surface,
                catalog = Catalog()
            };

        return await projection.ReadAsync(founder, request, cancellationToken);
    }
}

internal sealed class WebsiteAnalyticsLegendReadProjection(
    WebsiteAnalyticsAiDataBuilder dataBuilder,
    IAgentTrackingService tracking,
    Infrastructure.Data.MasterAppDbContext db,
    IMarketingManagerService marketing, MarketingConnectionStore connections,
    Infrastructure.Reception.ReceptionistOperatorService receptionist) : ILegendMasterAppReadProjection
{
    public string Key => "agent-portal.website-analytics";
    public string Application => "AgentPortal";
    public IReadOnlyList<string> Routes => ["/WebsiteAnalytics", "/website-analytics"];
    public string Description =>
        "Privacy-safe Website Analytics operational performance using the same canonical AI review projection as the page.";

    public async Task<object> ReadAsync(
        ClaimsPrincipal founder,
        LegendMasterAppReadRequest request,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        MarketingOwnerScope owner;
        ScopeContext scope;
        if (request.AssistantWorkspaceKey is { } workspace)
        {
            var actor = founder.GetCanonicalUserId();
            if (actor is null || !await Infrastructure.Businesses.BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, actor, workspace, cancellationToken))
                throw new ForbidResultException();
            Shared.Workspaces.WorkspaceKey.TryParse(workspace, out var key);
            owner = key!.Kind == "business" ? MarketingOwnerScope.Business(key.OwnerId!.Value) : MarketingOwnerScope.Agent(key.OwnerId!.Value);
            scope = key.Kind == "business" ? ScopeContext.ForBusiness(key.OwnerId.Value) : ScopeContext.ForAgent(key.OwnerId.Value);
        }
        else
        {
            var profile = await tracking.GetByUserIdAsync(FounderGuard.FounderOid, cancellationToken)
                ?? throw new InvalidOperationException("Founder analytics scope is unavailable.");
            owner = MarketingOwnerScope.Founder;
            scope = ScopeContext.ForFounder(profile.Id);
        }

        var normalizedPreset = request.Preset?.Trim().ToLowerInvariant();
        if (normalizedPreset is not ("today" or "7d" or "30d" or "month" or "year"))
            normalizedPreset = "7d";

        var viewerTimeZone = AnalyticsViewerTimeZoneResolver.Resolve(
            request.TimeZoneId,
            request.TimeZoneOffsetMinutes);
        var range = TimeRangeRequest.FromPreset(normalizedPreset, viewerTz: viewerTimeZone);
        var plan = request.AssistantWorkspaceKey is null ? null : await marketing.PlanAsync(owner, scope, range,
            new MarketingManagerGoalRequest(request.BusinessGoal ?? "Assess the sales funnel and recommend reviewable next actions."), cancellationToken);
        var payload = plan?.AnalyticsContext ?? await dataBuilder.BuildAsync(
            range, scope, range.Label, "Founder Personal", "All Traffic", TrafficType.All, cancellationToken, owner);
        object? businessProfile = null;
        object? publishedWebsite = null;
        object? receptionistStatus = null;
        if (request.AssistantWorkspaceKey is { } selected)
        {
            var actor = founder.GetCanonicalUserId();
            if (owner.CommerceBusinessId is { } businessId)
            {
                var business = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                    db.CommerceBusinesses.Where(x => x.Id == businessId), cancellationToken);
                businessProfile = new { source = "CommerceBusiness", business.Id, business.DisplayName, business.BusinessType, business.PrimaryDomain, business.UpdatedUtc };
                if (await Infrastructure.Businesses.BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, actor, selected, cancellationToken, "website"))
                {
                    var published = await Infrastructure.WebsiteEditing.WebsiteContentStore.PublishedBusinessAsync(db, businessId, cancellationToken);
                    publishedWebsite = published is null ? (object)new { status = "unavailable", source = "WebsiteContentStore" }
                        : new { status = "published_revision_observed", source = "WebsiteContentStore", published.Id, published.Revision, published.CreatedUtc,
                            coverage = "publication_identity_only_content_not_exported" };
                }
                else publishedWebsite = new { status = "permission_unavailable" };
            }
            if (await Infrastructure.Businesses.BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, actor, selected, cancellationToken, "settings"))
            {
                var voice = await connections.GetReceptionistConfigurationAsync(owner, "openai", cancellationToken);
                if (voice.ConfigurationId is { } configurationId)
                {
                    var preparation = await receptionist.PrepareAsync(owner.Key, configurationId, cancellationToken);
                    receptionistStatus = new { source = "ReceptionistOperatorService", preparation.ObservedUtc, preparation.Status,
                        preparation.ConfigurationId, preparation.ConfigurationVersion, preparation.ConfigurationSha256,
                        preparation.AdmissionEnabled, preparation.CurrentBindingAccepted,
                        coverage = "configuration_observation_only_not_live_call_acceptance" };
                }
                else receptionistStatus = new { status = "not_configured", source = "ReceptionistOperatorService" };
            }
            else receptionistStatus = new { status = "permission_unavailable" };
        }

        return new
        {
            ok = true,
            workspace = owner.Key,
            businessProfile,
            publishedWebsite,
            receptionistStatus,
            recommendations = plan?.Recommendations,
            advertising = plan?.Advertising,
            guardrails = plan?.Guardrails,
            crmCoverage = "record_level_crm_and_retention_not_exported",
            schemaVersion = 1,
            authority = nameof(WebsiteAnalyticsAiDataBuilder),
            surface = Key,
            application = Application,
            routes = Routes,
            generatedUtc = payload.GeneratedUtc,
            fromUtc = payload.FromUtc,
            toUtc = payload.ToUtc,
            rangeLabel = payload.RangeLabel,
            scopeLabel = payload.ScopeLabel,
            qualityMode = payload.QualityMode,
            trafficFilter = payload.TrafficFilter,
            pageViews = payload.PageViews,
            uniqueVisitors = payload.UniqueVisitors,
            sessions = payload.Sessions,
            verifiedLeads = payload.VerifiedLeads,
            sessionConversionRate = payload.SessionConversionRate,
            intentConversionRate = payload.IntentConversionRate,
            intentAvailable = payload.IntentAvailable,
            totalConversions = payload.TotalConversions,
            topPage = payload.TopPage,
            topCta = payload.TopCta,
            topSource = payload.TopSource,
            topCampaign = payload.TopCampaign,
            topPages = payload.TopPages.Take(10).ToArray(),
            topSources = payload.TopSources.Take(10).ToArray(),
            topCampaigns = payload.TopCampaigns.Take(10).ToArray(),
            pagePerformance = payload.PagePerformance.Take(10).ToArray(),
            channels = payload.Channels.Take(10).ToArray(),
            warnings = payload.Warnings.Take(20).ToArray(),
            privacy = new
            {
                projection = "WebsiteAnalyticsAiRedactor",
                aggregateOnly = true,
                piiIncluded = false,
                leadBodiesIncluded = false,
                credentialsIncluded = false
            }
        };
    }
}


internal static class LegendMasterAppReadServiceCollectionExtensions
{
    internal static IServiceCollection AddLegendMasterAppReadAuthority(this IServiceCollection services)
    {
        services.AddScoped<LegendMasterAppReadAuthority>();
        var contract = typeof(ILegendMasterAppReadProjection);
        var implementations = contract.Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && contract.IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (var implementation in implementations)
            services.AddScoped(contract, implementation);
        return services;
    }
}
