using System.Text.Json;
using Domain.FinancialIntelligence;
using Shared.Finance;

namespace Infrastructure.FinancialIntelligence.Rules;

/// <summary>
/// Uses the existing Living Balance Sheet calculator unchanged and only raises
/// an agent-review finding when its completed cash-flow inputs calculate a
/// negative annual lifestyle remainder.
/// </summary>
public sealed class CashFlowShortfallRule : IFinancialIntelligenceRule
{
    public const string RuleIdentifier = "LivingBalanceSheetCashFlowShortfall";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string Identifier => RuleIdentifier;

    public int Version => 1;

    public FinancialIntelligenceRuleResult Evaluate(FinancialIntelligenceRuleContext context)
    {
        if (context.LivingBalanceSheetState == null ||
            string.IsNullOrWhiteSpace(context.LivingBalanceSheetState.JsonState))
        {
            return Empty(canReconcile: false);
        }

        LegendLivingBalanceSheetState? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(
                context.LivingBalanceSheetState.JsonState,
                JsonOptions);
        }
        catch (JsonException)
        {
            return Empty(canReconcile: false);
        }

        if (parsed == null)
            return Empty(canReconcile: false);

        var calculated = LegendLivingBalanceSheetCalculator.Calculate(parsed);
        var annualEarnings = calculated.CashFlow.Earnings;
        var annualRemainder = calculated.CashFlow.LifestyleRemaining;

        // A blank/incomplete planning state does not produce or resolve a finding.
        if (annualEarnings <= 0m)
            return Empty(canReconcile: false);

        if (annualRemainder >= 0m)
            return Empty(canReconcile: true);

        var shortfall = Math.Abs(annualRemainder);
        var urgency = shortfall >= annualEarnings * 0.25m ? "High" : "Medium";
        const string observationKey = RuleIdentifier + ":AnnualLifestyleRemaining";
        var stateUpdatedUtc = context.LivingBalanceSheetState.UpdatedUtc;

        return new FinancialIntelligenceRuleResult(
            CanReconcile: true,
            Observations:
            [
                new FinancialIntelligenceObservationCandidate(
                    ObservationKey: observationKey,
                    ObservationType: "AnnualCashFlowShortfall",
                    SourceType: "FinanceToolState",
                    SourceReference: LegendLivingBalanceSheetConstants.ToolId,
                    PeriodStartUtc: stateUpdatedUtc,
                    PeriodEndUtc: context.EvaluatedUtc,
                    NumericValue: annualRemainder,
                    PreviousValue: null,
                    Unit: "planning currency per year",
                    Confidence: 0.90m,
                    EvidenceSummary: "The existing Living Balance Sheet calculation reports a negative annual lifestyle remainder after earnings, insurance costs, planned savings, debt obligations, and calculated taxes.")
            ],
            Findings:
            [
                new FinancialIntelligenceFindingCandidate(
                    FindingKey: RuleIdentifier + ":AnnualLifestyleRemaining",
                    Category: FinancialFindingCategories.Risk,
                    FindingType: "AnnualCashFlowShortfall",
                    Title: "Cash-flow shortfall needs review",
                    Explanation: "The Living Balance Sheet calculates a negative annual lifestyle remainder. Review the underlying inputs and priorities with your agent before making financial changes.",
                    EstimatedImpact: shortfall,
                    ImpactUnit: "planning currency per year",
                    Confidence: 0.90m,
                    Urgency: urgency,
                    Difficulty: "Review",
                    EvidenceSummary: $"Annual earnings entered in the Living Balance Sheet: {annualEarnings:N2}; calculated annual lifestyle remainder: {annualRemainder:N2}.",
                    ClientFacingSummary: "A cash-flow result needs review with your servicing agent.",
                    AgentFacingSummary: "The Living Balance Sheet calculates a negative annual lifestyle remainder; verify inputs and discuss appropriate next steps with the client.",
                    Disclaimer: "This is an educational planning signal based on entered Living Balance Sheet data. It is not tax, investment, credit, insurance, or legal advice.",
                    RequiresAgentReview: true,
                    ObservationKeys: [observationKey])
            ]);
    }

    private static FinancialIntelligenceRuleResult Empty(bool canReconcile) =>
        new(canReconcile, Array.Empty<FinancialIntelligenceObservationCandidate>(), Array.Empty<FinancialIntelligenceFindingCandidate>());
}
