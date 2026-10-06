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
    public const string GoogleAcceptanceEvidence = "click_conversion_accepted";
    public const string TikTokAcceptanceEvidence = "events_api_accepted";

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
            .Where(r => r.OwnerKey == owner.Key &&
                (r.Provider == MarketingDestinationKeys.OpenAi ||
                 r.Provider == MarketingDestinationKeys.Google ||
                 r.Provider == MarketingDestinationKeys.TikTok) &&
                r.Channel == "server" && r.CanonicalSource == nameof(AnalyticsEvent) && r.CreatedUtc >= from)
            .ToListAsync(ct);
        var openAi = deliveries.Where(r => r.Provider == MarketingDestinationKeys.OpenAi &&
            r.AnalyticsEventId is { } id && sourceById.ContainsKey(id) &&
            r.AgentTrackingProfileId == owner.AgentTrackingProfileId && r.CommerceBusinessId == owner.CommerceBusinessId &&
            OpenAiConversionDispatcherHostedService.MatchesCurrentDestination(r, currentOpenAi)).ToArray();

        var googleConnection = await connections.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google, ct);
        var googleMeasurement = await connections.GetProviderMeasurementConfigurationAsync(owner, MarketingDestinationKeys.Google, ct);
        var tiktokConnection = await connections.GetProviderConnectionAsync(owner, MarketingDestinationKeys.TikTok, ct);
        var tiktokMeasurement = await connections.GetProviderMeasurementConfigurationAsync(owner, MarketingDestinationKeys.TikTok, ct);
        var google = CurrentExternalReceipts(deliveries, sourceById, owner, googleConnection, googleMeasurement);
        var tiktok = CurrentExternalReceipts(deliveries, sourceById, owner, tiktokConnection, tiktokMeasurement);

        var metaAccepted = meta.Count(MarketingDeliveryEvidencePolicy.MetaProviderAccepted);
        var openAiAccepted = openAi.Count(MarketingDeliveryEvidencePolicy.HttpTransportAccepted);
        var googleAccepted = google.Count(MarketingDeliveryEvidencePolicy.HttpTransportAccepted);
        var tiktokAccepted = tiktok.Count(MarketingDeliveryEvidencePolicy.HttpTransportAccepted);
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
            EvidenceForExternal(
                google,
                googleAccepted,
                sources.Any(e => PaidAdsClickReference.NormalizeGoogle(
                    CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "gclid")) is not null ||
                    CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.GoogleAds),
                googleMeasurement.MappingReady,
                MarketingDeliveryEvidencePolicy.GoogleAcceptanceEvidence),
            EvidenceForExternal(
                tiktok,
                tiktokAccepted,
                sources.Any(e => PaidAdsClickReference.NormalizeTikTok(
                    CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "ttclid")) is not null ||
                    CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.TikTokAds),
                tiktokMeasurement.MappingReady,
                MarketingDeliveryEvidencePolicy.TikTokAcceptanceEvidence));
    }

    private static MarketingDestinationDelivery[] CurrentExternalReceipts(
        IReadOnlyCollection<MarketingDestinationDelivery> deliveries,
        IReadOnlyDictionary<long, AnalyticsEvent> sourceById,
        MarketingOwnerScope owner,
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderMeasurementConfiguration measurement)
    {
        if (!connection.Ready || !measurement.MappingReady || string.IsNullOrWhiteSpace(connection.AccountId))
            return [];

        return deliveries.Where(receipt =>
            receipt.Provider == connection.Provider &&
            receipt.AnalyticsEventId is { } id &&
            sourceById.ContainsKey(id) &&
            receipt.AgentTrackingProfileId == owner.AgentTrackingProfileId &&
            receipt.CommerceBusinessId == owner.CommerceBusinessId &&
            string.Equals(receipt.AdvertiserAccountId, connection.AccountId, StringComparison.Ordinal) &&
            measurement.Mappings.Any(mapping =>
                string.Equals(mapping.CanonicalEventName, receipt.CanonicalEventName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.ProviderEventName, receipt.ProviderEventName, StringComparison.Ordinal) &&
                string.Equals(receipt.ConversionDataSourceId,
                    connection.Provider == MarketingDestinationKeys.Google
                        ? mapping.DestinationId
                        : measurement.EventSourceId,
                    StringComparison.Ordinal)))
            .ToArray();
    }

    private static ProviderMeasurementEvidence EvidenceForExternal(
        IReadOnlyCollection<MarketingDestinationDelivery> receipts,
        int accepted,
        bool attributionObserved,
        bool mappingReady,
        string acceptanceEvidence) =>
        new(
            receipts.Count(row => row.AttemptCount > 0),
            accepted,
            receipts.Count(row => row.Status == "pending" ||
                row.Status.StartsWith("blocked_", StringComparison.Ordinal)),
            receipts.Count(row => row.Status == "retryable"),
            receipts.Count(row => row.Status == "permanent_failure"),
            attributionObserved,
            receipts.Where(MarketingDeliveryEvidencePolicy.HttpTransportAccepted)
                .Select(row => row.SentUtc).Max(),
            accepted > 0 ? acceptanceEvidence :
                mappingReady ? "not_observed" : "conversion_mapping_not_ready");

}
