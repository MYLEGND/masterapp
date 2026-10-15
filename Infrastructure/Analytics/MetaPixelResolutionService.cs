using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Infrastructure.Analytics;
using Shared.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Analytics;

public interface IMetaPixelResolutionService
{
    Task<ResolvedMetaPixelContext> ResolveForOwnerAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default);
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

    /// <summary>Projects credentials for an already resolved permanent owner; never infers another tenant.</summary>
    public async Task<ResolvedMetaPixelContext> ResolveForOwnerAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner == MarketingOwnerScope.Founder) return await ResolveFounderAsync(cancellationToken);
        if (owner.CommerceBusinessId is Guid businessId) return await ResolveForBusinessAsync(businessId, cancellationToken);
        if (owner.AgentTrackingProfileId is not Guid profileId) return new();
        var resolved = await _resolver.ResolveByIdAsync(profileId, cancellationToken);
        return !resolved.Found || resolved.Profile is null ? new()
            : await ResolveInternalAsync(resolved.Profile, resolved.CanonicalSlug ?? resolved.Profile.Slug, false, cancellationToken);
    }

    public async Task<ResolvedMetaPixelContext> ResolveForCurrentRequestAsync(HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        var owner = await ProtectWebsiteOwnerResolver.ResolveAsync(httpContext, _resolver,
            _configuration["Founder:Upn"], ct: cancellationToken);
        if (owner is null) return new();
        return await ResolveInternalAsync(owner.Profile, owner.Slug, owner.IsFounder, cancellationToken);
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
        var owner = await ProtectWebsiteOwnerResolver.ResolveLeadAsync(_resolver, _configuration["Founder:Upn"],
            agentTrackingProfileId, agentSlug, isFounderPath, cancellationToken);
        if (owner is null) return new();
        return await ResolveInternalAsync(owner.Profile, owner.Slug, owner.IsFounder, cancellationToken);
    }

    private async Task<ResolvedMetaPixelContext> ResolveInternalAsync(
        AgentTrackingProfile? trackingProfile,
        string? agentSlug,
        bool isFounderPath,
        CancellationToken cancellationToken)
    {
        if (isFounderPath)
            return await ResolveFounderAsync(cancellationToken);

        // Scoped agent traffic is tenant-owned. It must never inherit Founder/agency
        // advertising credentials when the agent has not connected its own destination.
        if (trackingProfile == null)
            return new ResolvedMetaPixelContext();

        try
        {
            var connection = await _agentMarketing.GetAsync(trackingProfile, cancellationToken);
            if (connection.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(connection.PixelId))
                return MergeWithAgentContext(new ResolvedMetaPixelContext(), trackingProfile, agentSlug);
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

    private async Task<ResolvedMetaPixelContext> ResolveFounderAsync(CancellationToken cancellationToken)
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

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
