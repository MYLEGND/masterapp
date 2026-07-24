using System.Text.Json;
using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.FinancialIntelligence;

internal sealed class ExpenseLensSynchronizationService : IExpenseLensSynchronizationService
{
    private static readonly HashSet<string> SupportedExpenseLensToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExpenseLens",
        "BusinessExpenseLens"
    };

    private readonly MasterAppDbContext _db;
    private readonly ILogger<ExpenseLensSynchronizationService> _logger;

    public ExpenseLensSynchronizationService(
        MasterAppDbContext db,
        ILogger<ExpenseLensSynchronizationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ExpenseLensStreamLinkResult> LinkStreamAsync(
        ExpenseLensStreamLinkCommand command,
        CancellationToken cancellationToken = default)
    {
        var toolId = NormalizeRequired(command.ExpenseLensToolId);
        var itemId = NormalizeRequired(command.ExpenseLensItemId);
        var confirmedByUserId = NormalizeOptional(command.ConfirmedByUserId);

        if (command.ClientProfileId == Guid.Empty || command.RecurringFinancialStreamId == Guid.Empty ||
            string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(itemId) ||
            !SupportedExpenseLensToolIds.Contains(toolId) || !Fits(itemId, 200) ||
            !Fits(confirmedByUserId, 450) ||
            (command.Confirmed && string.IsNullOrWhiteSpace(confirmedByUserId)))
        {
            return Failure("EXPENSE_LENS_LINK_INVALID", "The Expense Lens stream link is invalid.");
        }

        var stream = await _db.RecurringFinancialStreams.FirstOrDefaultAsync(
            x => x.Id == command.RecurringFinancialStreamId && x.ClientProfileId == command.ClientProfileId,
            cancellationToken);
        if (stream is null)
            return Failure("STREAM_NOT_FOUND", "The requested recurring financial stream was not found for this client.");

        if (string.Equals(stream.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            return Failure("STREAM_INACTIVE", "An inactive recurring financial stream cannot be linked to Expense Lens.");

        var state = await _db.FinanceToolStates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ClientProfileId == command.ClientProfileId && x.ToolId == toolId,
                cancellationToken);
        if (state is null)
            return Failure("EXPENSE_LENS_STATE_NOT_FOUND", "The requested Expense Lens state was not found for this client.");

        if (!TryReadExpenseLensItemIds(state.JsonState, out var itemIds))
            return Failure("EXPENSE_LENS_STATE_INVALID", "The stored Expense Lens state is invalid.");

        if (!itemIds.Contains(itemId))
            return Failure("EXPENSE_LENS_ITEM_NOT_FOUND", "The requested Expense Lens item was not found for this client.");

        var link = await _db.ExpenseLensStreamLinks.FirstOrDefaultAsync(
            x => x.RecurringFinancialStreamId == command.RecurringFinancialStreamId,
            cancellationToken);
        var nowUtc = DateTime.UtcNow;
        if (link is null)
        {
            link = new ExpenseLensStreamLink
            {
                ClientProfileId = command.ClientProfileId,
                RecurringFinancialStreamId = command.RecurringFinancialStreamId,
                CreatedUtc = nowUtc
            };
            _db.ExpenseLensStreamLinks.Add(link);
        }

        link.ExpenseLensToolId = toolId;
        link.ExpenseLensItemId = itemId;
        link.Status = command.Confirmed ? "Confirmed" : "Suggested";
        link.ConfirmedByUserId = command.Confirmed ? confirmedByUserId : null;
        link.ConfirmedUtc = command.Confirmed ? nowUtc : null;
        link.UpdatedUtc = nowUtc;

        stream.Status = command.Confirmed ? "Confirmed" : "Candidate";
        stream.UpdatedUtc = nowUtc;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Expense Lens stream link save failed. ClientProfileId={ClientProfileId} StreamId={StreamId}",
                command.ClientProfileId,
                command.RecurringFinancialStreamId);
            return Failure("EXPENSE_LENS_LINK_SAVE_FAILED", "The Expense Lens stream link could not be saved.");
        }

        return new ExpenseLensStreamLinkResult(true, null, null, link);
    }

    public async Task<ExpenseLensSynchronizationResult> SynchronizeAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!await FinancialIntelligenceScope.ClientProfileExistsAsync(_db, clientProfileId, cancellationToken))
            return SynchronizationFailure("CLIENT_PROFILE_NOT_FOUND", "The requested client profile was not found.");

        var states = await _db.FinanceToolStates
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId && SupportedExpenseLensToolIds.Contains(x.ToolId))
            .ToListAsync(cancellationToken);
        var itemIdsByTool = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (!TryReadExpenseLensItemIds(state.JsonState, out var itemIds))
            {
                _logger.LogWarning(
                    "Expense Lens synchronization skipped invalid state. ClientProfileId={ClientProfileId} ToolId={ToolId}",
                    clientProfileId,
                    state.ToolId);
                return SynchronizationFailure("EXPENSE_LENS_STATE_INVALID", "A stored Expense Lens state is invalid.");
            }

            itemIdsByTool[state.ToolId] = itemIds;
        }

        var links = await _db.ExpenseLensStreamLinks
            .Where(x => x.ClientProfileId == clientProfileId)
            .ToListAsync(cancellationToken);
        var validLinkCount = 0;
        var staleLinkCount = 0;
        var changed = false;
        var nowUtc = DateTime.UtcNow;

        foreach (var link in links)
        {
            var isValid = itemIdsByTool.TryGetValue(link.ExpenseLensToolId, out var itemIds) &&
                          itemIds.Contains(link.ExpenseLensItemId);
            if (isValid)
            {
                validLinkCount += 1;
                var restoredStatus = link.ConfirmedUtc.HasValue ? "Confirmed" : "Suggested";
                if (!string.Equals(link.Status, restoredStatus, StringComparison.Ordinal))
                {
                    link.Status = restoredStatus;
                    link.UpdatedUtc = nowUtc;
                    changed = true;
                }

                continue;
            }

            staleLinkCount += 1;
            if (!string.Equals(link.Status, "Stale", StringComparison.Ordinal))
            {
                link.Status = "Stale";
                link.UpdatedUtc = nowUtc;
                changed = true;
            }
        }

        if (changed)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(
                    ex,
                    "Expense Lens synchronization save failed. ClientProfileId={ClientProfileId}",
                    clientProfileId);
                return SynchronizationFailure("EXPENSE_LENS_SYNC_SAVE_FAILED", "Expense Lens stream links could not be synchronized.");
            }
        }

        return new ExpenseLensSynchronizationResult(true, null, null, validLinkCount, staleLinkCount);
    }

    private static bool TryReadExpenseLensItemIds(string? jsonState, out HashSet<string> itemIds)
    {
        itemIds = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(jsonState))
            return false;

        try
        {
            using var document = JsonDocument.Parse(jsonState);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var categories = document.RootElement.TryGetProperty("categories", out var categoriesElement) &&
                             categoriesElement.ValueKind == JsonValueKind.Array &&
                             categoriesElement.GetArrayLength() > 0
                ? categoriesElement
                : document.RootElement.TryGetProperty("expenses", out var expensesElement)
                    ? expensesElement
                    : categoriesElement;
            if (categories.ValueKind != JsonValueKind.Array)
                return true;

            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object ||
                    !category.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = NormalizeRequired(idElement.GetString());
                if (!string.IsNullOrWhiteSpace(id))
                    itemIds.Add(id);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ExpenseLensStreamLinkResult Failure(string errorCode, string summary) =>
        new(false, errorCode, summary);

    private static ExpenseLensSynchronizationResult SynchronizationFailure(string errorCode, string summary) =>
        new(false, errorCode, summary, 0, 0);

    private static bool Fits(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
