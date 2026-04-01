using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System.Globalization;
using System.Security.Claims;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public class BookKeepingController : Controller
{
    private readonly MasterAppDbContext _db;

    public BookKeepingController(MasterAppDbContext db)
    {
        _db = db;
    }

    private sealed record WorkspaceContext(string OwnerUserId, BookkeepingScope Scope, string ClientUserId);

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private static string ResolveRecordType(ClientProfile client)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
        return ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType);
    }

    private string GetAgentUpn() =>
        Norm(User.FindFirstValue("preferred_username")
            ?? User.FindFirstValue(ClaimTypes.Upn)
            ?? User.FindFirstValue(ClaimTypes.Email)
            ?? User.Identity?.Name);

    private string[] GetAgentIdCandidates() =>
        User.GetUserIdCandidates()
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

    private string GetAgentOidOrThrow()
    {
        var oid = Norm(
            User.FindFirstValue("oid")
            ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
        );

        if (string.IsNullOrWhiteSpace(oid))
            throw new InvalidOperationException("Missing agent OID claim.");

        return oid;
    }

    private async Task<bool> AgentOwnsClientAsync(string agentOid, string clientUserId)
    {
        var clientId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientId)) return false;

        var upn = GetAgentUpn();
        var agentIds = GetAgentIdCandidates();

        return await _db.AgentClients.AnyAsync(x =>
            (x.ClientUserId ?? "").ToLower() == clientId &&
            (
                (x.AgentUserId ?? "").ToLower() == agentOid ||
                agentIds.Contains((x.AgentUserId ?? "").ToLower()) ||
                (!string.IsNullOrWhiteSpace(upn) && (x.AgentUpn ?? "").ToLower() == upn)
            ));
    }

    private IQueryable<BookkeepingEntry> EntriesFor(string ownerUserId, BookkeepingScope scope)
        => _db.BookkeepingEntries.Where(x =>
            x.OwnerUserId == ownerUserId &&
            x.Scope == scope);

    private IQueryable<RecurringExpense> RecurringFor(string ownerUserId, BookkeepingScope scope)
        => _db.RecurringExpenses.Where(x =>
            x.OwnerUserId == ownerUserId &&
            x.Scope == scope);

    private async Task<WorkspaceContext?> ResolveWorkspaceAsync(string agentOid, string? clientUserId)
    {
        var normalizedClientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(normalizedClientUserId))
            return new WorkspaceContext(agentOid, BookkeepingScope.AgentPrivate, "");

        if (!await AgentOwnsClientAsync(agentOid, normalizedClientUserId))
            return null;

        var client = await _db.ClientProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == normalizedClientUserId);

        if (client == null || !string.Equals(ResolveRecordType(client), "BusinessClient", StringComparison.OrdinalIgnoreCase))
            return null;

        return new WorkspaceContext(normalizedClientUserId, BookkeepingScope.ClientShared, normalizedClientUserId);
    }

    private async Task<bool> SetWorkspaceBagsAsync(WorkspaceContext workspace)
    {
        ViewBag.ClientUserId = workspace.ClientUserId;
        ViewBag.BookKeepingMode = workspace.Scope == BookkeepingScope.AgentPrivate ? "agent" : "client";

        if (workspace.Scope == BookkeepingScope.AgentPrivate)
        {
            ViewBag.ClientDisplayName = "My Book Keeping";
            return true;
        }

        var client = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == workspace.ClientUserId);

        if (client == null) return false;

        ViewBag.ClientDisplayName = $"{client.FirstName} {client.LastName}".Trim();
        return true;
    }

    private object RouteForWorkspace(WorkspaceContext workspace, string? monthKey = null)
        => workspace.Scope == BookkeepingScope.ClientShared
            ? new { clientUserId = workspace.ClientUserId, monthKey }
            : new { monthKey };

    public sealed class EditBookkeepingEntryVm
    {
        public int Id { get; set; }
        public BookkeepingEntryType Type { get; set; }
        public DateTime EntryDate { get; set; }
        public decimal Amount { get; set; }
        public BookkeepingCategory Category { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class EditRecurringExpenseVm
    {
        public int Id { get; set; }
        public BookkeepingEntryType Type { get; set; }
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public RecurrenceFrequency Frequency { get; set; }
        public BookkeepingCategory Category { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? NextDueDate { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; }
    }

    [HttpGet]
        public async Task<IActionResult> Index(string? clientUserId, string? monthKey = null)
        {
            if (!string.IsNullOrWhiteSpace(clientUserId))
                return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

            string agentOid;
            try { agentOid = GetAgentOidOrThrow(); }
            catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (!await SetWorkspaceBagsAsync(workspace))
            return NotFound();

        return View(await BuildIndexVmAsync(workspace, monthKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEntry([Bind(Prefix = "QuickAdd")] AddBookkeepingEntryVm model, string? clientUserId, string? monthKey = null)
    {
        // Shared client bookkeeping now lives in ClientApp; keep AgentPortal actions for agent-private only.
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (!model.Amount.HasValue || model.Amount.Value <= 0m)
            ModelState.AddModelError("QuickAdd.Amount", "Amount is required.");

        if (!ModelState.IsValid)
        {
            if (!await SetWorkspaceBagsAsync(workspace))
                return NotFound();

            var vmInvalid = await BuildIndexVmAsync(workspace, monthKey);
            vmInvalid.QuickAdd = model;
            return View("Index", vmInvalid);
        }

        var entity = new BookkeepingEntry
        {
            OwnerUserId = workspace.OwnerUserId,
            Scope = workspace.Scope,
            AgentUserId = agentOid,
            Type = model.Type,
            EntryDate = model.EntryDate.Date,
            Amount = model.Amount.GetValueOrDefault(),
            Category = model.Category,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        _db.BookkeepingEntries.Add(entity);
        await _db.SaveChangesAsync();

        var redirectKey = !string.IsNullOrWhiteSpace(monthKey) ? monthKey : entity.EntryDate.ToString("yyyy-MM");
        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, redirectKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEntry(int id, string? clientUserId, string? monthKey = null)
    {
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        var row = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (row != null)
        {
            _db.BookkeepingEntries.Remove(row);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, monthKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateEntry([Bind] EditBookkeepingEntryVm model, string? clientUserId, string? monthKey = null)
    {
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (model.Amount <= 0m)
            ModelState.AddModelError(nameof(model.Amount), "Amount is required.");

        if (!ModelState.IsValid)
        {
            if (!await SetWorkspaceBagsAsync(workspace))
                return NotFound();

            var vmInvalid = await BuildIndexVmAsync(workspace, monthKey);
            return View("Index", vmInvalid);
        }

        var row = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .SingleOrDefaultAsync(x => x.Id == model.Id);

        if (row == null) return NotFound();

        row.Type = model.Type;
        row.EntryDate = model.EntryDate.Date;
        row.Amount = model.Amount;
        row.Category = model.Category;
        row.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        row.AgentUserId = agentOid;
        row.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var redirectKey = !string.IsNullOrWhiteSpace(monthKey) ? monthKey : row.EntryDate.ToString("yyyy-MM");
        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, redirectKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRecurring([Bind(Prefix = "AddRecurring")] AddRecurringExpenseVm model, string? clientUserId, string? monthKey = null)
    {
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (!model.Amount.HasValue || model.Amount.Value <= 0m)
            ModelState.AddModelError("AddRecurring.Amount", "Amount is required.");

        if (!ModelState.IsValid)
        {
            if (!await SetWorkspaceBagsAsync(workspace))
                return NotFound();

            var vmInvalid = await BuildIndexVmAsync(workspace, monthKey);
            vmInvalid.AddRecurring = model;
            return View("Index", vmInvalid);
        }

        var entity = new RecurringExpense
        {
            OwnerUserId = workspace.OwnerUserId,
            Scope = workspace.Scope,
            AgentUserId = agentOid,
            Type = model.Type,
            Name = (model.Name ?? "").Trim(),
            Amount = model.Amount.GetValueOrDefault(),
            Frequency = model.Frequency,
            Category = model.Category,
            StartDate = model.StartDate.Date,
            NextDueDate = model.NextDueDate?.Date,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        _db.RecurringExpenses.Add(entity);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, monthKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRecurring([Bind] EditRecurringExpenseVm model, string? clientUserId, string? monthKey = null)
    {
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (model.Amount <= 0m)
            ModelState.AddModelError(nameof(model.Amount), "Amount is required.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "Name is required.");

        if (!ModelState.IsValid)
        {
            if (!await SetWorkspaceBagsAsync(workspace))
                return NotFound();

            var vmInvalid = await BuildIndexVmAsync(workspace, monthKey);
            return View("Index", vmInvalid);
        }

        var row = await RecurringFor(workspace.OwnerUserId, workspace.Scope)
            .SingleOrDefaultAsync(x => x.Id == model.Id);

        if (row == null) return NotFound();

        row.Type = model.Type;
        row.Name = model.Name.Trim();
        row.Amount = model.Amount;
        row.Frequency = model.Frequency;
        row.Category = model.Category;
        row.StartDate = model.StartDate.Date;
        row.NextDueDate = model.NextDueDate?.Date;
        row.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        row.IsActive = model.IsActive;
        row.AgentUserId = agentOid;
        row.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, monthKey));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRecurring(int id, string? clientUserId, string? monthKey = null)
    {
        if (!string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId, monthKey });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        var row = await RecurringFor(workspace.OwnerUserId, workspace.Scope)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (row != null)
        {
            _db.RecurringExpenses.Remove(row);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), RouteForWorkspace(workspace, monthKey));
    }

    [HttpGet]
        public async Task<IActionResult> Reports(string? clientUserId)
        {
            if (!string.IsNullOrWhiteSpace(clientUserId))
                return RedirectToAction("BookKeepingReports", "ClientWorkspace", new { clientUserId });

            string agentOid;
            try { agentOid = GetAgentOidOrThrow(); }
            catch { return Challenge(); }

        var workspace = await ResolveWorkspaceAsync(agentOid, clientUserId);
        if (workspace == null) return Forbid();

        if (!await SetWorkspaceBagsAsync(workspace))
            return NotFound();

        var today = DateTime.Today;
        var yearStart = new DateTime(today.Year, 1, 1);
        var endExclusive = today.AddDays(1);
        var rangeEndInclusive = endExclusive.AddTicks(-1);

        var entries = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .Where(x => x.EntryDate >= yearStart && x.EntryDate < endExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var recurring = await RecurringFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();

        var incomeYtdEntries = entries.Where(x => x.Type == BookkeepingEntryType.Income).Sum(x => x.Amount);
        var expenseYtdEntries = entries.Where(x => x.Type == BookkeepingEntryType.Expense).Sum(x => x.Amount);

        var (recurringIncomeYtd, recurringExpenseYtd) =
            SumRecurringOccurrencesSplitInRange(recurring, yearStart, rangeEndInclusive);

        var incomeYtdAllIn = incomeYtdEntries + recurringIncomeYtd;
        var expensesYtdAllIn = expenseYtdEntries + recurringExpenseYtd;
        var netYtdAllIn = incomeYtdAllIn - expensesYtdAllIn;
        var profit = netYtdAllIn > 0 ? netYtdAllIn : 0m;

        var est = TaxEstimator2026.EstimateAzSelfEmployedReserve(
            profit,
            FilingStatus.Single,
            includeArizona: true
        );

        var vm = new BookKeepingReportsVm
        {
            StartDate = yearStart,
            EndDate = today,
            IncomeYtd = incomeYtdAllIn,
            ExpensesYtd = expensesYtdAllIn,
            NetYtd = netYtdAllIn,
            TaxRateUsed = 0m,
            TaxReserve = est.TotalEstimatedTax,
            FederalIncomeTaxYtd = est.FederalIncomeTax,
            SelfEmploymentTaxYtd = est.SeTax,
            ArizonaTaxYtd = est.ArizonaTax,
            EffectiveTaxRateYtd = est.EffectiveRate,
            Entries = entries,
            RecurringInRangeTotal = recurringIncomeYtd + recurringExpenseYtd
        };

        return View(vm);
    }

    private async Task<BookKeepingIndexVm> BuildIndexVmAsync(WorkspaceContext workspace, string? monthKey)
    {
        var today = DateTime.Today;
        var currentMonthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        var endExclusive = today.AddDays(1);
        var rangeEndInclusive = endExclusive.AddTicks(-1);

        var entriesYtd = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .Where(x => x.EntryDate >= yearStart && x.EntryDate < endExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var recurring = await RecurringFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();

        var earliestEntryDate = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .OrderBy(x => x.EntryDate)
            .Select(x => (DateTime?)x.EntryDate)
            .FirstOrDefaultAsync();

        var earliestRecurringDate = await RecurringFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .OrderBy(x => x.StartDate)
            .Select(x => (DateTime?)x.StartDate)
            .FirstOrDefaultAsync();

        var earliest = MinNullable(earliestEntryDate, earliestRecurringDate) ?? currentMonthStart;
        var requestedMonthStart = ParseMonthKeyOrDefault(monthKey, currentMonthStart);
        if (requestedMonthStart > currentMonthStart)
            requestedMonthStart = currentMonthStart;

        var selectedMonthStart = requestedMonthStart;
        var selectedMonthKey = selectedMonthStart.ToString("yyyy-MM");
        var monthOptions = BuildMonthOptions(
            new DateTime(earliest.Year, earliest.Month, 1),
            currentMonthStart,
            selectedMonthKey
        );

        var monthStart = selectedMonthStart;
        var monthEndExclusive = monthStart.AddMonths(1);

        var entriesForMonth = await EntriesFor(workspace.OwnerUserId, workspace.Scope)
            .AsNoTracking()
            .Where(x => x.EntryDate >= monthStart && x.EntryDate < monthEndExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var incomeYtdEntries = entriesYtd.Where(x => x.Type == BookkeepingEntryType.Income).Sum(x => x.Amount);
        var expenseYtdEntries = entriesYtd.Where(x => x.Type == BookkeepingEntryType.Expense).Sum(x => x.Amount);
        var (recIncomeOcc, recExpenseOcc) = SumRecurringOccurrencesSplit(recurring, yearStart, rangeEndInclusive);

        var incomeYtdAllIn = incomeYtdEntries + recIncomeOcc;
        var expensesYtdAllIn = expenseYtdEntries + recExpenseOcc;
        var netYtdAllIn = incomeYtdAllIn - expensesYtdAllIn;
        var profit = netYtdAllIn > 0 ? netYtdAllIn : 0m;

        var est = TaxEstimator2026.EstimateAzSelfEmployedReserve(
            profit,
            FilingStatus.Single,
            includeArizona: true
        );

        var recurringMonthlyIncomeTotal = recurring
            .Where(x => x.Type == BookkeepingEntryType.Income)
            .Sum(MonthlyEquivalent);

        var recurringMonthlyExpensesTotal = recurring
            .Where(x => x.Type == BookkeepingEntryType.Expense)
            .Sum(MonthlyEquivalent);

        return new BookKeepingIndexVm
        {
            RangeStart = yearStart,
            RangeEnd = today,
            SelectedMonthKey = selectedMonthKey,
            MonthOptions = monthOptions,
            RecentForMonth = entriesForMonth,
            IncomeYtd = incomeYtdAllIn,
            ExpensesYtd = expensesYtdAllIn,
            NetYtd = netYtdAllIn,
            IncomeMtd = 0m,
            ExpensesMtd = 0m,
            NetMtd = 0m,
            TaxReserve = est.TotalEstimatedTax,
            TaxRateUsed = 0m,
            FederalIncomeTaxYtd = est.FederalIncomeTax,
            SelfEmploymentTaxYtd = est.SeTax,
            ArizonaTaxYtd = est.ArizonaTax,
            EffectiveTaxRateYtd = est.EffectiveRate,
            RecurringYtdOccurrencesTotal = recExpenseOcc,
            RecurringMonthlyTotal = recurring.Sum(MonthlyEquivalent),
            RecurringMonthlyIncomeTotal = recurringMonthlyIncomeTotal,
            RecurringMonthlyExpensesTotal = recurringMonthlyExpensesTotal,
            RecurringMonthlyNet = recurringMonthlyIncomeTotal - recurringMonthlyExpensesTotal,
            Recent = entriesYtd.Take(12).ToList(),
            Recurring = recurring,
            QuickAdd = new AddBookkeepingEntryVm
            {
                EntryDate = today,
                Type = BookkeepingEntryType.Expense,
                Category = BookkeepingCategory.Other,
                Amount = null,
                Notes = null
            },
            AddRecurring = new AddRecurringExpenseVm
            {
                StartDate = today,
                Frequency = RecurrenceFrequency.Monthly,
                Category = BookkeepingCategory.Software,
                Amount = null,
                Notes = null
            }
        };
    }

    private static DateTime ParseMonthKeyOrDefault(string? monthKey, DateTime fallbackMonthStart)
    {
        if (string.IsNullOrWhiteSpace(monthKey)) return fallbackMonthStart;

        if (DateTime.TryParseExact(monthKey.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return new DateTime(dt.Year, dt.Month, 1);

        return fallbackMonthStart;
    }

    private static List<SelectListItem> BuildMonthOptions(DateTime fromMonthStart, DateTime toMonthStart, string selectedKey)
    {
        var list = new List<SelectListItem>();
        var cur = new DateTime(toMonthStart.Year, toMonthStart.Month, 1);
        var min = new DateTime(fromMonthStart.Year, fromMonthStart.Month, 1);

        while (cur >= min)
        {
            var key = cur.ToString("yyyy-MM");
            list.Add(new SelectListItem
            {
                Value = key,
                Text = cur.ToString("MMMM yyyy"),
                Selected = key == selectedKey
            });

            cur = cur.AddMonths(-1);
        }

        return list;
    }

    private static DateTime? MinNullable(DateTime? a, DateTime? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return a.Value <= b.Value ? a : b;
    }

    private static (decimal income, decimal expense) SumRecurringOccurrencesSplitInRange(
        List<RecurringExpense> recurring,
        DateTime rangeStart,
        DateTime rangeEnd)
        => SumRecurringOccurrencesSplit(recurring, rangeStart, rangeEnd);

    private static (decimal income, decimal expense) SumRecurringOccurrencesSplit(
        List<RecurringExpense> recurring,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        if (rangeEnd < rangeStart) return (0m, 0m);

        decimal income = 0m;
        decimal expense = 0m;

        foreach (var r in recurring)
        {
            if (!r.IsActive) continue;

            var start = r.StartDate.Date;
            if (start > rangeEnd) continue;

            var add = 0m;

            switch (r.Frequency)
            {
                case RecurrenceFrequency.Weekly:
                {
                    var first = start;

                    if (first < rangeStart)
                    {
                        var days = (rangeStart.Date - first.Date).Days;
                        var weeksToAdd = (int)Math.Ceiling(days / 7.0);
                        first = first.AddDays(weeksToAdd * 7);
                    }

                    for (var d = first; d <= rangeEnd; d = d.AddDays(7))
                    {
                        if (d >= rangeStart && d >= start)
                            add += r.Amount;
                    }

                    break;
                }

                case RecurrenceFrequency.Monthly:
                    add = SumMonthly(r, rangeStart, rangeEnd, 1);
                    break;

                case RecurrenceFrequency.Quarterly:
                    add = SumMonthly(r, rangeStart, rangeEnd, 3);
                    break;

                case RecurrenceFrequency.Annual:
                    add = SumMonthly(r, rangeStart, rangeEnd, 12);
                    break;
            }

            if (r.Type == BookkeepingEntryType.Income) income += add;
            else expense += add;
        }

        return (income, expense);
    }

    private static decimal SumMonthly(RecurringExpense r, DateTime rangeStart, DateTime rangeEnd, int stepMonths)
    {
        var start = r.StartDate.Date;

        int MonthsBetween(DateTime a, DateTime b) => (b.Year - a.Year) * 12 + (b.Month - a.Month);

        var iter = new DateTime(rangeStart.Year, rangeStart.Month, 1);
        var baseMonth = new DateTime(start.Year, start.Month, 1);

        var mb = MonthsBetween(baseMonth, iter);
        if (mb < 0) iter = baseMonth;
        else if (mb % stepMonths != 0) iter = iter.AddMonths(stepMonths - (mb % stepMonths));

        decimal total = 0m;

        while (iter <= rangeEnd)
        {
            var day = Math.Min(start.Day, DateTime.DaysInMonth(iter.Year, iter.Month));
            var occ = new DateTime(iter.Year, iter.Month, day);

            if (occ >= rangeStart && occ >= start && occ <= rangeEnd)
                total += r.Amount;

            iter = iter.AddMonths(stepMonths);
        }

        return total;
    }

    private static decimal MonthlyEquivalent(RecurringExpense r)
        => r.Frequency switch
        {
            RecurrenceFrequency.Weekly => Math.Round(r.Amount * 52m / 12m, 2),
            RecurrenceFrequency.Monthly => r.Amount,
            RecurrenceFrequency.Quarterly => Math.Round(r.Amount / 3m, 2),
            RecurrenceFrequency.Annual => Math.Round(r.Amount / 12m, 2),
            _ => r.Amount
        };
}
