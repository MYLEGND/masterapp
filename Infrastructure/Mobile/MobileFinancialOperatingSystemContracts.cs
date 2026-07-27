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
