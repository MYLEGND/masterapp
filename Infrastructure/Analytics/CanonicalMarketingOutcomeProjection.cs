using System.Globalization;
using System.Text.Json;
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

    public static string ChannelFor(
        MetaSignalEvent row,
        IReadOnlyDictionary<string, string> sessionChannels,
        IReadOnlyDictionary<string, string> visitorChannels)
    {
        if (ContainsTextProperty(row.MetadataJson, "oppref", out var oppref) &&
            OpenAiClickReference.Normalize(oppref) is not null)
            return MarketingChannels.ChatGptAds;

        if (!string.IsNullOrWhiteSpace(row.SessionId) &&
            sessionChannels.TryGetValue(row.SessionId, out var bySession))
            return bySession;

        if (!string.IsNullOrWhiteSpace(row.VisitorId) &&
            visitorChannels.TryGetValue(row.VisitorId, out var byVisitor))
            return byVisitor;

        var source = row.UtmSource?.Trim();
        var medium = row.UtmMedium?.Trim();
        if (source is not null &&
            (source.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("facebook", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("instagram", StringComparison.OrdinalIgnoreCase)))
            return MarketingChannels.MetaAds;

        return TrafficAttribution.Classify(source, medium, row.UtmCampaign, row.FbclidPresent ? "present" : null) switch
        {
            TrafficType.PaidAds => MarketingChannels.Unknown,
            TrafficType.Organic => MarketingChannels.Organic,
            TrafficType.Direct => MarketingChannels.Direct,
            TrafficType.Referral => MarketingChannels.Referral,
            _ => MarketingChannels.Unknown
        };
    }

    public static decimal ReadMoney(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "amount", "personalAmount", "revenue", "value", "paidPremium", "orderTotal" })
            {
                var value = FindNumber(doc.RootElement, key);
                if (value.HasValue) return value.Value;
            }
        }
        catch (JsonException) { }
        return 0m;
    }

    public static bool IsCustomer(string? eventName) =>
        IsAny(eventName, "PolicyPaid", "Purchase", "OrderCreated");

    public static bool IsPipeline(string? eventName) =>
        IsAny(eventName, "QualifiedLead", "AppointmentBooked", "Schedule", "ApplicationSubmitted",
            "SubmitApplication", "PolicyIssued", "CompleteRegistration");

    private static bool IsAny(string? value, params string[] candidates) =>
        candidates.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsTextProperty(string? json, string name, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            value = FindText(doc.RootElement, name);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException) { return false; }
    }

    private static string? FindText(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindText(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindText(item, name);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static decimal? FindNumber(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var number))
                        return number;
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(property.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                        return number;
                }

                var nested = FindNumber(property.Value, name);
                if (nested.HasValue) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindNumber(item, name);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }
}
