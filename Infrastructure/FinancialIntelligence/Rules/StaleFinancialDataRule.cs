using Domain.FinancialIntelligence;

namespace Infrastructure.FinancialIntelligence.Rules;

/// <summary>Identifies active financial connections whose data is no longer current.</summary>
public sealed class StaleFinancialDataRule : IFinancialIntelligenceRule
{
    public const string RuleIdentifier = "StaleFinancialData";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    public string Identifier => RuleIdentifier;

    public int Version => 1;

    public FinancialIntelligenceRuleResult Evaluate(FinancialIntelligenceRuleContext context)
    {
        var staleBefore = context.EvaluatedUtc.Subtract(StaleAfter);
        var candidates = context.Connections
            .Where(connection =>
                string.Equals(connection.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                (connection.LastSyncCompletedUtc == null
                    ? connection.CreatedUtc <= staleBefore
                    : connection.LastSyncCompletedUtc <= staleBefore))
            .OrderBy(connection => connection.Id)
            .ToList();

        var observations = candidates
            .Select(connection => new FinancialIntelligenceObservationCandidate(
                ObservationKey: $"{Identifier}:{connection.Id:N}",
                ObservationType: "FinancialDataStale",
                SourceType: "FinancialDataConnection",
                SourceReference: connection.Id.ToString("N"),
                PeriodStartUtc: connection.LastSyncCompletedUtc ?? connection.CreatedUtc,
                PeriodEndUtc: context.EvaluatedUtc,
                NumericValue: null,
                PreviousValue: null,
                Unit: null,
                Confidence: 1m,
                EvidenceSummary: connection.LastSyncCompletedUtc is null
                    ? "The active financial connection has not completed an initial sync."
                    : $"The active financial connection has not completed a sync since {connection.LastSyncCompletedUtc:yyyy-MM-dd}."))
            .ToList();

        var findings = candidates
            .Select(connection => new FinancialIntelligenceFindingCandidate(
                FindingKey: $"{Identifier}:{connection.Id:N}",
                Category: FinancialFindingCategories.Information,
                FindingType: "FinancialDataStale",
                Title: "Financial data needs a refresh",
                Explanation: connection.LastSyncCompletedUtc is null
                    ? "An active financial connection has not completed its initial sync, so current analysis may be incomplete."
                    : "An active financial connection has not completed a recent sync, so current analysis may be incomplete.",
                EstimatedImpact: null,
                ImpactUnit: null,
                Confidence: 1m,
                Urgency: "Medium",
                Difficulty: "Quick",
                EvidenceSummary: connection.LastSyncCompletedUtc is null
                    ? "No completed sync has been recorded for this active connection."
                    : $"Last completed sync: {connection.LastSyncCompletedUtc:yyyy-MM-dd}.",
                ClientFacingSummary: "Refresh your financial connection to improve data coverage.",
                AgentFacingSummary: "Data coverage is limited until the client refreshes this active financial connection.",
                Disclaimer: "This notice addresses data freshness only; it does not make a financial recommendation.",
                RequiresAgentReview: false,
                ObservationKeys: new[] { $"{Identifier}:{connection.Id:N}" }))
            .ToList();

        return new FinancialIntelligenceRuleResult(
            CanReconcile: context.Connections.Count > 0,
            Observations: observations,
            Findings: findings);
    }
}
