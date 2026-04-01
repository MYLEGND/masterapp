using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AgentPortal.Models;

public class AddBookkeepingEntryVm
{
    [Required]
    public BookkeepingEntryType Type { get; set; } = BookkeepingEntryType.Expense;

    [Required]
    public DateTime EntryDate { get; set; } = DateTime.Today;

    [Required, Range(0.01, 999999999)]
    public decimal? Amount { get; set; }

    [Required]
    public BookkeepingCategory Category { get; set; } = BookkeepingCategory.Other;

    [MaxLength(240)]
    public string? Notes { get; set; }
}

public class AddRecurringExpenseVm
{
    // ✅ NEW: supports recurring income + recurring expense
    [Required]
    public BookkeepingEntryType Type { get; set; } = BookkeepingEntryType.Expense;

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [Required, Range(0.01, 999999999)]
    public decimal? Amount { get; set; }

    [Required]
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Monthly;

    [Required]
    public BookkeepingCategory Category { get; set; } = BookkeepingCategory.Software;

    public DateTime StartDate { get; set; } = DateTime.Today;

    public DateTime? NextDueDate { get; set; }

    [MaxLength(240)]
    public string? Notes { get; set; }
}

/// <summary>
/// Pure display row for recurring list (no math in the view).
/// </summary>
public class RecurringCostRowVm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Monthly { get; set; }
}

public class BookKeepingIndexVm
{
    // ✅ Range for transparency (YTD)
    public DateTime RangeStart { get; set; }
    public DateTime RangeEnd { get; set; }

    // ✅ YTD totals (ALL-IN: entries + recurring occurrences)
    public decimal IncomeYtd { get; set; }
    public decimal ExpensesYtd { get; set; }
    public decimal NetYtd { get; set; }

    // (Keep these for backward compatibility; not used in YTD UI)
    public decimal IncomeMtd { get; set; }
    public decimal ExpensesMtd { get; set; }
    public decimal NetMtd { get; set; }

    public decimal TaxReserve { get; set; }
    public decimal TaxRateUsed { get; set; } = 0.25m;

    // ✅ Tax breakdown (YTD)
    public decimal FederalIncomeTaxYtd { get; set; }
    public decimal SelfEmploymentTaxYtd { get; set; }
    public decimal ArizonaTaxYtd { get; set; }
    public decimal EffectiveTaxRateYtd { get; set; } // TotalTax / Profit

    // ✅ Recurring totals (kept)
    public decimal RecurringMonthlyTotal { get; set; }
    public decimal RecurringYtdOccurrencesTotal { get; set; }
    public List<RecurringCostRowVm> RecurringRows { get; set; } = new();

    // ✅ NEW: recurring income/expense breakdown for the UI
    public decimal RecurringMonthlyIncomeTotal { get; set; }
    public decimal RecurringMonthlyExpensesTotal { get; set; }
    public decimal RecurringMonthlyNet { get; set; }

    // ✅ Month selector
    public string SelectedMonthKey { get; set; } = ""; // "yyyy-MM"
    public List<SelectListItem> MonthOptions { get; set; } = new();
    public List<BookkeepingEntry> RecentForMonth { get; set; } = new();

    // Tables (legacy / other screens still use these)
    public List<BookkeepingEntry> Recent { get; set; } = new();
    public List<RecurringExpense> Recurring { get; set; } = new();

    // Forms
    public AddBookkeepingEntryVm QuickAdd { get; set; } = new();
    public AddRecurringExpenseVm AddRecurring { get; set; } = new();
}
