namespace Shared.Workspaces;

/// <summary>
/// Stable owner identity shared by application projections. A key identifies a workspace;
/// it never proves that the current actor may access it. Agent IDs here are tracking-profile
/// IDs, not Entra object IDs. Existing persisted marketing keys remain unchanged.
/// </summary>
public sealed record WorkspaceKey
{
    public string Kind { get; }
    public Guid? OwnerId { get; }
    public string Value => OwnerId is { } id ? $"{Kind}:{id:N}" : Kind;

    private WorkspaceKey(string kind, Guid? ownerId)
    {
        Kind = kind;
        OwnerId = ownerId;
    }

    public static WorkspaceKey Founder { get; } = new("founder", null);
    public static WorkspaceKey ForAgentTrackingProfile(Guid id) => Create("agent", id);
    public static WorkspaceKey ForBusiness(Guid id) => Create("business", id);

    private static WorkspaceKey Create(string kind, Guid id) => id != Guid.Empty
        ? new(kind, id)
        : throw new ArgumentException("A permanent workspace owner ID is required.", nameof(id));

    public override string ToString() => Value;
}
