using Shared.Workspaces;

namespace Shared.Analytics;

/// <summary>Server-resolved ownership. This value is not a browser authorization credential.</summary>
public sealed record MarketingOwnerScope
{
    public WorkspaceKey Workspace { get; }
    public string OwnerType => Workspace.Kind;
    public Guid? AgentTrackingProfileId => OwnerType == "agent" ? Workspace.OwnerId : null;
    public Guid? CommerceBusinessId => OwnerType == "business" ? Workspace.OwnerId : null;
    public string Key => Workspace.Value;

    private MarketingOwnerScope(WorkspaceKey workspace) => Workspace = workspace;

    public static MarketingOwnerScope Founder { get; } = new(WorkspaceKey.Founder);
    public static MarketingOwnerScope Agent(Guid id) => id != Guid.Empty
        ? new(WorkspaceKey.ForAgentTrackingProfile(id)) : throw new ArgumentException("An agent owner is required.", nameof(id));
    public static MarketingOwnerScope Business(Guid id) => id != Guid.Empty
        ? new(WorkspaceKey.ForBusiness(id)) : throw new ArgumentException("A business owner is required.", nameof(id));
}
