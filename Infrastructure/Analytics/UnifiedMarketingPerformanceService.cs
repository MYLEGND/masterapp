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
                        Fields: ["spend", "impressions", "clicks", "conversions", "campaign_id", "campaign_name", "status"]),
                    ct);
                delivery.AddRange(ParseOpenAiRows(provider.Payload));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OpenAiAdsExecutionException)
            {
                notes.Add("ChatGPT Ads delivery metrics are temporarily unavailable: " + ex.Message);
            }
        }
        else
        {
            notes.Add("ChatGPT Ads is not connected for this marketing owner.");
        }

        var attributedEvents = await analytics.LoadAttributedEventsAsync(range, analyticsScope, TrafficType.All, ct);
        var openAiEvents = attributedEvents
            .Where(x => OpenAiClickReference.Normalize(x.Oppref) is not null)
            .ToList();

        var openAiLeadIds = openAiEvents
            .Where(x => IsLeadEvent(x.EventType))
            .Select(x => x.ClientEventId ?? x.EventId)
            .Distinct()
            .LongCount();

        var metaSignals = await analytics.LoadScopedMetaEventsAsync(range, analyticsScope, attributedEvents, ct);
        var openAiSignals = metaSignals.Where(HasOppref).ToList();

        var outcomes = new CanonicalOutcomeTotals(
            Leads: openAiLeadIds,
            QualifiedLeads: CountSignals(openAiSignals, "QualifiedLead"),
            Appointments: CountSignals(openAiSignals, "AppointmentBooked", "Schedule", "AppointmentCompleted"),
            Customers: CountSignals(openAiSignals, "PolicyPaid", "Purchase", "OrderCreated"),
            Revenue: openAiSignals
                .Where(x => IsAny(x.EventName, "PolicyPaid", "Purchase", "OrderCreated"))
                .Sum(x => ReadRevenue(x.MetadataJson)));

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
            "verified",
            "Provider delivery + canonical oppref CRM lineage"));

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
        catch (InvalidOperationException ex)
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
                rows.LongCount(x => IsLeadEvent(x.EventType)),
                0, 0, 0, 0, 0,
                "observed",
                "Canonical website attribution events"));
        }
    }

    private static bool HasOppref(Domain.Entities.MetaSignalEvent row)
    {
        if (string.IsNullOrWhiteSpace(row.MetadataJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(row.MetadataJson);
            return FindProperty(doc.RootElement, "oppref") is { } value &&
                   OpenAiClickReference.Normalize(value) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FindProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();

                var nested = FindProperty(property.Value, propertyName);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindProperty(item, propertyName);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static decimal ReadRevenue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "amount", "personalAmount", "revenue", "value", "paidPremium", "orderTotal" })
            {
                var found = FindNumber(doc.RootElement, key);
                if (found.HasValue) return found.Value;
            }
        }
        catch (JsonException) { }
        return 0;
    }

    private static decimal? FindNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var number))
                        return number;
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(property.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                        return number;
                }
                var nested = FindNumber(property.Value, propertyName);
                if (nested.HasValue) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindNumber(item, propertyName);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    private static long CountSignals(
        IEnumerable<Domain.Entities.MetaSignalEvent> rows,
        params string[] names) =>
        rows.LongCount(x => names.Any(name => string.Equals(x.EventName, name, StringComparison.OrdinalIgnoreCase)));

    private static bool IsLeadEvent(string? value) =>
        IsAny(value, "Lead", "LeadCreated", "lead_created", "QuoteSubmitted", "quote_submitted");

    private static bool IsAny(string? value, params string[] candidates) =>
        candidates.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase));

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
