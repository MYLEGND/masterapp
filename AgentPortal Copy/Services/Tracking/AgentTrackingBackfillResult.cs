namespace AgentPortal.Services.Tracking;

public sealed class AgentTrackingBackfillResult
{
    public List<(string AgentUserId, string Slug)> Created { get; } = new();
    public int SkippedExisting { get; set; }
}
