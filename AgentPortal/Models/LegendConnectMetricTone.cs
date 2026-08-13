namespace AgentPortal.Models;

/// <summary>
/// Maps already-authoritative Legend Connect snapshot values to presentation-only
/// semantic tones. This class never calculates, persists, or changes operational state.
/// </summary>
public static class LegendConnectMetricTone
{
    public const string Neutral = "legend-connect-metric--neutral";
    public const string Info = "legend-connect-metric--info";
    public const string Authority = "legend-connect-metric--authority";
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
        "VALIDATED" or "VERIFIED" or "TRUSTED" or "SUPPORTED" => Success,
        "OBSERVATION" => Info,
        "WARNING" or "REVIEW" or "NEEDSREVIEW" or "INSUFFICIENT" or "PENDING" => Warning,
        "REJECTED" or "BLOCKED" or "INVALID" or "SUPERSEDED" or "FAILED" => Danger,
        _ => Info
    };

    public static string Maturity(string? maturityState) => Quality(maturityState);

    public static string Provenance(string? provenance, bool humanVerified = false)
    {
        if (humanVerified)
        {
            return Authority;
        }

        return provenance?.Trim().ToUpperInvariant() switch
        {
            "FOUNDERAPPROVED" or "HUMANVERIFIED" => Authority,
            "PROVIDERDERIVED" or "AZURETRANSLATOR" or "CONSENTEDLIVETRANSLATION" => Info,
            "LEGACY" or "IMPORTED" => Warning,
            _ => Neutral
        };
    }

    public static string Confidence(decimal? confidence) => confidence switch
    {
        null => Neutral,
        >= 0.98m => Success,
        >= 0.85m => Info,
        >= 0.60m => Warning,
        _ => Danger
    };

    public static string Evidence(int count) => count switch
    {
        >= 2 => Success,
        > 0 => Info,
        _ => Warning
    };

    public static string Contradictions(int count) => count > 0 ? Danger : Success;

    public static string ProductionEligibility(bool eligible) => eligible ? Success : Warning;

    public static string Verification(bool humanVerified) => humanVerified ? Authority : Warning;

    public static string Lifecycle(string? state)
    {
        var normalized = state?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized is "NONE" or "NOTPROCESSED")
        {
            return Neutral;
        }

        if (normalized.Contains("FAIL", StringComparison.Ordinal) ||
            normalized.Contains("REJECT", StringComparison.Ordinal) ||
            normalized.Contains("BLOCK", StringComparison.Ordinal) ||
            normalized.Contains("ERROR", StringComparison.Ordinal) ||
            normalized.Contains("DENIED", StringComparison.Ordinal) ||
            normalized.Contains("INELIGIBLE", StringComparison.Ordinal))
        {
            return Danger;
        }

        if (normalized.Contains("PENDING", StringComparison.Ordinal) ||
            normalized.Contains("PROCESSING", StringComparison.Ordinal) ||
            normalized.Contains("AWAIT", StringComparison.Ordinal) ||
            normalized.Contains("QUEUED", StringComparison.Ordinal) ||
            normalized.Contains("REVIEW", StringComparison.Ordinal) ||
            normalized.Contains("HOLD", StringComparison.Ordinal))
        {
            return Warning;
        }

        if (normalized.Contains("OBSERV", StringComparison.Ordinal))
        {
            return Info;
        }

        return normalized.Contains("PROMOT", StringComparison.Ordinal) ||
               normalized.Contains("PROCESSED", StringComparison.Ordinal) ||
               normalized.Contains("COMPLETE", StringComparison.Ordinal) ||
               normalized.Contains("APPROVED", StringComparison.Ordinal) ||
               normalized.Contains("ELIGIBLE", StringComparison.Ordinal) ||
               normalized.Contains("SUCCEED", StringComparison.Ordinal) ||
               normalized.Contains("ACTIVE", StringComparison.Ordinal)
            ? Success
            : Info;
    }
}
