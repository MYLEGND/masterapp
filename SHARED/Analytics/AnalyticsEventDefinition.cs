namespace Shared.Analytics;

public sealed record AnalyticsEventDefinition(
    string Name,
    string Category,
    IReadOnlyList<string> QuoteTypeApplicability,
    string FunnelStage,
    bool CountsAsLandingView,
    bool CountsAsCtaClick,
    bool CountsAsFunnelStart,
    bool CountsAsFormStart,
    bool CountsAsContactStep,
    bool CountsAsSubmitAttempt,
    bool CountsAsConfirmedLead,
    bool EligibleForMetaSignal,
    bool IsCritical,
    bool AllowBrowser,
    bool AllowServer,
    IReadOnlyList<string> DashboardMetrics);
