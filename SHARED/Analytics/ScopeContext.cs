namespace AgentPortal.Models.Analytics;

public enum ScopeType
{
    Global,
    Agent
}

public sealed class ScopeContext
{
    public ScopeType ScopeType { get; init; } = ScopeType.Global;
    public Guid? AgentTrackingProfileId { get; init; }
    public string? SiteKey { get; init; }
    public string? ReportingOwner { get; init; }

    public bool HasSiteScope =>
        !string.IsNullOrWhiteSpace(SiteKey) ||
        !string.IsNullOrWhiteSpace(ReportingOwner);

    public static ScopeContext ForAgent(Guid agentId) => new() { ScopeType = ScopeType.Agent, AgentTrackingProfileId = agentId };
    public static ScopeContext ForSite(string siteKey, string? reportingOwner = null) => new()
    {
        ScopeType = ScopeType.Global,
        SiteKey = siteKey?.Trim(),
        ReportingOwner = reportingOwner?.Trim()
    };

    public static ScopeContext Global => new() { ScopeType = ScopeType.Global };
}
