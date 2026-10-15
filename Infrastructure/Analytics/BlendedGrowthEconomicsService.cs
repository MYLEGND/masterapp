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
    IUnifiedMarketingPerformanceService performance) : IBlendedGrowthEconomicsService
{
    public async Task<BlendedGrowthEconomicsSnapshot> GetAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        CancellationToken ct = default)
    {
        var unified = await performance.GetAsync(owner, analyticsScope, range, ct);
        return unified.Economics ?? throw new InvalidOperationException("Canonical performance economics are unavailable.");
    }

    internal static BlendedGrowthEconomicsSnapshot Calculate(MarketingOwnerScope owner, TimeRangeRequest range,
        UnifiedChannelPerformanceSnapshot unified, IReadOnlyCollection<Domain.Entities.AnalyticsEvent> events)
    {
        var outcomeGroups = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events)
            .Select(x => new
            {
                Row = x,
                Channel = CanonicalMarketingOutcomeProjection.ChannelFor(x)
            })
            .GroupBy(x => x.Channel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Customers = CanonicalMarketingOutcomeProjection.CustomerCount(g.Select(x => x.Row)),
                    Revenue = g.Where(x => CanonicalMarketingOutcomeProjection.IsCustomer(x.Row.EventType))
                        .Sum(x => CanonicalMarketingOutcomeProjection.ReadMoney(x.Row.MetadataJson)),
                    Pipeline = CanonicalMarketingOutcomeProjection.PipelineValue(g.Select(x => x.Row))
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

            var spend = delivery?.Spend;
            var customers = outcome?.Customers ?? delivery?.Customers ?? 0;
            var revenue = outcome?.Revenue ?? delivery?.Revenue ?? 0m;
            var pipeline = outcome?.Pipeline ?? 0m;

            rows.Add(new(
                channel,
                spend,
                customers,
                customers > 0 && spend.HasValue ? Math.Round(spend.Value / customers, 2) : (decimal?)null,
                revenue,
                spend > 0 ? Math.Round(revenue / spend.Value, 2) : (decimal?)null,
                pipeline,
                delivery?.AttributionBasis ?? "Canonical analytics + CRM outcome lineage"));
        }

        rows = rows
            .OrderByDescending(x => x.Spend)
            .ThenByDescending(x => x.Revenue)
            .ThenBy(x => x.Channel)
            .ToList();

        decimal? totalSpend = rows.All(x => x.Spend.HasValue) ? rows.Sum(x => x.Spend) : null;
        var totalCustomers = CanonicalMarketingOutcomeProjection.CustomerCount(CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events));
        var totalRevenue = rows.Sum(x => x.Revenue);
        var totalPipeline = rows.Sum(x => x.PipelineValue);

        var notes = new List<string>(unified.DataQualityNotes)
        {
            "Blended economics uses one canonical scoped outcome stream. Spend comes from connected paid providers; customers, revenue, and pipeline value come from downstream CRM/commerce outcomes attributed to the acquisition channel.",
            "Cost per customer and ROAS are unavailable when required evidence or a valid denominator is missing.",
            "Channel customer counts can overlap; the blended customer total deduplicates across channels."
        };

        return new(
            owner,
            range.FromUtc,
            range.ToUtc,
            totalSpend,
            totalCustomers,
            totalCustomers > 0 && totalSpend.HasValue ? Math.Round(totalSpend.Value / totalCustomers, 2) : (decimal?)null,
            totalRevenue,
            totalSpend > 0 ? Math.Round(totalRevenue / totalSpend.Value, 2) : (decimal?)null,
            totalPipeline,
            rows,
            notes);
    }

}
