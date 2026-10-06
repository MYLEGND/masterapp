using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record ProviderMeasurementEvidence(int Attempted, int Accepted, int Pending, int Retrying,
    int Failed, bool AttributionObserved, DateTime? LastSentUtc, string AcceptanceEvidence);
public sealed record MarketingMeasurementEvidenceSnapshot(
    string OwnerKey,
    DateTime WindowFromUtc,
    bool ReceivingEvents,
    DateTime? LastReceivedUtc,
    ProviderMeasurementEvidence Meta,
    ProviderMeasurementEvidence OpenAi,
    ProviderMeasurementEvidence Google,
    ProviderMeasurementEvidence TikTok);

/// <summary>
/// Single read-only interpretation policy for provider delivery evidence.
/// Configuration and transport attempts never become business truth through this policy.
/// </summary>
public static class MarketingDeliveryEvidencePolicy
{
    public const string MetaAcceptanceEvidence = "events_received";
    public const string OpenAiAcceptanceEvidence = "http_accepted";

    public static bool MetaAttempted(MetaSignalEvent row) =>
        CanonicalAdvertisingEventProjection.ReadBoolean(row.MetadataJson, "metaServerAttempted") == true;

    public static bool MetaProviderAccepted(MetaSignalEvent row) =>
        row.MetaServerSent &&
        CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "metaServerEventsReceived") > 0;

    public static bool MetaRetryable(MetaSignalEvent row) =>
        CanonicalAdvertisingEventProjection.ReadBoolean(row.MetadataJson, "metaServerRetryable") == true;

    public static DateTime? MetaDispatchedUtc(MetaSignalEvent row) =>
        DateTime.TryParse(
            CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "metaServerDispatchedUtc"),
            out var at)
            ? at
            : null;

    public static bool HttpTransportAccepted(MarketingDestinationDelivery row) =>
        row.Status == "sent" && row.LastHttpStatusCode is >= 200 and < 300;
}

/// <summary>Read-only source-linked evidence. Credentials and configuration never imply event delivery.</summary>
public sealed class MarketingMeasurementEvidenceService(MasterAppDbContext db, IConfiguration configuration,
    MarketingConnectionStore connections, IOpenAiAdsAccountConnectionAuthority openAiConnections)
{
    public async Task<MarketingMeasurementEvidenceSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var from = DateTime.UtcNow.AddDays(-30);
        var sources = await new AnalyticsQueryService(db, configuration).LoadOwnerEventsAsync(from, owner, ct);
        var currentMeta = await connections.GetStatusAsync(owner, ct);
        var currentOpenAi = await openAiConnections.GetAsync(owner, ct);
        var sourceById = sources.ToDictionary(e => e.Id);
        var metaQuery = db.MetaSignalEvents.AsNoTracking().Where(e => e.CreatedUtc >= from);
        if (owner.CommerceBusinessId is { } metaBusiness) metaQuery = metaQuery.Where(e => e.CommerceBusinessId == metaBusiness);
        else if (owner.AgentTrackingProfileId is { } metaAgent) metaQuery = metaQuery.Where(e => e.AgentTrackingProfileId == metaAgent && e.CommerceBusinessId == null);
        else metaQuery = metaQuery.Where(e => e.CommerceBusinessId == null);
        var meta = (await metaQuery.ToListAsync(ct)).Where(m =>
            CanonicalAdvertisingEventProjection.ReadInt64(m.MetadataJson, "sourceAnalyticsEventId") is { } id &&
            sourceById.TryGetValue(id, out var source) && source.AgentTrackingProfileId == m.AgentTrackingProfileId &&
            source.CommerceBusinessId == m.CommerceBusinessId && currentMeta is { DisconnectedUtc: null } &&
            !string.IsNullOrWhiteSpace(currentMeta.PixelId) &&
            (CanonicalAdvertisingEventProjection.ReadBoolean(m.MetadataJson, "metaServerAttempted") != true ||
             CanonicalAdvertisingEventProjection.ReadString(m.MetadataJson, "metaServerPixelId") == currentMeta.PixelId)).ToArray();
        var deliveries = await db.Set<MarketingDestinationDelivery>().AsNoTracking()
            .Where(r => r.OwnerKey == owner.Key && r.Provider == MarketingDestinationKeys.OpenAi &&
                r.Channel == "server" && r.CanonicalSource == nameof(AnalyticsEvent) && r.CreatedUtc >= from).ToListAsync(ct);
        var openAi = deliveries.Where(r => r.AnalyticsEventId is { } id && sourceById.ContainsKey(id) &&
            r.AgentTrackingProfileId == owner.AgentTrackingProfileId && r.CommerceBusinessId == owner.CommerceBusinessId &&
            OpenAiConversionDispatcherHostedService.MatchesCurrentDestination(r, currentOpenAi)).ToArray();
        var metaAccepted = meta.Count(MarketingDeliveryEvidencePolicy.MetaProviderAccepted);
        var openAiAccepted = openAi.Count(MarketingDeliveryEvidencePolicy.HttpTransportAccepted);
        return new(owner.Key, from, sources.Count > 0, sources.Select(e => (DateTime?)e.ReceivedUtc).Max(),
            new(meta.Count(MarketingDeliveryEvidencePolicy.MetaAttempted), metaAccepted,
                meta.Count(m => !MarketingDeliveryEvidencePolicy.MetaAttempted(m) &&
                    MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(m.EventName, m.MetadataJson)),
                meta.Count(MarketingDeliveryEvidencePolicy.MetaRetryable),
                meta.Count(m => MarketingDeliveryEvidencePolicy.MetaAttempted(m) &&
                    !m.MetaServerSent && !MarketingDeliveryEvidencePolicy.MetaRetryable(m)),
                sources.Any(e => !string.IsNullOrWhiteSpace(e.Fbclid) || !string.IsNullOrWhiteSpace(e.MetaCampaignId)),
                meta.Where(MarketingDeliveryEvidencePolicy.MetaProviderAccepted)
                    .Select(MarketingDeliveryEvidencePolicy.MetaDispatchedUtc).Max(),
                metaAccepted > 0 ? MarketingDeliveryEvidencePolicy.MetaAcceptanceEvidence : "not_observed"),
            new(openAi.Count(r => r.AttemptCount > 0), openAiAccepted,
                openAi.Count(r => r.Status == "pending" || r.Status.StartsWith("blocked_", StringComparison.Ordinal)),
                openAi.Count(r => r.Status == "retryable"), openAi.Count(r => r.Status == "permanent_failure"),
                sources.Any(e => OpenAiClickReference.Normalize(e.Oppref) is not null),
                openAi.Where(MarketingDeliveryEvidencePolicy.HttpTransportAccepted).Select(r => r.SentUtc).Max(),
                openAiAccepted > 0 ? MarketingDeliveryEvidencePolicy.OpenAiAcceptanceEvidence : "not_observed"),
            new(0, 0, 0, 0, 0,
                sources.Any(e => PaidAdsClickReference.NormalizeGoogle(
                    CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "gclid")) is not null ||
                    CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.GoogleAds),
                null,
                "conversion_delivery_not_configured"),
            new(0, 0, 0, 0, 0,
                sources.Any(e => PaidAdsClickReference.NormalizeTikTok(
                    CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "ttclid")) is not null ||
                    CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.TikTokAds),
                null,
                "conversion_delivery_not_configured"));
    }
}
