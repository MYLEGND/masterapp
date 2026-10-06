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
    IMetaAdsService metaAds,
    IMarketingExternalAdsReportingService externalAds) : IUnifiedMarketingPerformanceService
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
        var openAiDeliveryAvailable = false;

        var connection = await openAiConnections.GetAsync(owner, ct);
        if (connection.Connected && connection.HasManagementCredential)
        {
            try
            {
                using var providerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                providerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var provider = await openAiAds.GetAccountInsightsAsync(
                    owner,
                    "campaign",
                    new OpenAiAdsInsightsQuery(
                        range.FromUtc,
                        range.ToUtc,
                        TimeGranularity: "none",
                        Fields: ["campaign.spend", "campaign.impressions", "campaign.clicks", "campaign.id", "campaign.name", "campaign.status"]),
                    providerDeadline.Token);
                delivery.AddRange(ParseOpenAiRows(provider.Payload));
                openAiDeliveryAvailable = true;
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
        var outcomes = CanonicalMarketingOutcomeProjection.Totals(openAiEvents);
        var metaOutcomes = CanonicalMarketingOutcomeProjection.Totals(attributedEvents.Where(e =>
            CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.MetaAds));
        var googleEvents = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(attributedEvents)
            .Where(e => CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.GoogleAds).ToArray();
        var googleOutcomes = CanonicalMarketingOutcomeProjection.Totals(googleEvents);
        var tiktokEvents = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(attributedEvents)
            .Where(e => CanonicalMarketingOutcomeProjection.ChannelFor(e) == MarketingChannels.TikTokAds).ToArray();
        var tiktokOutcomes = CanonicalMarketingOutcomeProjection.Totals(tiktokEvents);

        var channels = new List<ChannelPerformanceRow>();
        decimal? openAiSpend = openAiDeliveryAvailable ? delivery.Sum(x => x.Spend) : null;
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
            openAiSpend > 0 ? Math.Round(outcomes.Revenue / openAiSpend.Value, 2) : null,
            openAiEvents.Length > 0 ? "reference_observed" : "not_observed",
            "Canonical oppref lineage; campaign credit requires separate provider evidence"));

        try
        {
            using var providerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            providerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var meta = await metaAds.GetCampaignsAsync(range, analyticsScope, providerDeadline.Token);
            var rows = meta.Rows ?? [];
            var spend = rows.Sum(x => x.Spend);
            channels.Add(new ChannelPerformanceRow(
                MarketingChannels.MetaAds,
                spend,
                rows.Sum(x => x.Impressions),
                rows.Sum(x => x.Clicks),
                metaOutcomes.Leads,
                metaOutcomes.QualifiedLeads,
                metaOutcomes.Appointments,
                metaOutcomes.Customers,
                metaOutcomes.Revenue,
                spend > 0 ? Math.Round(metaOutcomes.Revenue / spend, 2) : null,
                "canonical_lineage",
                "Canonical Meta campaign attribution + CRM outcomes"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add("Meta Ads comparison is unavailable for this scope: " + ex.Message);
            channels.Add(new ChannelPerformanceRow(MarketingChannels.MetaAds, null, 0, 0, metaOutcomes.Leads, metaOutcomes.QualifiedLeads,
                metaOutcomes.Appointments, metaOutcomes.Customers, metaOutcomes.Revenue, null,
                "unavailable", "Provider reporting unavailable; delivery and economics are unknown"));
        }

        await AddExternalChannelAsync(
            MarketingDestinationKeys.Google,
            MarketingChannels.GoogleAds,
            googleOutcomes,
            googleEvents.Length,
            owner,
            range,
            channels,
            delivery,
            notes,
            ct);
        await AddExternalChannelAsync(
            MarketingDestinationKeys.TikTok,
            MarketingChannels.TikTokAds,
            tiktokOutcomes,
            tiktokEvents.Length,
            owner,
            range,
            channels,
            delivery,
            notes,
            ct);

        AddNonPaidRows(channels, attributedEvents);

        if (delivery.Count > 0)
            notes.Add("Campaign-level downstream attribution is used only where canonical campaign lineage is proven; provider delivery totals never manufacture campaign revenue.");

        var snapshot = new UnifiedChannelPerformanceSnapshot(
            owner,
            range.FromUtc,
            range.ToUtc,
            DateTime.UtcNow,
            delivery,
            outcomes,
            channels,
            notes);
        return snapshot with { Economics = BlendedGrowthEconomicsService.Calculate(owner, range, snapshot, attributedEvents) };
    }

    private async Task AddExternalChannelAsync(
        string provider,
        string channel,
        CanonicalOutcomeTotals outcomes,
        int canonicalOutcomeRows,
        MarketingOwnerScope owner,
        TimeRangeRequest range,
        ICollection<ChannelPerformanceRow> channels,
        ICollection<ProviderDeliveryMetricRow> delivery,
        ICollection<string> notes,
        CancellationToken ct)
    {
        try
        {
            using var providerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            providerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var report = await externalAds.GetCampaignsAsync(owner, provider, range, providerDeadline.Token);
            var rows = report.Rows;
            foreach (var row in rows) delivery.Add(row);
            var spend = rows.Sum(x => x.Spend);
            var providerLabel = channel == MarketingChannels.GoogleAds ? "Google Ads" : "TikTok Ads";
            notes.Add($"{providerLabel} reporting uses account-local dates: {report.ProviderFromDate:yyyy-MM-dd} to {report.ProviderToDate:yyyy-MM-dd} ({report.AccountTimeZone}).");
            channels.Add(new ChannelPerformanceRow(
                channel,
                spend,
                rows.Sum(x => x.Impressions),
                rows.Sum(x => x.Clicks),
                outcomes.Leads,
                outcomes.QualifiedLeads,
                outcomes.Appointments,
                outcomes.Customers,
                outcomes.Revenue,
                spend > 0 ? Math.Round(outcomes.Revenue / spend, 2) : null,
                canonicalOutcomeRows > 0 ? "canonical_lineage" : "not_observed",
                "Provider delivery metrics joined to canonical first-party downstream outcomes; provider conversion totals are not treated as CRM truth"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add($"{channel} delivery comparison is unavailable: {ex.Message}");
            channels.Add(new ChannelPerformanceRow(
                channel,
                null,
                0,
                0,
                outcomes.Leads,
                outcomes.QualifiedLeads,
                outcomes.Appointments,
                outcomes.Customers,
                outcomes.Revenue,
                null,
                canonicalOutcomeRows > 0 ? "canonical_lineage" : "unavailable",
                "Provider reporting unavailable; canonical downstream outcomes remain available independently"));
        }
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
            var rows = events.Where(x =>
                    CanonicalMarketingOutcomeProjection.ChannelFor(x) == channel &&
                    TrafficAttribution.Classify(
                        x.UtmSource, x.UtmMedium, x.UtmCampaign, x.Fbclid, x.ReferrerHost,
                        x.MetaCampaignId, x.MetaAdSetId, x.MetaAdId, x.IsInternal,
                        x.Environment, x.Host, x.Oppref) == type)
                .ToList();

            var totals = CanonicalMarketingOutcomeProjection.Totals(rows);
            channels.Add(new ChannelPerformanceRow(
                channel, 0, 0, 0, totals.Leads, totals.QualifiedLeads, totals.Appointments,
                totals.Customers, totals.Revenue, null,
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
