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
                MonthAtGlance: null,
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
}
