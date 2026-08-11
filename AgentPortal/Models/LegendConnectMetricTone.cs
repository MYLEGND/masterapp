namespace AgentPortal.Models;

/// <summary>
/// Maps already-authoritative Legend Connect snapshot values to presentation-only
/// semantic tones. This class never calculates, persists, or changes operational state.
/// </summary>
public static class LegendConnectMetricTone
{
    public const string Neutral = "legend-connect-metric--neutral";
    public const string Info = "legend-connect-metric--info";
    public const string Success = "legend-connect-metric--success";
    public const string Warning = "legend-connect-metric--warning";
    public const string Danger = "legend-connect-metric--danger";

    public static string BeneficialActivity(long count) => count > 0 ? Success : Neutral;

    public static string InformationalActivity(long count) => count > 0 ? Info : Neutral;

    public static string PendingWork(long count) => count > 0 ? Warning : Success;

    public static string Failure(long count) => count > 0 ? Danger : Success;

    public static string Dependency(decimal rate, long demandCount)
    {
        if (demandCount <= 0)
        {
            return Neutral;
        }

        return rate switch
        {
            >= 0.75m => Danger,
            >= 0.30m => Warning,
            _ => Success
        };
    }

    public static string Avoidance(decimal rate, long routedRequestCount)
    {
        if (routedRequestCount <= 0)
        {
            return Neutral;
        }

        return rate switch
        {
            >= 0.75m => Success,
            >= 0.25m => Warning,
            _ => Danger
        };
    }

    public static string Capacity(long? amount, bool configured = true)
    {
        if (!configured || !amount.HasValue)
        {
            return Danger;
        }

        return amount.Value > 0 ? Success : Danger;
    }

    public static string CapacityLimit(long amount) => amount > 0 ? Info : Danger;

    public static string Expansion(DateTime? timestamp) => timestamp.HasValue ? Success : Neutral;

    public static string Health(string? healthState) => healthState?.Trim().ToUpperInvariant() switch
    {
        "HEALTHY" => Success,
        "WARNING" or "DEGRADED" or "LOW" => Warning,
        "UNHEALTHY" or "CRITICAL" or "BLOCKED" => Danger,
        _ => Neutral
    };

    public static string Quality(string? qualityState) => qualityState?.Trim().ToUpperInvariant() switch
    {
        "VALIDATED" or "VERIFIED" or "TRUSTED" => Success,
        "OBSERVATION" => Neutral,
        "WARNING" or "REVIEW" => Warning,
        "REJECTED" or "BLOCKED" or "INVALID" => Danger,
        _ => Info
    };
}
