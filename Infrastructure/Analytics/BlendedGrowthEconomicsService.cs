using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IBlendedGrowthEconomicsService
{
    Task<BlendedGrowthEconomicsSnapshot> GetAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        CancellationToken ct = default);
}

public sealed class BlendedGrowthEconomicsService(
    IUnifiedMarketingPerformanceService performance,
    IAnalyticsQueryService analytics) : IBlendedGrowthEconomicsService
{
    public async Task<BlendedGrowthEconomicsSnapshot> GetAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        CancellationToken ct = default)
    {
        var unified = await performance.GetAsync(owner, analyticsScope, range, ct);
        var events = await analytics.LoadAttributedEventsAsync(range, analyticsScope, TrafficType.All, ct);
        var signals = await analytics.LoadScopedMetaEventsAsync(range, analyticsScope, events, ct);

        var sessionChannels = events
            .Where(x => !string.IsNullOrWhiteSpace(x.SessionId))
            .GroupBy(x => x.SessionId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => PickChannel(g.Select(CanonicalMarketingOutcomeProjection.ChannelFor)),
                StringComparer.OrdinalIgnoreCase);

        var visitorChannels = events
            .Where(x => !string.IsNullOrWhiteSpace(x.VisitorId))
            .GroupBy(x => x.VisitorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => PickChannel(g.Select(CanonicalMarketingOutcomeProjection.ChannelFor)),
                StringComparer.OrdinalIgnoreCase);

        var outcomeGroups = signals
            .Select(x => new
            {
                Row = x,
                Channel = CanonicalMarketingOutcomeProjection.ChannelFor(x, sessionChannels, visitorChannels)
            })
            .GroupBy(x => x.Channel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Customers = g.LongCount(x => CanonicalMarketingOutcomeProjection.IsCustomer(x.Row.EventName)),
                    Revenue = g.Where(x => CanonicalMarketingOutcomeProjection.IsCustomer(x.Row.EventName))
                        .Sum(x => CanonicalMarketingOutcomeProjection.ReadMoney(x.Row.MetadataJson)),
                    Pipeline = g.Where(x => CanonicalMarketingOutcomeProjection.IsPipeline(x.Row.EventName))
                        .Sum(x => CanonicalMarketingOutcomeProjection.ReadMoney(x.Row.MetadataJson))
                },
                StringComparer.OrdinalIgnoreCase);

        var allChannels = unified.Channels.Select(x => x.Channel)
            .Concat(outcomeGroups.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<ChannelEconomicsRow>();
        foreach (var channel in allChannels)
        {
            var delivery = unified.Channels.FirstOrDefault(x =>
                string.Equals(x.Channel, channel, StringComparison.OrdinalIgnoreCase));
            outcomeGroups.TryGetValue(channel, out var outcome);

            var spend = delivery?.Spend ?? 0m;
            var customers = outcome?.Customers ?? delivery?.Customers ?? 0;
            var revenue = outcome?.Revenue ?? delivery?.Revenue ?? 0m;
            var pipeline = outcome?.Pipeline ?? 0m;

            rows.Add(new(
                channel,
                spend,
                customers,
                customers > 0 ? Math.Round(spend / customers, 2) : 0m,
                revenue,
                spend > 0 ? Math.Round(revenue / spend, 2) : 0m,
                pipeline,
                delivery?.AttributionBasis ?? "Canonical analytics + CRM outcome lineage"));
        }

        rows = rows
            .OrderByDescending(x => x.Spend)
            .ThenByDescending(x => x.Revenue)
            .ThenBy(x => x.Channel)
            .ToList();

        var totalSpend = rows.Sum(x => x.Spend);
        var totalCustomers = rows.Sum(x => x.CustomersAcquired);
        var totalRevenue = rows.Sum(x => x.Revenue);
        var totalPipeline = rows.Sum(x => x.PipelineValue);

        var notes = new List<string>(unified.DataQualityNotes)
        {
            "Blended economics uses one canonical scoped outcome stream. Spend comes from connected paid providers; customers, revenue, and pipeline value come from downstream CRM/commerce outcomes attributed to the acquisition channel.",
            "Cost per customer is zero when a channel has no acquired customers in the selected range; it is not an estimate."
        };

        return new(
            owner,
            range.FromUtc,
            range.ToUtc,
            totalSpend,
            totalCustomers,
            totalCustomers > 0 ? Math.Round(totalSpend / totalCustomers, 2) : 0m,
            totalRevenue,
            totalSpend > 0 ? Math.Round(totalRevenue / totalSpend, 2) : 0m,
            totalPipeline,
            rows,
            notes);
    }

    private static string PickChannel(IEnumerable<string> channels)
    {
        var values = channels.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Contains(MarketingChannels.ChatGptAds, StringComparer.OrdinalIgnoreCase)) return MarketingChannels.ChatGptAds;
        if (values.Contains(MarketingChannels.MetaAds, StringComparer.OrdinalIgnoreCase)) return MarketingChannels.MetaAds;
        if (values.Contains(MarketingChannels.Organic, StringComparer.OrdinalIgnoreCase)) return MarketingChannels.Organic;
        if (values.Contains(MarketingChannels.Referral, StringComparer.OrdinalIgnoreCase)) return MarketingChannels.Referral;
        if (values.Contains(MarketingChannels.Direct, StringComparer.OrdinalIgnoreCase)) return MarketingChannels.Direct;
        return MarketingChannels.Unknown;
    }
}
