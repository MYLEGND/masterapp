namespace AgentPortal.Models.Analytics;

public enum TrafficQualityMode
{
    RealHumanTraffic = 0,
    LikelyHuman = 1,
    ReviewedNeeded = 2,
    SuspiciousActivity = 3,
    LikelyBotsAutomation = 4,
    InternalQa = 5,
    AllTraffic = 6,

    // Legacy aliases retained for backwards-compatible enum parsing.
    RealHuman = RealHumanTraffic,
    Review = ReviewedNeeded,
    Suspicious = SuspiciousActivity,
    LikelyBot = LikelyBotsAutomation,
    Internal = InternalQa,
    All = AllTraffic
}
