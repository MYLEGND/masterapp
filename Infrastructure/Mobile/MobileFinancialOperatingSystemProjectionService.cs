using System.Globalization;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Mobile;

/// <summary>
/// Produces a read-only mobile projection from the authenticated account's
/// existing ClientApp or AgentPortal finance authority. This service reads
/// persisted projection data only and never owns, edits, schedules, or
/// recalculates financial state.
/// </summary>
public interface IMobileFinancialOperatingSystemProjectionService
{
    Task<MobileFinancialOperatingSystemSnapshot> ProjectAsync(
        Guid clientProfileId,
        DateOnly currentDate,
        CancellationToken cancellationToken = default);

    Task<MobileFinancialOperatingSystemSnapshot> ProjectAgentAsync(
        string agentUserId,
        DateOnly currentDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects the authenticated account's actual current calendar period from
/// the authoritative web-authored Expense Lens timeline. Client and agent
/// states remain separately owned and are never crossed or merged.
///
/// Expense Lens remains the only calculator. This service performs a direct
/// date selection and transport mapping from persisted JSON into immutable
/// mobile contracts.
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
        DateOnly currentDate,
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
            // Historical profile-scoped Expense Lens rows can coexist from
            // before household-scoped persistence was introduced. The saved
            // state with the newest update is the authoritative projection;
            // choosing it preserves the existing data and prevents a stale
            // duplicate from making the read-only mobile bridge fail.
            .OrderByDescending(row => row.UpdatedUtc)
            .ThenByDescending(row => row.CreatedUtc)
            .ThenByDescending(row => row.Id)
            .Select(row => new MobilePersistedExpenseLensState(
                row.JsonState,
                row.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return await ProjectPersistedStateAsync(
            state,
            generatedUtc,
            currentDate,
            financeStateRoot => ResolveLabelContextAsync(
                clientProfileId,
                financeStateRoot,
                cancellationToken),
            ownerLabel: "client");
    }

    public async Task<MobileFinancialOperatingSystemSnapshot> ProjectAgentAsync(
        string agentUserId,
        DateOnly currentDate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedAgentUserId = agentUserId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedAgentUserId))
        {
            throw new ArgumentException(
                "A valid agent user identifier is required.",
                nameof(agentUserId));
        }

        var generatedUtc = DateTime.UtcNow;
        var state = await _db.AgentFinanceToolStates
            .AsNoTracking()
            .Where(row =>
                row.AgentUserId.ToLower() == normalizedAgentUserId &&
                row.ToolId == ExpenseLensToolId)
            .Select(row => new MobilePersistedExpenseLensState(
                row.JsonState,
                row.UpdatedUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return await ProjectPersistedStateAsync(
            state,
            generatedUtc,
            currentDate,
            financeStateRoot => Task.FromResult(
                new MobileFinancialLabelContext(
                    ClientFirstName: null,
                    HouseholdFirstName: null,
                    IncomeLabels: ReadSavedIncomeLabels(financeStateRoot))),
            ownerLabel: "agent");
    }

    private async Task<MobileFinancialOperatingSystemSnapshot>
        ProjectPersistedStateAsync(
            MobilePersistedExpenseLensState? state,
            DateTime generatedUtc,
            DateOnly currentDate,
            Func<JsonElement, Task<MobileFinancialLabelContext>>
                resolveLabelContext,
            string ownerLabel)
    {
        if (state is null)
        {
            return BuildUnavailable(
                generatedUtc,
                financeStateUpdatedUtc: null,
                reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                summary:
                    $"Expense Lens has not been saved for this {ownerLabel}.");
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

            var period = ResolveCurrentPeriod(
                document.RootElement,
                currentDate);
            if (!period.Succeeded)
            {
                return BuildUnavailable(
                    generatedUtc,
                    state.UpdatedUtc,
                    period.ReasonCode!,
                    period.Summary!);
            }

            var labelContext = await resolveLabelContext(document.RootElement);
            var week = PersonalizeWeek(
                MapWeek(period.WeekElement!.Value) with
                {
                    PressureStatus = "current"
                },
                labelContext);
            MobileFinancialMonthAtGlance? month = null;
            if (period.MonthElement.HasValue)
            {
                var mappedMonth = MapMobileMonthSnapshot(
                    period.MonthElement.Value) ??
                    throw new MobileProjectionMappingException(
                        "The current mobile month projection is incomplete.");
                month = mappedMonth with
                {
                    PressureStatus = "current",
                    Weeks = mappedMonth.Weeks
                        .Select(summary => summary with
                        {
                            PressureStatus = ResolveWeekStatus(
                                summary,
                                currentDate)
                        })
                        .ToArray()
                };
            }

            return new MobileFinancialOperatingSystemSnapshot(
                Projection: new MobileFinancialProjectionStatus(
                    Status: "Available",
                    ReasonCode: null,
                    Summary:
                        $"Current period for {currentDate:yyyy-MM-dd} loaded from the authoritative Expense Lens projection."),
                Freshness: new MobileFinancialDataFreshness(
                    FinanceStateUpdatedUtc: state.UpdatedUtc,
                    IntelligenceEvaluatedUtc: null,
                    GeneratedUtc: generatedUtc),
                WeekAtGlance: week,
                MonthAtGlance: month,
                Tools: new[]
                {
                    new MobileFinancialToolSummary(
                        ToolId: ExpenseLensToolId,
                        Title: "Expense Lens",
                        Category: "Cash Flow",
                        Priority: 1,
                        AvailabilityStatus: "Available",
                        UpdatedUtc: state.UpdatedUtc,
                        Summary: month is null
                            ? "The actual current week projection is available. Save Expense Lens to publish the synchronized month timeline."
                            : "The actual current week and month projections are available.",
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
                exception.ReasonCode ??
                    "MOBILE_WEEK_PROJECTION_INCOMPLETE",
                exception.Message);
        }
    }

    private static MobileCurrentPeriodResolution ResolveCurrentPeriod(
        JsonElement financeStateRoot,
        DateOnly currentDate)
    {
        if (financeStateRoot.TryGetProperty(
                "mobilePeriodProjection",
                out var timelineElement) &&
            timelineElement.ValueKind is not
                JsonValueKind.Null and not
                JsonValueKind.Undefined)
        {
            if (timelineElement.ValueKind != JsonValueKind.Object)
            {
                return MobileCurrentPeriodResolution.Failure(
                    "MOBILE_PERIOD_PROJECTION_INVALID",
                    "The saved Expense Lens period projection is invalid.");
            }

            if (ReadRequiredInt32(timelineElement, "schemaVersion") !=
                SupportedSchemaVersion)
            {
                return MobileCurrentPeriodResolution.Failure(
                    "MOBILE_PERIOD_SCHEMA_UNSUPPORTED",
                    "The saved Expense Lens period projection schema is not supported.");
            }

            if (!timelineElement.TryGetProperty(
                    "periods",
                    out var periodsElement) ||
                periodsElement.ValueKind != JsonValueKind.Array)
            {
                return MobileCurrentPeriodResolution.Failure(
                    "MOBILE_PERIOD_PROJECTION_INCOMPLETE",
                    "The saved Expense Lens period projection does not contain calendar periods.");
            }

            var currentMonthKey = currentDate.ToString(
                "yyyy-MM",
                CultureInfo.InvariantCulture);
            foreach (var periodElement in periodsElement.EnumerateArray())
            {
                if (periodElement.ValueKind != JsonValueKind.Object ||
                    !periodElement.TryGetProperty("monthKey", out var monthKeyElement) ||
                    monthKeyElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        monthKeyElement.GetString(),
                        currentMonthKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!periodElement.TryGetProperty(
                        "monthSnapshot",
                        out var monthElement) ||
                    monthElement.ValueKind != JsonValueKind.Object ||
                    !periodElement.TryGetProperty(
                        "weekSnapshots",
                        out var weeksElement) ||
                    weeksElement.ValueKind != JsonValueKind.Array)
                {
                    return MobileCurrentPeriodResolution.Failure(
                        "MOBILE_PERIOD_PROJECTION_INCOMPLETE",
                        $"The authoritative Expense Lens period for {currentMonthKey} is incomplete.");
                }

                var snapshotMonthKey = ReadRequiredString(
                    monthElement,
                    "monthKey");
                var monthStartDate = ReadRequiredDate(
                    monthElement,
                    "startDate");
                var monthEndDate = ReadRequiredDate(
                    monthElement,
                    "endDate");
                if (!string.Equals(
                        snapshotMonthKey,
                        currentMonthKey,
                        StringComparison.Ordinal) ||
                    currentDate < monthStartDate ||
                    currentDate > monthEndDate)
                {
                    return MobileCurrentPeriodResolution.Failure(
                        "MOBILE_PERIOD_PROJECTION_MISMATCH",
                        $"The authoritative Expense Lens period for {currentMonthKey} does not match its month snapshot.");
                }

                foreach (var weekElement in weeksElement.EnumerateArray())
                {
                    if (weekElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var startDate = ReadRequiredDate(weekElement, "startDate");
                    var endDate = ReadRequiredDate(weekElement, "endDate");
                    if (currentDate >= startDate && currentDate <= endDate)
                    {
                        ValidateSnapshotSchema(weekElement, "week");
                        ValidateSnapshotSchema(monthElement, "month");
                        return MobileCurrentPeriodResolution.Success(
                            weekElement,
                            monthElement);
                    }
                }

                return MobileCurrentPeriodResolution.Failure(
                    "MOBILE_CURRENT_WEEK_NOT_FOUND",
                    $"The authoritative Expense Lens period does not contain the week for {currentDate:yyyy-MM-dd}.");
            }

            return MobileCurrentPeriodResolution.Failure(
                "MOBILE_CURRENT_PERIOD_NOT_FOUND",
                $"Expense Lens has no authoritative projection for the current month {currentMonthKey}. Open and save Expense Lens to extend the synchronized timeline.");
        }

        return ResolveLegacyCurrentPeriod(
            financeStateRoot,
            currentDate);
    }

    private static MobileCurrentPeriodResolution ResolveLegacyCurrentPeriod(
        JsonElement financeStateRoot,
        DateOnly currentDate)
    {
        if (!financeStateRoot.TryGetProperty(
                "mobileWeekProjection",
                out var weekElement) ||
            weekElement.ValueKind is
                JsonValueKind.Null or
                JsonValueKind.Undefined)
        {
            return MobileCurrentPeriodResolution.Failure(
                "MOBILE_WEEK_PROJECTION_NOT_FOUND",
                "Open and save Expense Lens to publish the synchronized mobile period projection.");
        }

        if (weekElement.ValueKind != JsonValueKind.Object)
        {
            return MobileCurrentPeriodResolution.Failure(
                "MOBILE_WEEK_PROJECTION_INVALID",
                "The saved mobile week projection is not a JSON object.");
        }

        ValidateSnapshotSchema(weekElement, "week");
        var startDate = ReadRequiredDate(weekElement, "startDate");
        var endDate = ReadRequiredDate(weekElement, "endDate");
        if (currentDate < startDate || currentDate > endDate)
        {
            return MobileCurrentPeriodResolution.Failure(
                "MOBILE_CURRENT_PERIOD_NOT_FOUND",
                $"The legacy Expense Lens snapshot covers {startDate:yyyy-MM-dd} through {endDate:yyyy-MM-dd}, not the current date {currentDate:yyyy-MM-dd}. Open and save Expense Lens to publish the synchronized timeline.");
        }

        JsonElement? monthSnapshot = null;
        if (financeStateRoot.TryGetProperty(
                "mobileMonthProjection",
                out var monthElement) &&
            monthElement.ValueKind == JsonValueKind.Object)
        {
            var currentMonthKey = currentDate.ToString(
                "yyyy-MM",
                CultureInfo.InvariantCulture);
            if (monthElement.TryGetProperty("monthKey", out var monthKeyElement) &&
                monthKeyElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    monthKeyElement.GetString(),
                    currentMonthKey,
                    StringComparison.Ordinal))
            {
                ValidateSnapshotSchema(monthElement, "month");
                monthSnapshot = monthElement;
            }
        }

        return MobileCurrentPeriodResolution.Success(
            weekElement,
            monthSnapshot);
    }

    private static void ValidateSnapshotSchema(
        JsonElement snapshot,
        string snapshotName)
    {
        var schemaVersion = ReadRequiredInt32(snapshot, "schemaVersion");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new MobileProjectionMappingException(
                $"Mobile {snapshotName} projection schema {schemaVersion} is not supported.",
                $"MOBILE_{snapshotName.ToUpperInvariant()}_SCHEMA_UNSUPPORTED");
        }
    }

    private static string ResolveWeekStatus(
        MobileFinancialWeekSummary week,
        DateOnly currentDate)
    {
        if (currentDate >= week.StartDate &&
            currentDate <= week.EndDate)
        {
            return "current";
        }

        if (week.EndDate < currentDate)
        {
            return string.Equals(
                week.PressureStatus,
                "actual",
                StringComparison.OrdinalIgnoreCase)
                ? "actual"
                : "historical-unreconciled";
        }

        return "projected";
    }

    private sealed record MobileCurrentPeriodResolution(
        bool Succeeded,
        JsonElement? WeekElement,
        JsonElement? MonthElement,
        string? ReasonCode,
        string? Summary)
    {
        public static MobileCurrentPeriodResolution Success(
            JsonElement weekElement,
            JsonElement? monthElement) =>
            new(true, weekElement, monthElement, null, null);

        public static MobileCurrentPeriodResolution Failure(
            string reasonCode,
            string summary) =>
            new(false, null, null, reasonCode, summary);
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

    private async Task<MobileFinancialLabelContext>
        ResolveLabelContextAsync(
            Guid clientProfileId,
            JsonElement financeStateRoot,
            CancellationToken cancellationToken)
    {
        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .Where(candidate => candidate.Id == clientProfileId)
            .Select(candidate => new MobileFinancialProfileName(
                candidate.ClientUserId,
                candidate.FirstName,
                candidate.SignificantOtherFirstName))
            .SingleOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            return MobileFinancialLabelContext.Empty;
        }

        var clientUserId = NormalizeName(profile.ClientUserId);
        var householdFirstName = string.IsNullOrWhiteSpace(clientUserId)
            ? null
            : await _db.HouseholdMembers
                .AsNoTracking()
                .Where(member => member.ClientUserId.ToLower() == clientUserId)
                .Where(member =>
                    member.RelationshipType == "SignificantOther" ||
                    member.RelationshipType == "Spouse")
                .OrderByDescending(member => member.UpdatedUtc)
                .ThenByDescending(member => member.CreatedUtc)
                .Select(member => member.FirstName)
                .FirstOrDefaultAsync(cancellationToken);

        return new MobileFinancialLabelContext(
            ClientFirstName: NormalizeName(profile.FirstName),
            HouseholdFirstName: NormalizeName(householdFirstName) ??
                NormalizeName(profile.SignificantOtherFirstName),
            IncomeLabels: ReadSavedIncomeLabels(financeStateRoot));
    }

    private static MobileFinancialWeekAtGlance PersonalizeWeek(
        MobileFinancialWeekAtGlance week,
        MobileFinancialLabelContext context) =>
        week with
        {
            Events = week.Events
                .Select(financialEvent => financialEvent with
                {
                    Title = ResolveEventTitle(financialEvent, context)
                })
                .ToArray()
        };

    private static string ResolveEventTitle(
        MobileFinancialCashFlowEvent financialEvent,
        MobileFinancialLabelContext context)
    {
        if (!string.Equals(
                financialEvent.Kind,
                "income",
                StringComparison.OrdinalIgnoreCase) ||
            !TryReadIncomeSource(financialEvent.EventKey, out var sourceKey))
        {
            return financialEvent.Title;
        }

        if (context.IncomeLabels.TryGetValue(
                sourceKey,
                out var savedLabel))
        {
            return savedLabel;
        }

        if (!IsGeneratedIncomeLabel(financialEvent.Title))
        {
            return financialEvent.Title;
        }

        return sourceKey.StartsWith("primary-", StringComparison.Ordinal)
            ? PossessiveIncomeLabel(context.ClientFirstName)
            : sourceKey.StartsWith("secondary-", StringComparison.Ordinal)
                ? PossessiveIncomeLabel(context.HouseholdFirstName)
                : financialEvent.Title;
    }

    private static IReadOnlyDictionary<string, string> ReadSavedIncomeLabels(
        JsonElement financeStateRoot)
    {
        var labels = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (!financeStateRoot.TryGetProperty(
                "incomeStreams",
                out var groupsElement) ||
            groupsElement.ValueKind != JsonValueKind.Object)
        {
            return labels;
        }

        foreach (var groupName in new[] { "primary", "secondary" })
        {
            if (!groupsElement.TryGetProperty(
                    groupName,
                    out var streamsElement) ||
                streamsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var stream in streamsElement.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !stream.TryGetProperty("label", out var labelElement) ||
                    labelElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = NormalizeName(idElement.GetString());
                var label = NormalizeName(labelElement.GetString());

                if (!string.IsNullOrWhiteSpace(id) &&
                    !string.IsNullOrWhiteSpace(label))
                {
                    labels[$"{groupName}-{id}"] = label;
                }
            }
        }

        return labels;
    }

    private static bool TryReadIncomeSource(
        string eventKey,
        out string sourceKey)
    {
        sourceKey = string.Empty;

        if (!eventKey.StartsWith("income:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = eventKey["income:".Length..];
        var lastSeparator = remainder.LastIndexOf(':');

        if (lastSeparator <= 0)
        {
            return false;
        }

        sourceKey = remainder[..lastSeparator].Trim();
        return sourceKey.Length > 0;
    }

    private static bool IsGeneratedIncomeLabel(string value)
    {
        var normalized = value.Trim();

        return string.Equals(normalized, "Income", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Client Income", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Partner Income", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Income Stream ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Primary Income Stream ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Partner Income Stream ", StringComparison.OrdinalIgnoreCase);
    }

    private static string PossessiveIncomeLabel(string? firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
        {
            return "Income";
        }

        return firstName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            ? $"{firstName}' Income"
            : $"{firstName}'s Income";
    }

    private static string? NormalizeName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record MobileFinancialProfileName(
        string? ClientUserId,
        string? FirstName,
        string? SignificantOtherFirstName);

    private sealed record MobilePersistedExpenseLensState(
        string JsonState,
        DateTime UpdatedUtc);

    private sealed record MobileFinancialLabelContext(
        string? ClientFirstName,
        string? HouseholdFirstName,
        IReadOnlyDictionary<string, string> IncomeLabels)
    {
        public static MobileFinancialLabelContext Empty { get; } = new(
            null,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
            string message,
            string? reasonCode = null)
            : base(message)
        {
            ReasonCode = reasonCode;
        }

        public string? ReasonCode { get; }
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

        return MapMobileMonthSnapshot(monthElement);
    }

    private static MobileFinancialMonthAtGlance?
        MapMobileMonthSnapshot(
            JsonElement monthElement)
    {
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
