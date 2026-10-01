using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed class LegendEngineeringBudgetAuthority(
    LegendEngineeringStateStore store,
    IConfiguration configuration)
{
    internal async Task<EngineeringBudgetEnvelope> GetEnvelopeAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var usage = await store.ReadUsageTotalsAsync(now, cancellationToken);
        var dailyCeiling = ReadPositiveLong("LegendEngineering:Budget:DailyTokenCeiling");
        var monthlyCeiling = ReadPositiveLong("LegendEngineering:Budget:MonthlyTokenCeiling");
        var maxCodex = Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Budget:MaxCodexAttempts") ?? 3, 1, 10);
        var maxDeep = Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Budget:MaxDeepReasoningEscalations") ?? 2, 1, 10);
        var billingMode = configuration["LegendEngineering:Budget:Mode"]?.Trim();

        // ChatGPT-plan remaining allowance is enforced by the participating product
        // and is not fabricated from API token accounting. Local attempt ceilings
        // still bound retries; the adapter stops when the plan provider rejects work.
        if (string.Equals(billingMode, "chatgpt_plan", StringComparison.OrdinalIgnoreCase))
            return new(true, "CHATGPT_PLAN_PROVIDER_ENFORCED", null, null,
                usage.DailyTokens, usage.MonthlyTokens, maxCodex, maxDeep, usage.UsageEvidenceComplete);

        if (dailyCeiling is null && monthlyCeiling is null)
            return new(false, "NOT_CONFIGURED", null, null, usage.DailyTokens, usage.MonthlyTokens,
                maxCodex, maxDeep, usage.UsageEvidenceComplete);

        if (!usage.UsageEvidenceComplete)
            return new(false, "USAGE_EVIDENCE_INCOMPLETE", dailyCeiling, monthlyCeiling,
                usage.DailyTokens, usage.MonthlyTokens, maxCodex, maxDeep, false);

        var dailyRatio = Ratio(usage.DailyTokens, dailyCeiling);
        var monthlyRatio = Ratio(usage.MonthlyTokens, monthlyCeiling);
        var ratio = Math.Max(dailyRatio, monthlyRatio);
        var mode = ratio >= 1.0 ? "STOP_NEW_AI_WORK"
            : ratio >= 0.90 ? "P1_P2_ONLY"
            : ratio >= 0.70 ? "ECONOMY_TRIAGE"
            : "NORMAL";
        return new(ratio < 1.0, mode, dailyCeiling, monthlyCeiling,
            usage.DailyTokens, usage.MonthlyTokens, maxCodex, maxDeep, true);
    }

    internal bool Permits(EngineeringBudgetEnvelope envelope, EngineeringWorkItemSnapshot item)
    {
        if (!envelope.AiWorkPermitted) return false;
        return envelope.Mode != "P1_P2_ONLY" || item.PriorityClass is "P1" or "P2";
    }

    private long? ReadPositiveLong(string key)
    {
        var value = configuration.GetValue<long?>(key);
        return value is > 0 ? value : null;
    }

    private static double Ratio(long observed, long? ceiling)
        => ceiling is > 0 ? Math.Clamp(observed / (double)ceiling.Value, 0, 10) : 0;
}
