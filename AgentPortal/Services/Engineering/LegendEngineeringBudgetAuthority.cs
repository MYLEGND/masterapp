using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed class LegendEngineeringBudgetAuthority(
    LegendEngineeringStateStore store,
    IConfiguration configuration)
{
    internal async Task<EngineeringBudgetEnvelope> GetEnvelopeAsync(CancellationToken cancellationToken)
    {
        var usage = await store.ReadUsageTotalsAsync(DateTime.UtcNow, cancellationToken);
        var maxCodex = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:Budget:MaxCodexAttempts") ?? 3, 1, 10);
        var maxDeep = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:Budget:MaxDeepReasoningEscalations") ?? 2, 1, 10);

        // The engineering runtime has one billing authority: the connected ChatGPT
        // plan. Provider allowance is enforced by the durable plan circuit breaker.
        // There is deliberately no deployment-time mode switch and no API-token
        // ceiling that can silently disable an otherwise connected plan runtime.
        return new(
            true,
            "CHATGPT_PLAN_PROVIDER_ENFORCED",
            null,
            null,
            usage.DailyTokens,
            usage.MonthlyTokens,
            maxCodex,
            maxDeep,
            usage.UsageEvidenceComplete);
    }

    internal bool Permits(EngineeringBudgetEnvelope envelope, EngineeringWorkItemSnapshot item)
    {
        if (!envelope.AiWorkPermitted) return false;
        return envelope.Mode != "P1_P2_ONLY" || item.PriorityClass is "P1" or "P2";
    }

}
