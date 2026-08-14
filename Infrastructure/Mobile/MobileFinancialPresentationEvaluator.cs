using Domain.FinancialIntelligence;

namespace Infrastructure.Mobile;

/// <summary>
/// The single priority authority for the native Financial Intelligence
/// dashboard. It ranks read-only facts already calculated by the authenticated
/// account's ClientApp or AgentPortal finance services; it does not perform a
/// second financial calculation.
/// </summary>
public static class MobileFinancialPresentationEvaluator
{
    private const int ImmediateAttention = 1;
    private const int HighImportance = 2;
    private const int Planning = 3;
    private const int Informational = 4;

    public static MobileFinancialPresentation Evaluate(
        MobileFinancialPosition? position,
        MobileFinancialIntelligenceSummary? intelligence,
        IReadOnlyList<MobileUpcomingBill> upcomingBills,
        MobileFinancialOperatingSystemSnapshot? operatingSystem)
    {
        var sections = new List<(MobileFinancialPrioritySection Section, int TieBreaker)>();

        if (operatingSystem is null ||
            !string.Equals(
                operatingSystem.Projection.Status,
                "Available",
                StringComparison.OrdinalIgnoreCase))
        {
            sections.Add((
                CreateSection(
                    key: "data-attention",
                    eyebrow: "Data needing attention",
                    title: "Expense Lens projection needs review",
                    systemImage: "exclamationmark.triangle.fill",
                    priority: ImmediateAttention,
                    status: "Data incomplete",
                    reason: operatingSystem?.Projection.Summary
                        ?? "A current Expense Lens projection is not available.",
                    primaryMetric: TextMetric(
                        "Projection",
                        "Not available",
                        "caution"),
                    secondaryMetric: null),
                0));
        }

        if (position is not null)
        {
            var severeHealth = position.HealthScore < 35 ||
                ContainsAny(
                    position.PositionStatus,
                    "critical",
                    "severe",
                    "danger");
            var negativeNetWorth = position.NetWorth < 0;
            var positionPriority = severeHealth
                ? ImmediateAttention
                : negativeNetWorth
                    ? HighImportance
                    : Informational;
            var positionStatus = severeHealth
                ? "Needs attention"
                : negativeNetWorth
                    ? "Review"
                    : "Current";

            sections.Add((
                CreateSection(
                    key: "financial-position",
                    eyebrow: "Financial position",
                    title: "Balance Sheet",
                    systemImage: "building.columns.fill",
                    priority: positionPriority,
                    status: positionStatus,
                    reason: $"Saved financial health information reports a {position.PositionStatus} position.",
                    primaryMetric: AmountMetric(
                        "Net worth",
                        ToCents(position.NetWorth),
                        position.NetWorth < 0 ? "negative" : position.NetWorth > 0 ? "positive" : "neutral"),
                    secondaryMetric: AmountMetric(
                        "Liabilities",
                        ToCents(position.LiabilitiesTotal),
                        "caution")),
                40));

            if (position.ProtectionGapTotal > 0)
            {
                sections.Add((
                    CreateSection(
                        key: "protection-discussion",
                        eyebrow: "Protection discussion",
                        title: "Saved protection information",
                        systemImage: "shield.lefthalf.filled",
                        priority: severeHealth ? HighImportance : Planning,
                        status: "Review",
                        reason: "Saved financial health information includes an open protection amount.",
                        primaryMetric: AmountMetric(
                            "Open amount",
                            ToCents(position.ProtectionGapTotal),
                            "caution"),
                        secondaryMetric: null),
                    50));
            }
        }

        if (operatingSystem?.WeekAtGlance is { } week)
        {
            var scheduledOutflow = week.DebitExpenseCents +
                week.CreditExpenseCents +
                week.RequiredDebtPaymentCents +
                week.ExtraDebtPaymentCents;
            var negativeEndingCash = week.EndingCashCents < 0;
            var cashPressure = negativeEndingCash || ContainsAny(
                week.PressureStatus,
                "critical",
                "negative",
                "pressure",
                "tight");

            sections.Add((
                CreateSection(
                    key: "current-outlook",
                    eyebrow: "Current outlook",
                    title: "Week at a Glance",
                    systemImage: "calendar.day.timeline.leading",
                    priority: negativeEndingCash ? ImmediateAttention : cashPressure ? HighImportance : Planning,
                    status: negativeEndingCash ? "Projected shortfall" : cashPressure ? "Review" : "Current",
                    reason: negativeEndingCash
                        ? "Projected ending cash for the current week is below zero."
                        : "Current-week cash-flow timing is available.",
                    primaryMetric: AmountMetric(
                        "Ending cash",
                        week.EndingCashCents,
                        CashSemantic(week.EndingCashCents)),
                    secondaryMetric: AmountMetric(
                        "Scheduled outflow",
                        scheduledOutflow,
                        "caution")),
                10));
        }

        if (operatingSystem?.MonthAtGlance is { } month)
        {
            var scheduledOutflow = month.DebitExpenseCents +
                month.CreditExpenseCents +
                month.RequiredDebtPaymentCents +
                month.ExtraDebtPaymentCents;
            var negativeEndingCash = month.EndingCashCents < 0;
            var monthPressure = negativeEndingCash || ContainsAny(
                month.PressureStatus,
                "critical",
                "negative",
                "pressure",
                "tight");

            sections.Add((
                CreateSection(
                    key: "monthly-outlook",
                    eyebrow: "Month ahead",
                    title: "Month at a Glance",
                    systemImage: "calendar",
                    priority: negativeEndingCash ? ImmediateAttention : monthPressure ? HighImportance : Planning,
                    status: negativeEndingCash ? "Projected shortfall" : monthPressure ? "Review" : "Current",
                    reason: negativeEndingCash
                        ? "Projected ending cash for the current month is below zero."
                        : "Monthly cash-flow timing is available.",
                    primaryMetric: AmountMetric(
                        "Ending cash",
                        month.EndingCashCents,
                        CashSemantic(month.EndingCashCents)),
                    secondaryMetric: AmountMetric(
                        "Scheduled outflow",
                        scheduledOutflow,
                        "caution")),
                20));

            if (month.LargestObligation is { } obligation)
            {
                var obligationPriority = negativeEndingCash ||
                    scheduledOutflow > month.IncomeCents
                    ? HighImportance
                    : Planning;

                sections.Add((
                    CreateSection(
                        key: "debt-obligations",
                        eyebrow: "Largest upcoming obligation",
                        title: obligation.Title,
                        systemImage: "calendar.badge.exclamationmark",
                        priority: obligationPriority,
                        status: obligationPriority == HighImportance ? "Review" : "Scheduled",
                        reason: "This is the largest scheduled outflow in the current monthly view.",
                        primaryMetric: AmountMetric(
                            "Amount",
                            obligation.AmountCents,
                            "caution"),
                        secondaryMetric: DateMetric(
                            "Scheduled",
                            obligation.OccursOn,
                            "informational")),
                    30));
            }
        }

        if (upcomingBills.Count > 0)
        {
            var nextBill = upcomingBills
                .OrderBy(bill => bill.NextExpectedDateUtc)
                .ThenBy(bill => bill.Id)
                .First();

            sections.Add((
                CreateSection(
                    key: "upcoming-activity",
                    eyebrow: "Upcoming activity",
                    title: nextBill.DisplayName,
                    systemImage: "calendar.badge.clock",
                    priority: Planning,
                    status: "Scheduled",
                    reason: "The next recurring financial item is shown from saved financial data.",
                    primaryMetric: AmountMetric(
                        "Amount",
                        nextBill.AverageAmountCents,
                        "caution"),
                    secondaryMetric: DateMetric(
                        "Next date",
                        DateOnly.FromDateTime(nextBill.NextExpectedDateUtc),
                        "informational")),
                60));
        }

        if (intelligence is { DataCompletenessScore: < 1m })
        {
            sections.Add((
                CreateSection(
                    key: "data-attention",
                    eyebrow: "Data needing attention",
                    title: "Saved financial information is incomplete",
                    systemImage: "checklist.unchecked",
                    priority: HighImportance,
                    status: "Incomplete",
                    reason: "Financial Intelligence reports that some saved information is incomplete.",
                    primaryMetric: TextMetric(
                        "Completeness",
                        intelligence.DataCompletenessScore.ToString("0%"),
                        "caution"),
                    secondaryMetric: null),
                70));
        }

        return new MobileFinancialPresentation(
            sections
                .OrderBy(item => item.Section.Priority)
                .ThenBy(item => item.TieBreaker)
                .Select(item => item.Section)
                .GroupBy(section => section.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray());
    }

    private static MobileFinancialPrioritySection CreateSection(
        string key,
        string eyebrow,
        string title,
        string systemImage,
        int priority,
        string status,
        string reason,
        MobileFinancialSummaryMetric primaryMetric,
        MobileFinancialSummaryMetric? secondaryMetric) =>
        new(
            key,
            eyebrow,
            title,
            systemImage,
            priority,
            status,
            reason,
            primaryMetric,
            secondaryMetric);

    private static MobileFinancialSummaryMetric AmountMetric(
        string label,
        long amountCents,
        string semantic) =>
        new(label, amountCents, null, null, semantic);

    private static MobileFinancialSummaryMetric DateMetric(
        string label,
        DateOnly date,
        string semantic) =>
        new(label, null, date, null, semantic);

    private static MobileFinancialSummaryMetric TextMetric(
        string label,
        string text,
        string semantic) =>
        new(label, null, null, text, semantic);

    private static bool ContainsAny(string? value, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string CashSemantic(long amountCents) =>
        amountCents < 0 ? "negative" : amountCents > 0 ? "positive" : "neutral";

    private static long ToCents(decimal amount) =>
        decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
}
