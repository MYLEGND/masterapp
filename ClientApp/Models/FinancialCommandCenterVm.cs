using System;
using System.Collections.Generic;

namespace ClientApp.Models;

public sealed class FinancialCommandCenterVm
{
    public string WorkspaceName { get; set; } = "Financial Command Center";
    public bool IsBusinessClient { get; set; }

    public bool IsQuickBooksConfigured { get; set; }
    public bool IsConnected { get; set; }
    public string? RealmId { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }

    public decimal RevenueMtd { get; set; }
    public decimal RevenueYtd { get; set; }
    public decimal ExpensesMtd { get; set; }
    public decimal ExpensesYtd { get; set; }
    public decimal NetProfitMtd { get; set; }
    public decimal NetProfitYtd { get; set; }
    public decimal CashPosition { get; set; }

    public int AccountsCount { get; set; }

    public List<ExpenseCategoryVm> TopExpenseCategories { get; set; } = new();
    public List<ProfitTrendPointVm> ProfitTrend { get; set; } = new();
    public List<FinancialTransactionVm> RecentTransactions { get; set; } = new();
}

public sealed class ExpenseCategoryVm
{
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class ProfitTrendPointVm
{
    public string MonthKey { get; set; } = "";
    public decimal NetProfit { get; set; }
}

public sealed class FinancialTransactionVm
{
    public DateTime Date { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Account { get; set; } = "";
    public decimal Amount { get; set; }
}
