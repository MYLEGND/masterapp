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

    public static IReadOnlyList<AnalyticsEvent> ConfirmedOutcomes(IEnumerable<AnalyticsEvent> events)
    {
        var facts = events.Where(e => CanonicalAdvertisingEventProjection.CanProjectServer(e) ||
            e.TrackingVersion is "crm-production-state-v1" or "crm-qualification-state-v1").ToArray();

        var production = facts.Where(e => !string.IsNullOrWhiteSpace(CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "productionRecordId")))
            .GroupBy(e => (e.AgentTrackingProfileId, e.CommerceBusinessId, Id: CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "productionRecordId")))
            .Select(g => g.OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id).First())
            .Where(e => CanonicalAdvertisingEventProjection.ReadBoolean(e.MetadataJson, "productionDeleted") != true);

        var qualification = facts.Where(e =>
                !string.IsNullOrWhiteSpace(CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "qualificationIdentity")))
            .GroupBy(e => (e.AgentTrackingProfileId, e.CommerceBusinessId,
                Id: CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "qualificationIdentity")))
            .Select(g => g.OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id).First())
            .Where(e => CanonicalAdvertisingEventProjection.ReadBoolean(e.MetadataJson, "qualificationActive") == true);

        var other = facts.Where(e =>
                string.IsNullOrWhiteSpace(CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "productionRecordId")) &&
                string.IsNullOrWhiteSpace(CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "qualificationIdentity")))
            .DistinctBy(e => (e.AgentTrackingProfileId, e.CommerceBusinessId, CanonicalAdvertisingEventProjection.ResolveEventId(e), OutcomeName(e)));

        return production.Concat(qualification).Concat(other).ToArray();
    }

    public static string CustomerIdentity(AnalyticsEvent e) => $"{e.CommerceBusinessId}|{e.AgentTrackingProfileId}|" +
        (CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "clientUserId") ??
         CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "workstationLeadId") ??
         CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "LeadId") ??
         e.VisitorId ?? "unresolved:" + CanonicalAdvertisingEventProjection.ResolveEventId(e));
    public static long CustomerCount(IEnumerable<AnalyticsEvent> events) => events.Where(e => IsCustomer(e.EventType))
        .Select(CustomerIdentity).Where(x => !x.Contains("unresolved:", StringComparison.Ordinal)).Distinct().LongCount();
    private static string OpportunityIdentity(AnalyticsEvent e) => $"{e.CommerceBusinessId}|{e.AgentTrackingProfileId}|" +
        (CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "productionRecordId") ??
         CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "workstationLeadId") ??
         CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "LeadId") ??
         "event:" + CanonicalAdvertisingEventProjection.ResolveEventId(e));
    public static decimal PipelineValue(IEnumerable<AnalyticsEvent> events) => events.GroupBy(OpportunityIdentity)
        .Where(g => !g.Any(e => IsCustomer(e.EventType)))
        .Select(g => g.Where(e => IsPipeline(e.EventType)).OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id).FirstOrDefault())
        .Where(e => e is not null).Sum(e => ReadMoney(e!.MetadataJson));

    public static CanonicalOutcomeTotals Totals(IEnumerable<AnalyticsEvent> events)
    {
        var rows = ConfirmedOutcomes(events);
        return new(
            rows.LongCount(e => OutcomeName(e) == "Lead"),
            rows.LongCount(e => OutcomeName(e) == "QualifiedLead"),
            rows.Where(e => OutcomeName(e) is "AppointmentBooked" or "AppointmentCompleted")
                .Select(e => CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "AppointmentId") ??
                    "event:" + CanonicalAdvertisingEventProjection.ResolveEventId(e)).Distinct().LongCount(),
            CustomerCount(rows),
            rows.Where(e => IsCustomer(e.EventType)).Sum(e => ReadMoney(e.MetadataJson)));
    }

    public static string? OutcomeName(AnalyticsEvent row) => CanonicalAdvertisingEventProjection.ResolveEventName(row);

    public static decimal ReadMoney(string? json) => CanonicalConversionValueProjection.Resolve(json)?.AmountMinorUnits / 100m ?? 0m;

    public static bool IsCustomer(string? eventName) =>
        IsAny(AnalyticsEventCatalog.ResolveConversionEventName(eventName), "PolicyPaid", "Purchase");

    public static bool IsPipeline(string? eventName) =>
        IsAny(AnalyticsEventCatalog.ResolveConversionEventName(eventName), "QualifiedLead", "AppointmentBooked", "ApplicationSubmitted", "PolicyIssued");

    private static bool IsAny(string? value, params string[] candidates) =>
        candidates.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase));
}
