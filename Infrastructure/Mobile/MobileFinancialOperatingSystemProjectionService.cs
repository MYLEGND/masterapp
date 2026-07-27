using System.Globalization;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Mobile;

/// <summary>
/// Produces the read-only mobile projection of the existing ClientApp finance
/// authority. This service reads persisted authoritative projection data only
/// and never owns, edits, schedules, or recalculates financial state.
/// </summary>
public interface IMobileFinancialOperatingSystemProjectionService
{
    Task<MobileFinancialOperatingSystemSnapshot> ProjectAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the authoritative mobile week projection persisted inside the
/// client's ExpenseLens FinanceToolState.
///
/// Expense Lens remains the only calculator. This service performs a direct
/// transport mapping from persisted JSON into immutable mobile contracts.
/// </summary>
public sealed class MobileFinancialOperatingSystemProjectionService
    : IMobileFinancialOperatingSystemProjectionService
{
    private const string ExpenseLensToolId = "ExpenseLens";
    private const int SupportedSchemaVersion = 1;

    private readonly MasterAppDbContext _db;

    public MobileFinancialOperatingSystemProjectionService(
        MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<MobileFinancialOperatingSystemSnapshot> ProjectAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (clientProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid client profile identifier is required.",
                nameof(clientProfileId));
        }

        var generatedUtc = DateTime.UtcNow;

        var state = await _db.FinanceToolStates
            .AsNoTracking()
            .Where(row =>
                row.ClientProfileId == clientProfileId &&
                row.ToolId == ExpenseLensToolId)
            .Select(row => new
            {
                row.JsonState,
                row.UpdatedUtc
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (state is null)
        {
            return BuildUnavailable(
                generatedUtc,
                financeStateUpdatedUtc: null,
                reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                summary:
                    "Expense Lens has not been saved for this client.");
        }

        if (string.IsNullOrWhiteSpace(state.JsonState))
        {
            return BuildUnavailable(
                generatedUtc,
                state.UpdatedUtc,
                "EXPENSE_LENS_STATE_EMPTY",
                "The saved Expense Lens state is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(state.JsonState);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BuildUnavailable(
                    generatedUtc,
                    state.UpdatedUtc,
                    "EXPENSE_LENS_STATE_INVALID",
                    "The saved Expense Lens state is not a JSON object.");
            }

            if (!document.RootElement.TryGetProperty(
                    "mobileWeekProjection",
                    out var weekElement) ||
                weekElement.ValueKind is
                    JsonValueKind.Null or
                    JsonValueKind.Undefined)
            {
                return BuildUnavailable(
                    generatedUtc,
                    state.UpdatedUtc,
                    "MOBILE_WEEK_PROJECTION_NOT_FOUND",
                    "Open and save Expense Lens to publish the current mobile week projection.");
            }

            if (weekElement.ValueKind != JsonValueKind.Object)
            {
                return BuildUnavailable(
                    generatedUtc,
                    state.UpdatedUtc,
                    "MOBILE_WEEK_PROJECTION_INVALID",
                    "The saved mobile week projection is not a JSON object.");
            }

            var schemaVersion = ReadRequiredInt32(
                weekElement,
                "schemaVersion");

            if (schemaVersion != SupportedSchemaVersion)
            {
                return BuildUnavailable(
                    generatedUtc,
                    state.UpdatedUtc,
                    "MOBILE_WEEK_SCHEMA_UNSUPPORTED",
                    $"Mobile week projection schema {schemaVersion} is not supported.");
            }

            var week = MapWeek(weekElement);

            return new MobileFinancialOperatingSystemSnapshot(
                Projection: new MobileFinancialProjectionStatus(
                    Status: "Available",
                    ReasonCode: null,
                    Summary:
                        "Current week loaded from the authoritative Expense Lens projection."),
                Freshness: new MobileFinancialDataFreshness(
                    FinanceStateUpdatedUtc: state.UpdatedUtc,
                    IntelligenceEvaluatedUtc: null,
                    GeneratedUtc: generatedUtc),
                WeekAtGlance: week,
                MonthAtGlance: MapMobileMonthProjection(document.RootElement),
                Tools: new[]
                {
                    new MobileFinancialToolSummary(
                        ToolId: ExpenseLensToolId,
                        Title: "Expense Lens",
                        Category: "Cash Flow",
                        Priority: 1,
                        AvailabilityStatus: "Available",
                        UpdatedUtc: state.UpdatedUtc,
                        Summary:
                            "Current week projection is available.",
                        Metrics: Array.Empty<MobileFinancialMetric>())
                });
        }
        catch (JsonException)
        {
            return BuildUnavailable(
                generatedUtc,
                state.UpdatedUtc,
                "EXPENSE_LENS_STATE_INVALID_JSON",
                "The saved Expense Lens state contains invalid JSON.");
        }
        catch (MobileProjectionMappingException exception)
        {
            return BuildUnavailable(
                generatedUtc,
                state.UpdatedUtc,
                "MOBILE_WEEK_PROJECTION_INCOMPLETE",
                exception.Message);
        }
    }

    private static MobileFinancialWeekAtGlance MapWeek(
        JsonElement week)
    {
        var events = new List<MobileFinancialCashFlowEvent>();

        if (week.TryGetProperty("events", out var eventsElement))
        {
            if (eventsElement.ValueKind != JsonValueKind.Array)
            {
                throw new MobileProjectionMappingException(
                    "The mobile week event collection is invalid.");
            }

            foreach (var eventElement in eventsElement.EnumerateArray())
            {
                if (eventElement.ValueKind != JsonValueKind.Object)
                {
                    throw new MobileProjectionMappingException(
                        "A mobile week event is invalid.");
                }

                var eventKey = ReadRequiredString(
                    eventElement,
                    "key");

                events.Add(new MobileFinancialCashFlowEvent(
                    EventKey: eventKey,
                    OccursOn: ReadRequiredDate(
                        eventElement,
                        "dateKey"),
                    Kind: ReadRequiredString(
                        eventElement,
                        "kind"),
                    Title: ReadRequiredString(
                        eventElement,
                        "label"),
                    AmountCents: ReadRequiredInt64(
                        eventElement,
                        "amountCents"),
                    SourceToolId: ExpenseLensToolId,
                    SourceItemId: eventKey,
                    Status: ReadRequiredString(
                        eventElement,
                        "status")));
            }
        }

        return new MobileFinancialWeekAtGlance(
            WeekKey: ReadRequiredString(
                week,
                "weekId"),
            StartDate: ReadRequiredDate(
                week,
                "startDate"),
            EndDate: ReadRequiredDate(
                week,
                "endDate"),
            OpeningCashCents: ReadRequiredInt64(
                week,
                "openingCashCents"),
            IncomeCents: ReadRequiredInt64(
                week,
                "incomeCents"),
            DebitExpenseCents: ReadRequiredInt64(
                week,
                "debitBillsCents"),
            CreditExpenseCents: ReadRequiredInt64(
                week,
                "creditBillsCents"),
            RequiredDebtPaymentCents: ReadRequiredInt64(
                week,
                "requiredDebtMinimumCents"),
            ExtraDebtPaymentCents: ReadRequiredInt64(
                week,
                "extraDebtPaymentCents"),
            EndingCashCents: ReadRequiredInt64(
                week,
                "closingCashCents"),
            OpeningDebtCents: ReadRequiredInt64(
                week,
                "openingDebtCents"),
            EndingDebtCents: ReadRequiredInt64(
                week,
                "closingDebtCents"),
            PressureStatus: ReadRequiredString(
                week,
                "status"),
            PressureSummary: ReadOptionalString(
                week,
                "weekLabel"),
            Events: events);
    }

    private static MobileFinancialOperatingSystemSnapshot BuildUnavailable(
        DateTime generatedUtc,
        DateTime? financeStateUpdatedUtc,
        string reasonCode,
        string summary)
    {
        return new MobileFinancialOperatingSystemSnapshot(
            Projection: new MobileFinancialProjectionStatus(
                Status: "Unavailable",
                ReasonCode: reasonCode,
                Summary: summary),
            Freshness: new MobileFinancialDataFreshness(
                FinanceStateUpdatedUtc: financeStateUpdatedUtc,
                IntelligenceEvaluatedUtc: null,
                GeneratedUtc: generatedUtc),
            WeekAtGlance: null,
            MonthAtGlance: null,
            Tools: Array.Empty<MobileFinancialToolSummary>());
    }

    private static string ReadRequiredString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection is missing '{propertyName}'.");
        }

        var value = element.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection contains an empty '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind is
                JsonValueKind.Null or
                JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection contains an invalid '{propertyName}'.");
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int ReadRequiredInt32(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value))
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection is missing or contains an invalid '{propertyName}'.");
        }

        return value;
    }

    private static long ReadRequiredInt64(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out var value))
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection is missing or contains an invalid '{propertyName}'.");
        }

        return value;
    }

    private static DateOnly ReadRequiredDate(
        JsonElement parent,
        string propertyName)
    {
        var value = ReadRequiredString(
            parent,
            propertyName);

        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new MobileProjectionMappingException(
                $"The mobile week projection contains an invalid '{propertyName}'.");
        }

        return date;
    }

    private sealed class MobileProjectionMappingException
        : Exception
    {
        public MobileProjectionMappingException(
            string message)
            : base(message)
        {
        }
    }

    private static MobileFinancialMonthAtGlance?
        MapMobileMonthProjection(
            JsonElement financeStateRoot)
    {
        if (!financeStateRoot.TryGetProperty(
                "mobileMonthProjection",
                out var monthElement) ||
            monthElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryReadPhase3Int32(
                monthElement,
                "schemaVersion",
                out var schemaVersion) ||
            schemaVersion != 1)
        {
            return null;
        }

        if (!TryReadPhase3String(
                monthElement,
                "monthKey",
                out var monthKey) ||
            !TryReadPhase3Date(
                monthElement,
                "startDate",
                out var startDate) ||
            !TryReadPhase3Date(
                monthElement,
                "endDate",
                out var endDate) ||
            !TryReadPhase3Int64(
                monthElement,
                "openingCashCents",
                out var openingCashCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "incomeCents",
                out var incomeCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "debitBillsCents",
                out var debitExpenseCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "creditBillsCents",
                out var creditExpenseCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "requiredDebtMinimumCents",
                out var requiredDebtPaymentCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "extraDebtPaymentCents",
                out var extraDebtPaymentCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "endingCashCents",
                out var endingCashCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "openingDebtCents",
                out var openingDebtCents) ||
            !TryReadPhase3Int64(
                monthElement,
                "endingDebtCents",
                out var endingDebtCents) ||
            !TryReadPhase3String(
                monthElement,
                "status",
                out var pressureStatus))
        {
            return null;
        }

        var pressureSummary =
            TryReadPhase3NullableString(
                monthElement,
                "pressureSummary");

        var savingsContributionCents =
            TryReadPhase3NullableInt64(
                monthElement,
                "savingsContributionCents") ?? 0L;

        var largestObligation =
            MapMobileLargestObligation(monthElement);

        if (!TryMapMobileMonthWeeks(
                monthElement,
                out var weeks))
        {
            return null;
        }

        return new MobileFinancialMonthAtGlance(
            MonthKey: monthKey,
            StartDate: startDate,
            EndDate: endDate,
            OpeningCashCents: openingCashCents,
            IncomeCents: incomeCents,
            DebitExpenseCents: debitExpenseCents,
            CreditExpenseCents: creditExpenseCents,
            RequiredDebtPaymentCents:
                requiredDebtPaymentCents,
            ExtraDebtPaymentCents:
                extraDebtPaymentCents,
            EndingCashCents: endingCashCents,
            OpeningDebtCents: openingDebtCents,
            EndingDebtCents: endingDebtCents,
            SavingsContributionCents:
                savingsContributionCents,
            PressureStatus: pressureStatus,
            PressureSummary: pressureSummary,
            LargestObligation: largestObligation,
            Weeks: weeks);
    }

    private static MobileFinancialLargestObligation?
        MapMobileLargestObligation(
            JsonElement monthElement)
    {
        if (!monthElement.TryGetProperty(
                "largestObligation",
                out var obligationElement) ||
            obligationElement.ValueKind ==
                JsonValueKind.Null)
        {
            return null;
        }

        if (obligationElement.ValueKind !=
            JsonValueKind.Object ||
            !TryReadPhase3String(
                obligationElement,
                "title",
                out var title) ||
            !TryReadPhase3Date(
                obligationElement,
                "dateKey",
                out var occursOn) ||
            !TryReadPhase3Int64(
                obligationElement,
                "amountCents",
                out var amountCents) ||
            !TryReadPhase3String(
                obligationElement,
                "kind",
                out var kind))
        {
            return null;
        }

        return new MobileFinancialLargestObligation(
            Title: title,
            OccursOn: occursOn,
            AmountCents: amountCents,
            Kind: kind);
    }

    private static bool TryMapMobileMonthWeeks(
        JsonElement monthElement,
        out IReadOnlyList<MobileFinancialWeekSummary> weeks)
    {
        weeks = Array.Empty<MobileFinancialWeekSummary>();

        if (!monthElement.TryGetProperty(
                "weeks",
                out var weeksElement) ||
            weeksElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var mappedWeeks =
            new List<MobileFinancialWeekSummary>();

        foreach (var weekElement in
                 weeksElement.EnumerateArray())
        {
            if (weekElement.ValueKind !=
                    JsonValueKind.Object ||
                !TryReadPhase3String(
                    weekElement,
                    "weekId",
                    out var weekKey) ||
                !TryReadPhase3Date(
                    weekElement,
                    "startDate",
                    out var startDate) ||
                !TryReadPhase3Date(
                    weekElement,
                    "endDate",
                    out var endDate) ||
                !TryReadPhase3Int64(
                    weekElement,
                    "incomeCents",
                    out var incomeCents) ||
                !TryReadPhase3Int64(
                    weekElement,
                    "outflowCents",
                    out var outflowCents) ||
                !TryReadPhase3Int64(
                    weekElement,
                    "closingCashCents",
                    out var endingCashCents) ||
                !TryReadPhase3Int64(
                    weekElement,
                    "closingDebtCents",
                    out var endingDebtCents) ||
                !TryReadPhase3String(
                    weekElement,
                    "status",
                    out var pressureStatus))
            {
                return false;
            }

            mappedWeeks.Add(
                new MobileFinancialWeekSummary(
                    WeekKey: weekKey,
                    StartDate: startDate,
                    EndDate: endDate,
                    IncomeCents: incomeCents,
                    OutflowCents: outflowCents,
                    EndingCashCents: endingCashCents,
                    EndingDebtCents: endingDebtCents,
                    PressureStatus: pressureStatus));
        }

        weeks = mappedWeeks;
        return true;
    }

    private static bool TryReadPhase3String(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(
                propertyName,
                out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;

        return value.Length > 0;
    }

    private static string? TryReadPhase3NullableString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }

    private static bool TryReadPhase3Date(
        JsonElement element,
        string propertyName,
        out DateOnly value)
    {
        value = default;

        return element.TryGetProperty(
                   propertyName,
                   out var property) &&
               property.ValueKind == JsonValueKind.String &&
               DateOnly.TryParseExact(
                   property.GetString(),
                   "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None,
                   out value);
    }

    private static bool TryReadPhase3Int32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = default;

        return element.TryGetProperty(
                   propertyName,
                   out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadPhase3Int64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = default;

        return element.TryGetProperty(
                   propertyName,
                   out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static long? TryReadPhase3NullableInt64(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : null;
    }
}
