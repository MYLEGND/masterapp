using System.Globalization;
using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

internal static class CanonicalMarketingOutcomeProjection
{
    public static string ChannelFor(
        AnalyticsEvent row)
    {
        if (OpenAiClickReference.Normalize(row.Oppref) is not null)
            return MarketingChannels.ChatGptAds;

        if (TrafficAttribution.IsMetaAttributedPaid(
            row.UtmSource, row.UtmMedium, row.UtmCampaign, row.Fbclid,
            row.MetaCampaignId, row.MetaAdSetId, row.MetaAdId,
            row.IsInternal, row.Environment, row.Host, row.ReferrerHost))
            return MarketingChannels.MetaAds;

        return TrafficAttribution.Classify(
            row.UtmSource, row.UtmMedium, row.UtmCampaign, row.Fbclid,
            row.ReferrerHost, row.MetaCampaignId, row.MetaAdSetId, row.MetaAdId,
            row.IsInternal, row.Environment, row.Host, row.Oppref) switch
        {
            TrafficType.Organic => MarketingChannels.Organic,
            TrafficType.Direct => MarketingChannels.Direct,
            TrafficType.Referral => MarketingChannels.Referral,
            TrafficType.PaidAds => MarketingChannels.Unknown,
            _ => MarketingChannels.Unknown
        };
    }

    public static IReadOnlyList<AnalyticsEvent> ConfirmedOutcomes(IEnumerable<AnalyticsEvent> events) =>
        events.Where(CanonicalAdvertisingEventProjection.CanProjectServer)
            .DistinctBy(row => (row.AgentTrackingProfileId, row.CommerceBusinessId,
                CanonicalAdvertisingEventProjection.ResolveEventId(row), CanonicalAdvertisingEventProjection.ResolveEventName(row)))
            .ToArray();

    public static string? OutcomeName(AnalyticsEvent row) => CanonicalAdvertisingEventProjection.ResolveEventName(row);

    public static decimal ReadMoney(string? json)
    {
        foreach (var key in new[] { "valueCents", "totalCents", "revenueCents" })
            if (decimal.TryParse(CanonicalAdvertisingEventProjection.ReadString(json, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var cents))
                return cents / 100m;
        foreach (var key in new[] { "amount", "personalAmount", "revenue", "value", "paidPremium", "orderTotal" })
            if (decimal.TryParse(CanonicalAdvertisingEventProjection.ReadString(json, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return value;
        return 0m;
    }

    public static bool IsCustomer(string? eventName) =>
        IsAny(AnalyticsEventCatalog.ResolveConversionEventName(eventName), "PolicyPaid", "Purchase");

    public static bool IsPipeline(string? eventName) =>
        IsAny(AnalyticsEventCatalog.ResolveConversionEventName(eventName), "QualifiedLead", "AppointmentBooked", "ApplicationSubmitted", "PolicyIssued");

    private static bool IsAny(string? value, params string[] candidates) =>
        candidates.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase));
}
