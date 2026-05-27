namespace AgentPortal.Models.Analytics;

public sealed record VisitorConcentrationPayload(
    int TotalVisitors,
    int TotalEvents,
    int VisitorsOneSession,
    int VisitorsTwoPlusSessions,
    int VisitorsFivePlusSessions,
    int LikelyInternalVisitors,
    decimal InternalEventShare,
    int TopVisitorEvents,
    decimal TopVisitorShare,
    List<VisitorConcentrationRow> Rows);

public sealed record VisitorConcentrationRow(
    string VisitorId,
    string VisitorShortId,
    int Sessions,
    int Events,
    string FirstSeenLocal,
    string LastSeenLocal,
    string TopPage,
    string Source,
    string Medium,
    string Campaign,
    string Device,
    string Browser,
    string OperatingSystem,
    string TimeZone,
    string Language,
    int InternalEvents,
    bool LikelyInternal);
