namespace AgentPortal.Models;

/// <summary>
/// Centralized feature toggles to enable hardened behaviors without changing defaults.
/// Defaults are false to avoid altering existing logic until explicitly enabled.
/// </summary>
public sealed class AppFeatureFlags
{
    public bool IngestHmacEnabled { get; set; }
    public bool SignalRRequireRedis { get; set; }
    public bool ImportValidatorEnabled { get; set; }
    public bool ResiliencePoliciesEnabled { get; set; }
    public bool DerivedInsightsEnabled { get; set; }
}
