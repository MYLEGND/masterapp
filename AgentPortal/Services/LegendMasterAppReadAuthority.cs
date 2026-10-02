using System.Security.Claims;
using AgentPortal.Security;
using AgentPortal.Services.Tracking;
using Infrastructure.Analytics;
using Shared.Analytics;

namespace AgentPortal.Services;

internal interface ILegendMasterAppReadProjection
{
    string Key { get; }
    string Application { get; }
    IReadOnlyList<string> Routes { get; }
    string Description { get; }
    Task<object> ReadAsync(ClaimsPrincipal founder, string? preset, CancellationToken cancellationToken);
}

internal sealed class LegendMasterAppReadAuthority(IEnumerable<ILegendMasterAppReadProjection> projections)
{
    private readonly IReadOnlyDictionary<string, ILegendMasterAppReadProjection> _projections =
        projections.ToDictionary(value => value.Key, StringComparer.OrdinalIgnoreCase);

    internal object Catalog() => new
    {
        schemaVersion = 1,
        authority = nameof(LegendMasterAppReadAuthority),
        sourceOfTruth = "canonical_page_service_projections",
        providerSpecificRegistry = false,
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
        string? preset,
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

        return await projection.ReadAsync(founder, preset, cancellationToken);
    }
}

internal sealed class WebsiteAnalyticsLegendReadProjection(
    WebsiteAnalyticsAiDataBuilder dataBuilder,
    IAgentTrackingService tracking) : ILegendMasterAppReadProjection
{
    public string Key => "agent-portal.website-analytics";
    public string Application => "AgentPortal";
    public IReadOnlyList<string> Routes => ["/WebsiteAnalytics", "/website-analytics"];
    public string Description =>
        "Privacy-safe Website Analytics operational performance using the same canonical AI review projection as the page.";

    public async Task<object> ReadAsync(
        ClaimsPrincipal founder,
        string? preset,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        var profile = await tracking.GetByUserIdAsync(FounderGuard.FounderOid, cancellationToken)
            ?? throw new InvalidOperationException("Founder analytics scope is unavailable.");

        var normalizedPreset = preset?.Trim().ToLowerInvariant();
        if (normalizedPreset is not ("today" or "7d" or "30d" or "month" or "year"))
            normalizedPreset = "7d";

        var range = TimeRangeRequest.FromPreset(normalizedPreset, viewerTz: TimeZoneInfo.Utc);
        var scope = ScopeContext.ForFounder(profile.Id);
        var payload = await dataBuilder.BuildAsync(
            range,
            scope,
            range.Label,
            "Founder Personal",
            "All Traffic",
            TrafficType.All,
            cancellationToken);

        return new
        {
            ok = true,
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
