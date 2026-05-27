namespace AgentPortal.Models.Analytics;

public sealed class VisitorTrustScoreDto
{
    public int TrustScore { get; init; }
    public string TrustTier { get; init; } = "Review";
    public decimal HumanConfidence { get; init; }
    public List<string> Signals { get; init; } = new();

    public int TotalEvents { get; init; }
    public int Sessions { get; init; }
    public int MaxScroll { get; init; }
    public int FormStarts { get; init; }
    public int CtaClicks { get; init; }

    public decimal AverageSecondsBetweenEvents { get; init; }
    public int BurstEventCount { get; init; }

    public int BehaviorScore { get; init; }
    public int IntentScore { get; init; }
    public int EngagementScore { get; init; }
    public int FrictionScore { get; init; }
    public int LeadReadinessScore { get; init; }
}
