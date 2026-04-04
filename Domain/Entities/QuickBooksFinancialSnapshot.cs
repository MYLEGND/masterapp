using System;
using System.ComponentModel.DataAnnotations;

namespace Domain.Entities;

public class QuickBooksFinancialSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(450)]
    public string OwnerUserId { get; set; } = "";

    [Required, MaxLength(128)]
    public string RealmId { get; set; } = "";

    public DateTime SyncedUtc { get; set; } = DateTime.UtcNow;

    public decimal RevenueMtd { get; set; }
    public decimal RevenueYtd { get; set; }
    public decimal ExpensesMtd { get; set; }
    public decimal ExpensesYtd { get; set; }
    public decimal NetProfitMtd { get; set; }
    public decimal NetProfitYtd { get; set; }
    public decimal CashPosition { get; set; }

    [Required, MaxLength(64)]
    public string SourceTag { get; set; } = "quickbooks_cache";

    public int AccountsCount { get; set; }

    public string? TopExpenseCategoriesJson { get; set; }
    public string? ProfitTrendJson { get; set; }
    public string? RecentTransactionsJson { get; set; }
}
