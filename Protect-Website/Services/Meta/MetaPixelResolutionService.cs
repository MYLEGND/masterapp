using Infrastructure.Analytics;
using Shared.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProtectWebsite.Services.Tracking;

namespace ProtectWebsite.Services.Meta;

public interface IMetaPixelResolutionService
{
    Task<ResolvedMetaPixelContext> ResolveForBusinessAsync(Guid businessId, CancellationToken cancellationToken = default);
    Task<ResolvedMetaPixelContext> ResolveForCurrentRequestAsync(HttpContext? httpContext, CancellationToken cancellationToken = default);
    Task<ResolvedMetaPixelContext> ResolveForLeadAsync(Guid? agentTrackingProfileId, string? agentSlug, bool isFounderPath, CancellationToken cancellationToken = default);
}

public static class MetaPixelOwnerTypes
{
    public const string Agency = "agency";
    public const string Agent = "agent";
    public const string Business = "business";
    public const string None = "none";
}

public sealed class ResolvedMetaPixelContext
{
    public string? PixelId { get; init; }
    public string PixelOwnerType { get; init; } = MetaPixelOwnerTypes.None;
    public string? AccessToken { get; init; }
    public string? TestEventCode { get; init; }
    public Guid? AgentTrackingProfileId { get; init; }
    public string? AgentSlug { get; init; }

    public bool HasBrowserPixel => !string.IsNullOrWhiteSpace(PixelId);
    public bool HasServerCapiCredentials => HasBrowserPixel && !string.IsNullOrWhiteSpace(AccessToken);

    public string? ResolveServerCapiSkipNote()
    {
        if (!HasBrowserPixel)
            return "meta_config_missing";

        if (HasServerCapiCredentials)
            return null;

        return string.Equals(PixelOwnerType, MetaPixelOwnerTypes.Agent, StringComparison.OrdinalIgnoreCase)
            ? "skipped_agent_token_missing"
            : "meta_config_missing";
    }
}

public sealed class MetaPixelResolutionService : IMetaPixelResolutionService
{
    private const string RequestCacheKey = "__ResolvedMetaPixelContext";

    private readonly IConfiguration _configuration;
    private readonly MasterAppDbContext _db;
    private readonly AgentTrackingResolver _resolver;
    private readonly AgentMarketingProfileService _agentMarketing;
    private readonly MarketingConnectionStore _connections;
    private readonly ILogger<MetaPixelResolutionService> _logger;

    public MetaPixelResolutionService(
        IConfiguration configuration,
        MasterAppDbContext db,
        AgentTrackingResolver resolver,
        AgentMarketingProfileService agentMarketing,
        MarketingConnectionStore connections,
        ILogger<MetaPixelResolutionService> logger)
    {
        _configuration = configuration;
        _db = db;
        _resolver = resolver;
        _agentMarketing = agentMarketing;
        _connections = connections;
        _logger = logger;
    }

    public async Task<ResolvedMetaPixelContext> ResolveForCurrentRequestAsync(HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        if (httpContext == null)
            return await ResolveAgencyFallbackAsync(cancellationToken);

        if (httpContext.Items.TryGetValue(RequestCacheKey, out var cached) &&
            cached is ResolvedMetaPixelContext cachedContext)
        {
            return cachedContext;
        }

        var request = httpContext.Request;
        var explicitSlug = ResolveExplicitAgentSlug(request);
        if (!string.IsNullOrWhiteSpace(explicitSlug))
        {
            var resolvedBySlug = await _resolver.ResolveBySlugAsync(explicitSlug, cancellationToken);
            if (resolvedBySlug.Found && resolvedBySlug.Profile != null)
            {
                var explicitResolved = await ResolveInternalAsync(
                    resolvedBySlug.Profile,
                    Normalize(resolvedBySlug.CanonicalSlug) ?? explicitSlug,
                    isFounderPath: false,
                    cancellationToken);
                httpContext.Items[RequestCacheKey] = explicitResolved;
                return explicitResolved;
            }
        }

        var isFounderPath = httpContext.Items["IsFounderPath"] as bool? == true;
        var trackingProfile = httpContext.Items["TrackingProfile"] as AgentTrackingProfile;
        var trackingSlug = Normalize(httpContext.Items["TrackingSlug"] as string);

        var resolved = await ResolveInternalAsync(trackingProfile, trackingSlug, isFounderPath, cancellationToken);
        httpContext.Items[RequestCacheKey] = resolved;
        return resolved;
    }

    public async Task<ResolvedMetaPixelContext> ResolveForBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        // A missing or disconnected business connection never inherits another owner's credentials.
        if (businessId == Guid.Empty || !await _db.CommerceBusinesses.AsNoTracking()
            .AnyAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", cancellationToken)) return new() { PixelOwnerType = MetaPixelOwnerTypes.Business };
        var owner = MarketingOwnerScope.Business(businessId);
        var connection = await _connections.GetStatusAsync(owner, cancellationToken);
        if (connection is null || connection.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(connection.PixelId)) return new() { PixelOwnerType = MetaPixelOwnerTypes.Business };
        return new ResolvedMetaPixelContext
        {
            PixelId = connection.PixelId, PixelOwnerType = MetaPixelOwnerTypes.Business,
            AccessToken = await _connections.GetCapiTokenAsync(owner, cancellationToken),
            TestEventCode = connection.TestEventCode
        };
    }

    public async Task<ResolvedMetaPixelContext> ResolveForLeadAsync(Guid? agentTrackingProfileId, string? agentSlug, bool isFounderPath, CancellationToken cancellationToken = default)
    {
        if (isFounderPath)
            return await ResolveAgencyFallbackAsync(cancellationToken);

        AgentTrackingProfile? trackingProfile = null;
        var normalizedSlug = Normalize(agentSlug);

        if (agentTrackingProfileId.HasValue && agentTrackingProfileId.Value != Guid.Empty)
        {
            trackingProfile = await _db.AgentTrackingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == agentTrackingProfileId.Value, cancellationToken);
        }

        if (trackingProfile == null && !string.IsNullOrWhiteSpace(normalizedSlug))
        {
            var resolvedBySlug = await _resolver.ResolveBySlugAsync(normalizedSlug, cancellationToken);
            if (resolvedBySlug.Found && resolvedBySlug.Profile != null)
            {
                trackingProfile = resolvedBySlug.Profile;
                normalizedSlug = Normalize(resolvedBySlug.CanonicalSlug) ?? normalizedSlug;
            }
        }

        return await ResolveInternalAsync(trackingProfile, normalizedSlug, isFounderPath: false, cancellationToken);
    }

    private async Task<ResolvedMetaPixelContext> ResolveInternalAsync(
        AgentTrackingProfile? trackingProfile,
        string? agentSlug,
        bool isFounderPath,
        CancellationToken cancellationToken)
    {
        var agencyFallback = await ResolveAgencyFallbackAsync(cancellationToken);
        if (isFounderPath)
            return agencyFallback;

        if (trackingProfile == null)
            return agencyFallback;

        try
        {
            var connection = await _agentMarketing.GetAsync(trackingProfile, cancellationToken);
            if (connection.DisconnectedUtc.HasValue)
                return MergeWithAgentContext(new ResolvedMetaPixelContext(), trackingProfile, agentSlug);
            if (string.IsNullOrWhiteSpace(connection.PixelId))
                return MergeWithAgentContext(agencyFallback, trackingProfile, agentSlug);
            var token = await _connections.GetCapiTokenAsync(MarketingOwnerScope.Agent(trackingProfile.Id), cancellationToken);
            return new ResolvedMetaPixelContext
            {
                PixelId = connection.PixelId, PixelOwnerType = MetaPixelOwnerTypes.Agent,
                AccessToken = token, TestEventCode = token is null ? null : connection.TestEventCode,
                AgentTrackingProfileId = trackingProfile.Id,
                AgentSlug = Normalize(agentSlug) ?? Normalize(trackingProfile.Slug)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Meta pixel resolution failed for tracking profile {TrackingProfileId}; advertising is disabled for this request.",
                trackingProfile.Id);
            return MergeWithAgentContext(new ResolvedMetaPixelContext(), trackingProfile, agentSlug);
        }
    }

    private async Task<ResolvedMetaPixelContext> ResolveAgencyFallbackAsync(CancellationToken cancellationToken)
    {
        var owner = MarketingOwnerScope.Founder;
        await _connections.ImportProfileAsync(owner, Normalize(_configuration["Meta:PixelId"]),
            Normalize(_configuration["Meta:AccessToken"]), Normalize(_configuration["Meta:TestEventCode"]), cancellationToken);
        var connection = (await _connections.GetStatusAsync(owner, cancellationToken))!;
        if (connection.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(connection.PixelId)) return new();
        return new ResolvedMetaPixelContext
        {
            PixelId = connection.PixelId, PixelOwnerType = MetaPixelOwnerTypes.Agency,
            AccessToken = await _connections.GetCapiTokenAsync(owner, cancellationToken), TestEventCode = connection.TestEventCode
        };
    }

    private static ResolvedMetaPixelContext MergeWithAgentContext(
        ResolvedMetaPixelContext context,
        AgentTrackingProfile trackingProfile,
        string? agentSlug)
    {
        return new ResolvedMetaPixelContext
        {
            PixelId = context.PixelId,
            PixelOwnerType = context.PixelOwnerType,
            AccessToken = context.AccessToken,
            TestEventCode = context.TestEventCode,
            AgentTrackingProfileId = trackingProfile.Id,
            AgentSlug = Normalize(agentSlug) ?? Normalize(trackingProfile.Slug)
        };
    }

    private static string? ResolveExplicitAgentSlug(HttpRequest? request)
    {
        if (request == null)
            return null;

        string? formSlug = null;
        try
        {
            if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
                formSlug = Normalize(request.Form["AgentSlug"].ToString());
        }
        catch
        {
            formSlug = null;
        }

        return Normalize(formSlug)
            ?? ExtractSlugFromPath(request.Path.Value)
            ?? ExtractSlugFromPath(request.Headers["Referer"].ToString());
    }

    private static string? ExtractSlugFromPath(string? pathOrUrl)
    {
        var value = Normalize(pathOrUrl);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = Normalize(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(value))
            return null;

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "a", StringComparison.OrdinalIgnoreCase))
            return Normalize(segments[1]);

        return null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
