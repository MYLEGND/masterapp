using System.Globalization;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Provider-independent projection of accepted first-party event truth.</summary>
public static class CanonicalAdvertisingEventProjection
{
    public static string? ResolveEventName(AnalyticsEvent source)
    {
        if (MarketingConversionDestinationCatalog.TryGet(source.EventType, out var conversion)) return conversion.CanonicalEventName;
        if (AnalyticsEventCatalog.TryGet(source.EventType, out var definition) && definition.AllowServer && definition.CountsAsConfirmedLead) return "Lead";
        return MetaSignalAnalyticsAliasCatalog.ResolveSignalName(source.EventType);
    }

    public static bool CanProjectServer(AnalyticsEvent source)
    {
        if (source.IsInternal || source.Id <= 0 || source.EventId == Guid.Empty ||
            !MarketingConversionDestinationCatalog.TryGet(ResolveEventName(source), out _) ||
            ReadBoolean(source.MetadataJson, "isBrowserSignal") == true) return false;
        var neutralEligibility = ReadBoolean(source.MetadataJson, "measurementServerAuthorityEligible");
        if (ReadBoolean(source.MetadataJson, "isServerAuthority") == true)
            // Required legacy adapter: this flag recorded server confirmation, not destination availability.
            return (neutralEligibility ?? ReadBoolean(source.MetadataJson, "metaServerAuthorityEligible")) == true;
        // Older confirmed-lead producers stamped server=false. Only catalog names prohibited at
        // public browser ingress can use this compatibility adapter; explicit neutral rejection wins.
        return neutralEligibility is null &&
            AnalyticsEventCatalog.TryGet(source.EventType, out var definition) && definition.CountsAsConfirmedLead &&
            definition.AllowServer && !definition.AllowBrowser && ReadBoolean(source.MetadataJson, "metaServerAuthorityEligible") == true;
    }

    public static string ResolveEventId(AnalyticsEvent source)
    {
        // Existing producer outcome identifiers remain stable across both destinations.
        var stable = ReadString(source.MetadataJson, "canonicalOutcomeEventId")
            ?? ReadString(source.MetadataJson, "upstreamMetaEventId");
        if (!string.IsNullOrWhiteSpace(stable)) return stable;
        var id = source.EventId.ToString("N");
        return ScopeEventId(source.CommerceBusinessId, id);
    }

    public static string ScopeEventId(Guid? businessId, string eventId)
    {
        if (businessId is not Guid business || business == Guid.Empty) return eventId;
        var prefix = $"business:{business:N}:";
        return eventId.StartsWith(prefix, StringComparison.Ordinal) ? eventId : prefix + eventId;
    }

    public static Task<MarketingOwnerScope?> ResolveOwnerAsync(MasterAppDbContext db, IConfiguration configuration,
        AgentTrackingProfile profile, CancellationToken ct = default) => ResolveOwnerIdentityAsync(db, configuration,
            false, null, profile.Id, profile.Slug, null, null, null, ct);

    public static Task<MarketingOwnerScope?> ResolveOwnerAsync(MasterAppDbContext db, IConfiguration configuration,
        PublicWebsiteRuntimeScope scope, CancellationToken ct = default) => ResolveOwnerIdentityAsync(db, configuration,
            true, scope.CommerceBusinessId, null, null, scope.PublishedVersion?.Id,
            JsonSerializer.Serialize(new { siteKey = scope.SiteKey }), scope.OriginHost, ct);

    public static Task<MarketingOwnerScope?> ResolveOwnerAsync(MasterAppDbContext db, IConfiguration configuration,
        ParfaitApp.Services.CommerceStoreContext commerce, string? host, CancellationToken ct = default)
    {
        var protect = string.Equals(commerce.WebsiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase);
        var legend = string.Equals(commerce.WebsiteSiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase);
        if ((protect && !commerce.AgentTrackingProfileId.HasValue) || (legend && commerce.AgentTrackingProfileId.HasValue))
            return Task.FromResult<MarketingOwnerScope?>(null);
        return ResolveOwnerIdentityAsync(db, configuration, true,
            protect || legend ? null : commerce.CommerceBusinessId, commerce.AgentTrackingProfileId, null,
            commerce.WebsiteContentVersionId, JsonSerializer.Serialize(new { siteKey = commerce.WebsiteSiteKey }), host, ct);
    }

    /// <summary>Required queue-history adapter; both models delegate to the same owner identity authority.</summary>
    public static Task<MarketingOwnerScope?> ResolveOwnerAsync(MasterAppDbContext db, IConfiguration configuration,
        MetaSignalEvent historical, CancellationToken ct = default) => ResolveOwnerIdentityAsync(db, configuration,
            historical.Id > 0, historical.CommerceBusinessId, historical.AgentTrackingProfileId, historical.AgentSlug,
            historical.WebsiteContentVersionId, historical.MetadataJson, historical.Host, ct);

    public static Task<MarketingOwnerScope?> ResolveOwnerAsync(MasterAppDbContext db, IConfiguration configuration,
        AnalyticsEvent source, CancellationToken ct = default) => ResolveOwnerIdentityAsync(db, configuration,
            source.Id > 0, source.CommerceBusinessId, source.AgentTrackingProfileId, source.AgentSlug,
            source.WebsiteContentVersionId, source.MetadataJson, source.Host, ct);

    private static async Task<MarketingOwnerScope?> ResolveOwnerIdentityAsync(MasterAppDbContext db, IConfiguration configuration,
        bool canonicalIdentityVerified, Guid? commerceBusinessId, Guid? agentTrackingProfileId, string? agentSlug,
        Guid? websiteContentVersionId, string? metadataJson, string? host, CancellationToken ct)
    {
        if (commerceBusinessId.HasValue)
        {
            if (commerceBusinessId == Guid.Empty || agentTrackingProfileId.HasValue || !string.IsNullOrWhiteSpace(agentSlug)) return null;
            return await db.CommerceBusinesses.AsNoTracking().AnyAsync(x => x.Id == commerceBusinessId && x.IsActive && x.Status == "Active", ct)
                ? MarketingOwnerScope.Business(commerceBusinessId.Value) : null;
        }
        if (agentTrackingProfileId.HasValue || !string.IsNullOrWhiteSpace(agentSlug))
        {
            var profiles = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
            var owner = await ProtectWebsiteOwnerResolver.ResolveLeadAsync(profiles, configuration["Founder:Upn"],
                agentTrackingProfileId, agentSlug, false, ct);
            return owner is null ? null : owner.IsFounder ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(owner.Profile.Id);
        }
        // The ingest authority stamps the top-level siteKey and Host after public-origin resolution.
        // Nested browser metadata cannot select the permanent owner.
        try
        {
            using var metadata = JsonDocument.Parse(metadataJson ?? "{}");
            if (canonicalIdentityVerified && metadata.RootElement.ValueKind == JsonValueKind.Object && metadata.RootElement.TryGetProperty("siteKey", out var site) &&
                site.ValueKind == JsonValueKind.String && site.GetString() == WebsiteEditorSiteKeys.Legend && PublicWebsiteRuntimeScopeResolver.IsLegendHost(host))
                return MarketingOwnerScope.Founder;
        }
        catch (JsonException) { }
        // An unscoped string supplied by a browser is not Founder ownership evidence.
        if (websiteContentVersionId is not Guid version) return null;
        var website = await (from v in db.Set<WebsiteContentVersion>().AsNoTracking()
                             join state in db.Set<WebsiteContentState>().AsNoTracking() on v.StateId equals state.Id
                             where v.Id == version select state).SingleOrDefaultAsync(ct);
        return website is not null &&
            website.SiteKey == WebsiteEditorSiteKeys.Legend && website.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey
            ? MarketingOwnerScope.Founder : null;
    }

    public static string? ReadString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var doc = JsonDocument.Parse(json); return Read(doc.RootElement, name, 0); }
        catch (JsonException) { return null; }
    }

    private static string? Read(JsonElement root, string name, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 5) return null;
        foreach (var p in root.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value.GetRawText();
        foreach (var container in new[] { "payload", "metadata", "analyticsMetadata" })
            foreach (var p in root.EnumerateObject())
                if (p.Name.Equals(container, StringComparison.OrdinalIgnoreCase) && Read(p.Value, name, depth + 1) is { } value) return value;
        return null;
    }

    public static bool? ReadBoolean(string? json, string name) => bool.TryParse(ReadString(json, name), out var value) ? value : null;
    public static long? ReadInt64(string? json, string name) => long.TryParse(ReadString(json, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
