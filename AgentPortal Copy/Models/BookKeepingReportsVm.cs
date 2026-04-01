using System;
using System.Collections.Generic;
using Domain.Entities;

namespace AgentPortal.Models;

public class MonthlyAllInVm
{
    public string MonthKey { get; set; } = "";        // "yyyy-MM"
    public DateTime MonthStart { get; set; }
    public DateTime MonthEnd { get; set; }            // inclusive end for display

    // Entries-only
    public decimal EntryIncome { get; set; }
    public decimal EntryExpenses { get; set; }

    // ✅ Recurring occurrences INSIDE this month (split by type)
    public decimal RecurringIncomeOccurrences { get; set; }
    public decimal RecurringExpenseOccurrences { get; set; }

    // ✅ Transparency helpers (optional but useful in the view)
    public decimal RecurringOccurrencesTotal => RecurringIncomeOccurrences + RecurringExpenseOccurrences;

    // ✅ All-in
    public decimal AllInIncome => EntryIncome + RecurringIncomeOccurrences;
    public decimal AllInExpenses => EntryExpenses + RecurringExpenseOccurrences;
    public decimal AllInNet => AllInIncome - AllInExpenses;
}


public class BookKeepingReportsVm
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    // ✅ YTD all-in totals (entries + recurring occurrences)
    public decimal IncomeYtd { get; set; }
    public decimal ExpensesYtd { get; set; }
    public decimal NetYtd { get; set; }

    public decimal TaxReserve { get; set; }
    public decimal TaxRateUsed { get; set; } = 0.25m; // legacy

    // ✅ Tax breakdown (YTD)
    public decimal FederalIncomeTaxYtd { get; set; }
    public decimal SelfEmploymentTaxYtd { get; set; }
    public decimal ArizonaTaxYtd { get; set; }
    public decimal EffectiveTaxRateYtd { get; set; }

    // ✅ Entries list (raw)
    public List<BookkeepingEntry> Entries { get; set; } = new();

    // ✅ Transparency: total recurring occurrences inside date range (YTD)
    public decimal RecurringInRangeTotal { get; set; }

    // ✅ Month-by-month ALL-IN reconciliation
    public List<MonthlyAllInVm> Months { get; set; } = new();

    // ✅ Optional: routing consistency (Back button / query preservation)
    public string? ClientOid { get; set; }
}
