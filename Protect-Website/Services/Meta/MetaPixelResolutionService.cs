using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProtectWebsite.Services.Tracking;

namespace ProtectWebsite.Services.Meta;

public interface IMetaPixelResolutionService
{
    Task<ResolvedMetaPixelContext> ResolveForCurrentRequestAsync(HttpContext? httpContext, CancellationToken cancellationToken = default);
    Task<ResolvedMetaPixelContext> ResolveForLeadAsync(Guid? agentTrackingProfileId, string? agentSlug, bool isFounderPath, CancellationToken cancellationToken = default);
}

public static class MetaPixelOwnerTypes
{
    public const string Agency = "agency";
    public const string Agent = "agent";
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
    private readonly MetaCapiCredentialProtector _metaCapiCredentialProtector;
    private readonly ILogger<MetaPixelResolutionService> _logger;

    public MetaPixelResolutionService(
        IConfiguration configuration,
        MasterAppDbContext db,
        AgentTrackingResolver resolver,
        MetaCapiCredentialProtector metaCapiCredentialProtector,
        ILogger<MetaPixelResolutionService> logger)
    {
        _configuration = configuration;
        _db = db;
        _resolver = resolver;
        _metaCapiCredentialProtector = metaCapiCredentialProtector;
        _logger = logger;
    }

    public async Task<ResolvedMetaPixelContext> ResolveForCurrentRequestAsync(HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        if (httpContext == null)
            return ResolveAgencyFallback();

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

    public async Task<ResolvedMetaPixelContext> ResolveForLeadAsync(Guid? agentTrackingProfileId, string? agentSlug, bool isFounderPath, CancellationToken cancellationToken = default)
    {
        if (isFounderPath)
            return ResolveAgencyFallback();

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
        var agencyFallback = ResolveAgencyFallback();
        if (isFounderPath)
            return agencyFallback;

        if (trackingProfile == null)
            return agencyFallback;

        try
        {
            var agentProfile = await ResolveAgentProfileAsync(trackingProfile, cancellationToken);
            var agentPixelId = Normalize(agentProfile?.MetaPixelId);
            if (string.IsNullOrWhiteSpace(agentPixelId))
                return MergeWithAgentContext(agencyFallback, trackingProfile, agentSlug);

            var decryptedAgentAccessToken = Normalize(
                _metaCapiCredentialProtector.Unprotect(agentProfile?.MetaCapiAccessToken, _logger));
            var agentTestEventCode = Normalize(agentProfile?.MetaTestEventCode);

            return new ResolvedMetaPixelContext
            {
                PixelId = agentPixelId,
                PixelOwnerType = MetaPixelOwnerTypes.Agent,
                AccessToken = decryptedAgentAccessToken,
                TestEventCode = string.IsNullOrWhiteSpace(decryptedAgentAccessToken) ? null : agentTestEventCode,
                AgentTrackingProfileId = trackingProfile.Id,
                AgentSlug = Normalize(agentSlug) ?? Normalize(trackingProfile.Slug)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Meta pixel resolution failed for tracking profile {TrackingProfileId}; using agency fallback.",
                trackingProfile.Id);
            return MergeWithAgentContext(agencyFallback, trackingProfile, agentSlug);
        }
    }

    private ResolvedMetaPixelContext ResolveAgencyFallback()
    {
        var pixelId = Normalize(_configuration["Meta:PixelId"]);
        if (string.IsNullOrWhiteSpace(pixelId))
        {
            return new ResolvedMetaPixelContext
            {
                PixelId = null,
                PixelOwnerType = MetaPixelOwnerTypes.None,
                AccessToken = null,
                TestEventCode = null
            };
        }

        return new ResolvedMetaPixelContext
        {
            PixelId = pixelId,
            PixelOwnerType = MetaPixelOwnerTypes.Agency,
            AccessToken = Normalize(_configuration["Meta:AccessToken"]),
            TestEventCode = Normalize(_configuration["Meta:TestEventCode"])
        };
    }

    private async Task<AgentProfile?> ResolveAgentProfileAsync(AgentTrackingProfile trackingProfile, CancellationToken cancellationToken)
    {
        var hasAgentUserId = !string.IsNullOrWhiteSpace(trackingProfile.AgentUserId);
        var hasAgentUpn = !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn);
        var normalizedUpn = hasAgentUpn ? trackingProfile.AgentUpn.Trim().ToUpperInvariant() : string.Empty;

        if (!hasAgentUserId && !hasAgentUpn)
            return null;

        var candidates = await _db.AgentProfiles.AsNoTracking()
            .Where(x =>
                (hasAgentUserId && x.AgentUserId == trackingProfile.AgentUserId) ||
                (hasAgentUpn && (x.NormalizedEmail == normalizedUpn || x.AgentUpn == trackingProfile.AgentUpn)))
            .ToListAsync(cancellationToken);

        return candidates
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.MetaPixelId))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.MetaCapiAccessToken))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Npn))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ShortBio))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.FullName))
            .ThenByDescending(x => hasAgentUserId && x.AgentUserId == trackingProfile.AgentUserId)
            .ThenByDescending(x => x.UpdatedUtc)
            .FirstOrDefault();
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
