using ClientApp.Models;
using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace ClientApp.Controllers;

[Authorize]
public class BookKeepingController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;

    public BookKeepingController(MasterAppDbContext db, EffectiveClientContextService clientContext)
    {
        _db = db;
        _clientContext = clientContext;
    }

    private Task<EffectiveClientContext?> GetClientContextAsync()
        => _clientContext.ResolveAsync(User, Request.Cookies);

    private static string ResolveRecordType(ClientProfile profile)
    {
        var raw = profile.CrmNotes;
        if (string.IsNullOrWhiteSpace(raw)) return "Lead";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("recordType", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString() ?? "";
                value = value.Trim();
                if (value.Equals("Business Client", StringComparison.OrdinalIgnoreCase))
                    return "BusinessClient";
                if (value.Equals("Client", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("BusinessClient", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Lead", StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }
        catch { /* ignore parse errors */ }

        return "Lead";
    }

    private static bool IsBusinessClient(EffectiveClientContext ctx)
        => string.Equals(ResolveRecordType(ctx.Profile), "BusinessClient", StringComparison.OrdinalIgnoreCase);

    // ✅ One place for the “client shared” filter so it can’t get inconsistent.
    private IQueryable<BookkeepingEntry> ClientEntries(string ownerId)
        => _db.BookkeepingEntries.Where(x => x.OwnerUserId == ownerId && x.Scope == BookkeepingScope.ClientShared);

    private IQueryable<RecurringExpense> ClientRecurring(string ownerId)
        => _db.RecurringExpenses.Where(x => x.OwnerUserId == ownerId && x.Scope == BookkeepingScope.ClientShared);

    // =========================================================
    // ✅ SURGICAL FIX: EDIT VMs (prevents OwnerUserId required)
    // =========================================================
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

    // -------------------- GET: /BookKeeping?monthKey=yyyy-MM --------------------
    [HttpGet]
    public async Task<IActionResult> Index(string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null || !IsBusinessClient(context)) return Forbid();
        return View(await BuildIndexVmAsync(context.ClientUserId, monthKey));
    }

    // -------------------- POST: Add Entry (binds QuickAdd.*) --------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEntry([Bind(Prefix = "QuickAdd")] AddBookkeepingEntryVm model, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null || !IsBusinessClient(context)) return Forbid();
        var ownerId = context.ClientUserId;

        if (!model.Amount.HasValue || model.Amount.Value <= 0m)
            ModelState.AddModelError("QuickAdd.Amount", "Amount is required.");

        if (!ModelState.IsValid)
        {
            var vmInvalid = await BuildIndexVmAsync(ownerId, monthKey);
            vmInvalid.QuickAdd = model;
            return View("Index", vmInvalid);
        }

        var entity = new BookkeepingEntry
        {
            OwnerUserId = ownerId,
            Scope = BookkeepingScope.ClientShared,
            AgentUserId = null,

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

        // Stay on currently selected month (or auto-switch to the entry’s month if none selected)
        var redirectKey = !string.IsNullOrWhiteSpace(monthKey) ? monthKey : entity.EntryDate.ToString("yyyy-MM");
        return RedirectToAction(nameof(Index), new { monthKey = redirectKey });
    }

    // -------------------- POST: Delete Entry --------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEntry(int id, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null || !IsBusinessClient(context)) return Forbid();
        var ownerId = context.ClientUserId;

        var row = await ClientEntries(ownerId)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (row != null)
        {
            _db.BookkeepingEntries.Remove(row);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), new { monthKey });
    }

    // =========================================================
    // ✅ SURGICAL FIX: Update Entry (EDIT) - VM binding only
    // =========================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateEntry([Bind] EditBookkeepingEntryVm model, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null) return Forbid();
        var ownerId = context.ClientUserId;

        if (model.Amount <= 0m)
            ModelState.AddModelError(nameof(model.Amount), "Amount is required.");

        if (!ModelState.IsValid)
        {
            var vmInvalid = await BuildIndexVmAsync(ownerId, monthKey);
            return View("Index", vmInvalid);
        }

        var row = await ClientEntries(ownerId)
            .SingleOrDefaultAsync(x => x.Id == model.Id);

        if (row == null) return NotFound();

        // Only editable fields
        row.Type = model.Type;
        row.EntryDate = model.EntryDate.Date;
        row.Amount = model.Amount;
        row.Category = model.Category;
        row.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        row.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var redirectKey = !string.IsNullOrWhiteSpace(monthKey) ? monthKey : row.EntryDate.ToString("yyyy-MM");
        return RedirectToAction(nameof(Index), new { monthKey = redirectKey });
    }

    // -------------------- POST: Add Recurring (binds AddRecurring.*) --------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRecurring([Bind(Prefix = "AddRecurring")] AddRecurringExpenseVm model, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null) return Forbid();
        var ownerId = context.ClientUserId;

        if (!model.Amount.HasValue || model.Amount.Value <= 0m)
            ModelState.AddModelError("AddRecurring.Amount", "Amount is required.");

        if (!ModelState.IsValid)
        {
            var vmInvalid = await BuildIndexVmAsync(ownerId, monthKey);
            vmInvalid.AddRecurring = model;
            return View("Index", vmInvalid);
        }

        var entity = new RecurringExpense
        {
            OwnerUserId = ownerId,
            Scope = BookkeepingScope.ClientShared,
            AgentUserId = null,

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

        return RedirectToAction(nameof(Index), new { monthKey });
    }

    // =========================================================
    // ✅ SURGICAL FIX: Update Recurring (EDIT) - VM binding only
    // =========================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRecurring([Bind] EditRecurringExpenseVm model, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null) return Forbid();
        var ownerId = context.ClientUserId;

        if (model.Amount <= 0m)
            ModelState.AddModelError(nameof(model.Amount), "Amount is required.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "Name is required.");

        if (!ModelState.IsValid)
        {
            var vmInvalid = await BuildIndexVmAsync(ownerId, monthKey);
            return View("Index", vmInvalid);
        }

        var row = await ClientRecurring(ownerId)
            .SingleOrDefaultAsync(x => x.Id == model.Id);

        if (row == null) return NotFound();

        // ✅ IMPORTANT: update Type too (prevents “income becomes expense” if form posts correctly)
        row.Type = model.Type;

        row.Name = model.Name.Trim();
        row.Amount = model.Amount;
        row.Frequency = model.Frequency;
        row.Category = model.Category;
        row.StartDate = model.StartDate.Date;
        row.NextDueDate = model.NextDueDate?.Date;
        row.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        row.IsActive = model.IsActive;

        row.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { monthKey });
    }

    // -------------------- POST: Delete Recurring --------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRecurring(int id, string? monthKey = null)
    {
        var context = await GetClientContextAsync();
        if (context == null) return Forbid();
        var ownerId = context.ClientUserId;

        var row = await ClientRecurring(ownerId)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (row != null)
        {
            _db.RecurringExpenses.Remove(row);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), new { monthKey });
    }

    // -------------------- GET: /BookKeeping/Reports (YTD) --------------------
    [HttpGet]
    public async Task<IActionResult> Reports()
    {
        var context = await GetClientContextAsync();
        if (context == null) return Forbid();
        var ownerId = context.ClientUserId;

        var today = DateTime.Today;
        var yearStart = new DateTime(today.Year, 1, 1);
        var endExclusive = today.AddDays(1);
        var rangeEndInclusive = endExclusive.AddTicks(-1);

        var entries = await ClientEntries(ownerId)
            .AsNoTracking()
            .Where(x => x.EntryDate >= yearStart && x.EntryDate < endExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var recurring = await ClientRecurring(ownerId)
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

        var recurringYtdOccurrencesTotal = recurringIncomeYtd + recurringExpenseYtd;

        var vm = new BookKeepingReportsVm
        {
            StartDate = yearStart,
            EndDate = today,

            IncomeYtd = incomeYtdAllIn,      // ✅ include recurring income occurrences
            ExpensesYtd = expensesYtdAllIn,  // ✅ include recurring expense occurrences
            NetYtd = netYtdAllIn,

            TaxRateUsed = 0m,
            TaxReserve = est.TotalEstimatedTax,

            FederalIncomeTaxYtd = est.FederalIncomeTax,
            SelfEmploymentTaxYtd = est.SeTax,
            ArizonaTaxYtd = est.ArizonaTax,
            EffectiveTaxRateYtd = est.EffectiveRate,

            Entries = entries,
            RecurringInRangeTotal = recurringYtdOccurrencesTotal
        };

        return View(vm);
    }

    // -------------------------------------------------------
    // SINGLE SOURCE OF TRUTH — YTD ALL-IN (Index) + MONTH FILTER
    // -------------------------------------------------------
    private async Task<BookKeepingIndexVm> BuildIndexVmAsync(string ownerId, string? monthKey)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return new BookKeepingIndexVm();

        var today = DateTime.Today;
        var currentMonthStart = new DateTime(today.Year, today.Month, 1);

        var yearStart = new DateTime(today.Year, 1, 1);
        var endExclusive = today.AddDays(1);
        var rangeEndInclusive = endExclusive.AddTicks(-1);

        // Pull YTD entries (single source of truth remains)
        var entriesYtd = await ClientEntries(ownerId)
            .AsNoTracking()
            .Where(x => x.EntryDate >= yearStart && x.EntryDate < endExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var recurring = await ClientRecurring(ownerId)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();

        // ===== Month dropdown source (need earliest entry/recurring across all time, not just YTD) =====
        var earliestEntryDate = await ClientEntries(ownerId)
            .AsNoTracking()
            .OrderBy(x => x.EntryDate)
            .Select(x => (DateTime?)x.EntryDate)
            .FirstOrDefaultAsync();

        var earliestRecurringDate = await ClientRecurring(ownerId)
            .AsNoTracking()
            .OrderBy(x => x.StartDate)
            .Select(x => (DateTime?)x.StartDate)
            .FirstOrDefaultAsync();

        var earliest = MinNullable(earliestEntryDate, earliestRecurringDate) ?? currentMonthStart;

        // ✅ Parse requested month
        var requestedMonthStart = ParseMonthKeyOrDefault(monthKey, currentMonthStart);

        // ✅ HARD RULE: never allow future month selection (clamp)
        if (requestedMonthStart > currentMonthStart)
            requestedMonthStart = currentMonthStart;

        var selectedMonthStart = requestedMonthStart;
        var selectedMonthKey = selectedMonthStart.ToString("yyyy-MM");

        // ✅ HARD RULE: dropdown shows ONLY up to the current month (NO FUTURE BUFFER)
        var monthOptions = BuildMonthOptions(
            new DateTime(earliest.Year, earliest.Month, 1),
            currentMonthStart,
            selectedMonthKey
        );

        // ===== Selected month entries for the table (NOT limited to YTD) =====
        var monthStart = selectedMonthStart;
        var monthEndExclusive = monthStart.AddMonths(1);

        var entriesForMonth = await ClientEntries(ownerId)
            .AsNoTracking()
            .Where(x => x.EntryDate >= monthStart && x.EntryDate < monthEndExclusive)
            .OrderByDescending(x => x.EntryDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        // ===== Compute YTD ALL-IN =====
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

        // ===== Monthly Recurring Summary (split + net) =====
        var recurringMonthlyIncomeTotal = recurring
            .Where(x => x.Type == BookkeepingEntryType.Income)
            .Sum(MonthlyEquivalent);

        var recurringMonthlyExpensesTotal = recurring
            .Where(x => x.Type == BookkeepingEntryType.Expense)
            .Sum(MonthlyEquivalent);

        var recurringMonthlyNet = recurringMonthlyIncomeTotal - recurringMonthlyExpensesTotal;

        // keep existing total too (if other views/old UI references it)
        var recurringMonthlyTotal = recurring.Sum(MonthlyEquivalent);

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

            // ✅ your existing behavior: this stores EXPENSE occurrences only (kept)
            RecurringYtdOccurrencesTotal = recExpenseOcc,
            RecurringMonthlyTotal = recurringMonthlyTotal,

            // ✅ Monthly Recurring Summary boxes
            RecurringMonthlyIncomeTotal = recurringMonthlyIncomeTotal,
            RecurringMonthlyExpensesTotal = recurringMonthlyExpensesTotal,
            RecurringMonthlyNet = recurringMonthlyNet,

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

        // newest -> oldest
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

    // -------------------------------------------------------
    // ✅ Provide the method name your Reports() calls
    // -------------------------------------------------------
    private static (decimal income, decimal expense) SumRecurringOccurrencesSplitInRange(
        List<RecurringExpense> recurring,
        DateTime rangeStart,
        DateTime rangeEnd)
        => SumRecurringOccurrencesSplit(recurring, rangeStart, rangeEnd);

    // -------------------------------------------------------
    // Recurring occurrences SUM in a date range (counts actual occurrences) - split
    // -------------------------------------------------------
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
                        if (d >= rangeStart && d >= start)
                            add += r.Amount;

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

    private static decimal SumRecurringOccurrencesInRange(List<RecurringExpense> recurring, DateTime rangeStart, DateTime rangeEnd)
    {
        if (rangeEnd < rangeStart) return 0m;

        decimal total = 0m;

        foreach (var r in recurring)
        {
            if (!r.IsActive) continue;

            var start = r.StartDate.Date;
            if (start > rangeEnd) continue;

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
                        if (d >= rangeStart && d >= start)
                            total += r.Amount;

                    break;
                }

                case RecurrenceFrequency.Monthly:
                    total += SumMonthly(r, rangeStart, rangeEnd, 1);
                    break;

                case RecurrenceFrequency.Quarterly:
                    total += SumMonthly(r, rangeStart, rangeEnd, 3);
                    break;

                case RecurrenceFrequency.Annual:
                    total += SumMonthly(r, rangeStart, rangeEnd, 12);
                    break;
            }
        }

        return total;
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
