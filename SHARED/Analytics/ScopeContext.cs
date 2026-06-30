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

    public static ScopeContext ForAgent(Guid agentId) => new() { ScopeType = ScopeType.Agent, AgentTrackingProfileId = agentId };
    public static ScopeContext Global => new() { ScopeType = ScopeType.Global };
}
