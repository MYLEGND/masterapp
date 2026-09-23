using Shared.Workspaces;

namespace Shared.Analytics;

public enum ScopeType
{
    Global,
    Agent,
    Business
}

public sealed class ScopeContext
{
    public ScopeType ScopeType { get; init; } = ScopeType.Global;
    public Guid? AgentTrackingProfileId { get; init; }
    public Guid? CommerceBusinessId { get; init; }
    public string? SiteKey { get; init; }
    public string? ReportingOwner { get; init; }

    // Invalid/empty scopes intentionally have no owner key. Site/global reports can
    // aggregate owners and must never be treated as a single marketing destination.
    public WorkspaceKey? Workspace => ScopeType switch
    {
        ScopeType.Agent when AgentTrackingProfileId is { } id && id != Guid.Empty && CommerceBusinessId is null
            => WorkspaceKey.ForAgentTrackingProfile(id),
        ScopeType.Business when CommerceBusinessId is { } id && id != Guid.Empty && AgentTrackingProfileId is null
            => WorkspaceKey.ForBusiness(id),
        _ => null
    };

    public bool HasSiteScope =>
        !string.IsNullOrWhiteSpace(SiteKey) ||
        !string.IsNullOrWhiteSpace(ReportingOwner);

    public static ScopeContext ForAgent(Guid agentId) => new() { ScopeType = ScopeType.Agent, AgentTrackingProfileId = agentId };
    public static ScopeContext ForBusiness(Guid businessId) => businessId != Guid.Empty
        ? new() { ScopeType = ScopeType.Business, CommerceBusinessId = businessId }
        : throw new ArgumentException("A permanent business owner is required.", nameof(businessId));
    public static ScopeContext ForSite(string siteKey, string? reportingOwner = null) => new()
    {
        ScopeType = ScopeType.Global,
        SiteKey = siteKey?.Trim(),
        ReportingOwner = reportingOwner?.Trim()
    };

    public static ScopeContext Global => new() { ScopeType = ScopeType.Global };
}
