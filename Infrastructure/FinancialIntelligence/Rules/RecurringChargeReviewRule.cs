using Domain.FinancialIntelligence;

namespace Infrastructure.FinancialIntelligence.Rules;

/// <summary>
/// Surfaces high-confidence, unlinked recurring charges for review. It never
/// infers that a charge is unnecessary or recommends cancelling it.
/// </summary>
public sealed class RecurringChargeReviewRule : IFinancialIntelligenceRule
{
    public const string RuleIdentifier = "RecurringChargeReview";

    public string Identifier => RuleIdentifier;

    public int Version => 1;

    public FinancialIntelligenceRuleResult Evaluate(FinancialIntelligenceRuleContext context)
    {
        var currenciesByAccountId = context.ImportedAccounts
            .Where(account => !string.IsNullOrWhiteSpace(account.CurrencyCode))
            .GroupBy(account => account.Id)
            .ToDictionary(group => group.Key, group => group.First().CurrencyCode.Trim(), EqualityComparer<Guid>.Default);
        var candidates = context.RecurringStreams
            .Where(stream =>
                string.Equals(stream.Status, "Candidate", StringComparison.OrdinalIgnoreCase) &&
                stream.Confidence >= 0.80m &&
                !context.ExpenseLensLinks.Any(link =>
                    link.RecurringFinancialStreamId == stream.Id &&
                    string.Equals(link.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(stream => stream.StreamKey, StringComparer.Ordinal)
            .ToList();

        var observations = candidates
            .Select(stream =>
            {
                var currency = CurrencyFor(stream, currenciesByAccountId);
                return new FinancialIntelligenceObservationCandidate(
                ObservationKey: $"{Identifier}:{stream.Id:N}",
                ObservationType: "RecurringChargeDetected",
                SourceType: "RecurringFinancialStream",
                SourceReference: stream.Id.ToString("N"),
                PeriodStartUtc: stream.FirstSeenUtc,
                PeriodEndUtc: stream.LastSeenUtc,
                NumericValue: stream.AverageAmountCents / 100m,
                PreviousValue: null,
                Unit: $"{currency} per {stream.Cadence}",
                Confidence: stream.Confidence,
                EvidenceSummary: $"A stable {stream.Cadence.ToLowerInvariant()} recurring pattern was detected from imported financial transactions.");
            })
            .ToList();

        var findings = candidates
            .Select(stream =>
            {
                var currency = CurrencyFor(stream, currenciesByAccountId);
                var amount = stream.AverageAmountCents / 100m;
                return new FinancialIntelligenceFindingCandidate(
                FindingKey: $"{Identifier}:{stream.Id:N}",
                Category: FinancialFindingCategories.Opportunity,
                FindingType: "RecurringChargeReview",
                Title: $"Review recurring charge: {stream.DisplayName}",
                Explanation: $"A recurring {stream.Cadence.ToLowerInvariant()} charge of {amount:N2} {currency} was detected. Review it against your current plan before making any change.",
                EstimatedImpact: amount,
                ImpactUnit: $"{currency} per {stream.Cadence}",
                Confidence: stream.Confidence,
                Urgency: "Low",
                Difficulty: "Review",
                EvidenceSummary: $"Recurring pattern detected from imported transactions between {stream.FirstSeenUtc:yyyy-MM-dd} and {stream.LastSeenUtc:yyyy-MM-dd}.",
                ClientFacingSummary: $"Review this recurring charge against your current financial plan.",
                AgentFacingSummary: $"Recurring charge is not yet linked to an Expense Lens item; discuss whether it remains intentional.",
                Disclaimer: "This is a recurring-charge review, not a recommendation to cancel a service or change your financial plan.",
                RequiresAgentReview: false,
                ObservationKeys: new[] { $"{Identifier}:{stream.Id:N}" });
            })
            .ToList();

        return new FinancialIntelligenceRuleResult(
            CanReconcile: context.Connections.Count > 0 || context.RecurringStreams.Count > 0,
            Observations: observations,
            Findings: findings);
    }

    private static string CurrencyFor(
        Domain.Entities.FinancialIntelligence.RecurringFinancialStream stream,
        IReadOnlyDictionary<Guid, string> currenciesByAccountId)
    {
        return stream.ImportedFinancialAccountId is Guid accountId &&
               currenciesByAccountId.TryGetValue(accountId, out var currency)
            ? currency
            : "account currency";
    }
}
