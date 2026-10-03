using System.Globalization;
using System.Text.Json;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IUnifiedMarketingPerformanceService
{
    Task<UnifiedChannelPerformanceSnapshot> GetAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        CancellationToken ct = default);
}

public sealed class UnifiedMarketingPerformanceService(
    IOpenAiAdsExecutionService openAiAds,
    IOpenAiAdsAccountConnectionAuthority openAiConnections,
    IAnalyticsQueryService analytics,
    IMetaAdsService metaAds) : IUnifiedMarketingPerformanceService
{
    public async Task<UnifiedChannelPerformanceSnapshot> GetAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(analyticsScope);
        ArgumentNullException.ThrowIfNull(range);

        var notes = new List<string>();
        var delivery = new List<ProviderDeliveryMetricRow>();

        var connection = await openAiConnections.GetAsync(owner, ct);
        if (connection.Connected && connection.HasManagementCredential)
        {
            try
            {
                var provider = await openAiAds.GetAccountInsightsAsync(
                    owner,
                    "campaign",
                    new OpenAiAdsInsightsQuery(
                        range.FromUtc,
                        range.ToUtc,
                        TimeGranularity: "none",
                        Fields: ["campaign.spend", "campaign.impressions", "campaign.clicks", "campaign.id", "campaign.name", "campaign.status"]),
                    ct);
                delivery.AddRange(ParseOpenAiRows(provider.Payload));
                if (provider.EffectiveFromUtc is { } effectiveFrom && provider.EffectiveToUtc is { } effectiveTo &&
                    (effectiveFrom != range.FromUtc.ToUniversalTime() || effectiveTo != range.ToUtc.ToUniversalTime()))
                    notes.Add($"ChatGPT Ads delivery covers completed account-local hours: {effectiveFrom:O} to {effectiveTo:O} (UTC). Canonical outcomes retain the selected range.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OpenAiAdsExecutionException or HttpRequestException ||
                ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add("ChatGPT Ads delivery metrics are temporarily unavailable: " + ex.Message);
            }
        }
        else
        {
            notes.Add("ChatGPT Ads is not connected for this marketing owner.");
        }

        var attributedEvents = await analytics.LoadAttributedEventsAsync(range, analyticsScope, TrafficType.All, ct);
        var openAiEvents = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(attributedEvents)
            .Where(x => CanonicalMarketingOutcomeProjection.ChannelFor(x) == MarketingChannels.ChatGptAds).ToArray();
        var outcomes = new CanonicalOutcomeTotals(
            Leads: openAiEvents.LongCount(x => CanonicalMarketingOutcomeProjection.OutcomeName(x) == "Lead"),
            QualifiedLeads: openAiEvents.LongCount(x => CanonicalMarketingOutcomeProjection.OutcomeName(x) == "QualifiedLead"),
            Appointments: openAiEvents.LongCount(x => CanonicalMarketingOutcomeProjection.OutcomeName(x) is "AppointmentBooked" or "AppointmentCompleted"),
            Customers: openAiEvents.LongCount(x => CanonicalMarketingOutcomeProjection.IsCustomer(x.EventType)),
            Revenue: openAiEvents.Where(x => CanonicalMarketingOutcomeProjection.IsCustomer(x.EventType))
                .Sum(x => CanonicalMarketingOutcomeProjection.ReadMoney(x.MetadataJson)));

        var channels = new List<ChannelPerformanceRow>();
        var openAiSpend = delivery.Sum(x => x.Spend);
        channels.Add(new ChannelPerformanceRow(
            MarketingChannels.ChatGptAds,
            openAiSpend,
            delivery.Sum(x => x.Impressions),
            delivery.Sum(x => x.Clicks),
            outcomes.Leads,
            outcomes.QualifiedLeads,
            outcomes.Appointments,
            outcomes.Customers,
            outcomes.Revenue,
            openAiSpend > 0 ? Math.Round(outcomes.Revenue / openAiSpend, 2) : 0,
            openAiEvents.Length > 0 ? "reference_observed" : "not_observed",
            "Canonical oppref lineage; campaign credit requires separate provider evidence"));

        try
        {
            var meta = await metaAds.GetCampaignsAsync(range, analyticsScope, ct);
            var rows = meta.Rows ?? [];
            var spend = rows.Sum(x => x.Spend);
            channels.Add(new ChannelPerformanceRow(
                MarketingChannels.MetaAds,
                spend,
                rows.Sum(x => x.Impressions),
                rows.Sum(x => x.Clicks),
                rows.Sum(x => x.WebsiteLeads),
                rows.Sum(x => x.QualifiedLeads),
                rows.Sum(x => x.Appointments),
                rows.Sum(x => x.PoliciesPaid),
                rows.Sum(x => x.PaidPremium),
                spend > 0 ? Math.Round(rows.Sum(x => x.PaidPremium) / spend, 2) : 0,
                "verified",
                "Canonical Meta campaign attribution + CRM outcomes"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add("Meta Ads comparison is unavailable for this scope: " + ex.Message);
        }

        AddNonPaidRows(channels, attributedEvents);

        if (delivery.Count > 0 && outcomes.Leads > 0)
            notes.Add("ChatGPT Ads downstream outcomes are joined only through stored oppref lineage. Campaign-level revenue is not inferred when provider campaign lineage cannot be proven.");

        return new UnifiedChannelPerformanceSnapshot(
            owner,
            range.FromUtc,
            range.ToUtc,
            DateTime.UtcNow,
            delivery,
            outcomes,
            channels,
            notes);
    }

    private static IReadOnlyList<ProviderDeliveryMetricRow> ParseOpenAiRows(JsonElement payload)
    {
        var data = payload.ValueKind == JsonValueKind.Object &&
                   payload.TryGetProperty("data", out var rows) &&
                   rows.ValueKind == JsonValueKind.Array
            ? rows
            : default;

        if (data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProviderDeliveryMetricRow>();
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var campaignId = Text(row, "campaign_id") ?? Text(row, "id");
            var id = campaignId ?? Text(row, "entity_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            result.Add(new ProviderDeliveryMetricRow(
                MarketingChannels.ChatGptAds,
                "campaign",
                id!,
                Text(row, "campaign_name") ?? Text(row, "name") ?? id!,
                Text(row, "status") ?? "unknown",
                campaignId,
                Text(row, "ad_group_id"),
                Text(row, "ad_id"),
                Decimal(row, "spend", "spend_amount", "cost"),
                Long(row, "impressions"),
                Long(row, "clicks"),
                Long(row, "conversions"),
                row.Clone()));
        }

        return result;
    }

    private static void AddNonPaidRows(
        ICollection<ChannelPerformanceRow> channels,
        IReadOnlyCollection<Domain.Entities.AnalyticsEvent> events)
    {
        foreach (var (type, channel) in new[]
        {
            (TrafficType.Organic, MarketingChannels.Organic),
            (TrafficType.Direct, MarketingChannels.Direct),
            (TrafficType.Referral, MarketingChannels.Referral)
        })
        {
            var rows = events.Where(x => TrafficAttribution.Classify(
                x.UtmSource, x.UtmMedium, x.UtmCampaign, x.Fbclid, x.ReferrerHost,
                x.MetaCampaignId, x.MetaAdSetId, x.MetaAdId, x.IsInternal,
                x.Environment, x.Host, x.Oppref) == type).ToList();

            channels.Add(new ChannelPerformanceRow(
                channel, 0, 0, 0,
                CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(rows).LongCount(x => CanonicalMarketingOutcomeProjection.OutcomeName(x) == "Lead"),
                0, 0, 0, 0, 0,
                "observed",
                "Canonical website attribution events"));
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
        }
        return 0;
    }

    private static decimal Decimal(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
        }
        return 0;
    }
}
