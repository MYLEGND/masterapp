namespace Infrastructure.Mobile;

/// <summary>
/// Read-only mobile projection of the existing ClientApp financial operating
/// system. These contracts never own editable financial state and all money
/// values use integer cents to preserve exact server-authoritative amounts.
/// </summary>
public sealed record MobileFinancialOperatingSystemSnapshot(
    MobileFinancialProjectionStatus Projection,
    MobileFinancialDataFreshness Freshness,
    MobileFinancialWeekAtGlance? WeekAtGlance,
    MobileFinancialMonthAtGlance? MonthAtGlance,
    IReadOnlyList<MobileFinancialToolSummary> Tools);

/// <summary>
/// Communicates whether the server was able to produce the projection without
/// forcing the mobile client to infer completeness or calculate fallbacks.
/// </summary>
public sealed record MobileFinancialProjectionStatus(
    string Status,
    string? ReasonCode,
    string? Summary);

/// <summary>
/// Identifies the authoritative persisted state used by the projection.
/// </summary>
public sealed record MobileFinancialDataFreshness(
    DateTime? FinanceStateUpdatedUtc,
    DateTime? IntelligenceEvaluatedUtc,
    DateTime GeneratedUtc);

/// <summary>
/// Server-authoritative view of the client's current financial week.
/// </summary>
public sealed record MobileFinancialWeekAtGlance(
    string WeekKey,
    DateOnly StartDate,
    DateOnly EndDate,
    long OpeningCashCents,
    long IncomeCents,
    long DebitExpenseCents,
    long CreditExpenseCents,
    long RequiredDebtPaymentCents,
    long ExtraDebtPaymentCents,
    long EndingCashCents,
    long OpeningDebtCents,
    long EndingDebtCents,
    string PressureStatus,
    string? PressureSummary,
    IReadOnlyList<MobileFinancialCashFlowEvent> Events);

/// <summary>
/// Server-authoritative view of the selected calendar month.
/// </summary>
public sealed record MobileFinancialMonthAtGlance(
    string MonthKey,
    DateOnly StartDate,
    DateOnly EndDate,
    long OpeningCashCents,
    long IncomeCents,
    long DebitExpenseCents,
    long CreditExpenseCents,
    long RequiredDebtPaymentCents,
    long ExtraDebtPaymentCents,
    long EndingCashCents,
    long OpeningDebtCents,
    long EndingDebtCents,
    long SavingsContributionCents,
    string PressureStatus,
    string? PressureSummary,
    MobileFinancialLargestObligation? LargestObligation,
    IReadOnlyList<MobileFinancialWeekSummary> Weeks);

/// <summary>
/// One dated cash-flow fact already derived by the authoritative projection.
/// The mobile app displays this event but does not reschedule or recalculate it.
/// </summary>
public sealed record MobileFinancialCashFlowEvent(
    string EventKey,
    DateOnly OccursOn,
    string Kind,
    string Title,
    long AmountCents,
    string? SourceToolId,
    string? SourceItemId,
    string Status);

/// <summary>
/// Compact week entry used inside Month at a Glance.
/// </summary>
public sealed record MobileFinancialWeekSummary(
    string WeekKey,
    DateOnly StartDate,
    DateOnly EndDate,
    long IncomeCents,
    long OutflowCents,
    long EndingCashCents,
    long EndingDebtCents,
    string PressureStatus);

public sealed record MobileFinancialLargestObligation(
    string Title,
    DateOnly OccursOn,
    long AmountCents,
    string Kind);

/// <summary>
/// Presentation metadata for the native Financial Intelligence dashboard.
/// It is derived from the same read-only financial projection returned to the
/// mobile client and never owns financial calculations or editable state.
/// </summary>
public sealed record MobileFinancialPresentation(
    MobileFinancialAssignedAgentContext AssignedAgent,
    IReadOnlyList<MobileFinancialPrioritySection> PrioritySections);

/// <summary>
/// Safe assigned-agent context resolved from the current client's persisted
/// AgentClients relationship and matching active AgentProfile only.
/// </summary>
public sealed record MobileFinancialAssignedAgentContext(
    bool HasAssignedAgent,
    string? DisplayName,
    string? FirstName);

/// <summary>
/// One compact, server-ranked Financial Intelligence destination. The native
/// client renders this metadata without recomputing financial priorities.
/// </summary>
public sealed record MobileFinancialPrioritySection(
    string Key,
    string Eyebrow,
    string Title,
    string SystemImage,
    int Priority,
    string Status,
    string Reason,
    string DiscussionPrompt,
    MobileFinancialSummaryMetric PrimaryMetric,
    MobileFinancialSummaryMetric? SecondaryMetric);

/// <summary>
/// A factual, display-ready metric. Monetary values remain integer cents and
/// the semantic value selects an existing native design-system tone.
/// </summary>
public sealed record MobileFinancialSummaryMetric(
    string Label,
    long? AmountCents,
    DateOnly? Date,
    string? TextValue,
    string Semantic);

/// <summary>
/// Read-only navigation and summary metadata for one verified ClientApp
/// finance tool. Tool details remain server-authoritative.
/// </summary>
public sealed record MobileFinancialToolSummary(
    string ToolId,
    string Title,
    string Category,
    int Priority,
    string AvailabilityStatus,
    DateTime? UpdatedUtc,
    string? Summary,
    IReadOnlyList<MobileFinancialMetric> Metrics);

public sealed record MobileFinancialMetric(
    string Key,
    string Label,
    string ValueType,
    long? AmountCents,
    decimal? NumericValue,
    string? TextValue,
    string? Status);
