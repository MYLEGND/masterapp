using System;
using System.ComponentModel.DataAnnotations;

namespace Domain.Entities;

public enum BookkeepingEntryType
{
    Expense = 0,
    Income = 1
}

public enum BookkeepingCategory
{
    Marketing = 0,
    Software = 1,
    LicensingCompliance = 2,
    Education = 3,
    Travel = 4,
    Meals = 5,
    Office = 6,
    ContractorVA = 7,
    Insurance = 8,
    Other = 9
}

public class BookkeepingEntry
{
    public int Id { get; set; }

    // ✅ HARD DATA-ISOLATION KEY (ENTRA OID)
    // ClientShared rows: OwnerUserId = Client OID
    // AgentPrivate rows: OwnerUserId = Agent OID
    [Required, MaxLength(450)]
    public string OwnerUserId { get; set; } = "";

    // ✅ Prevents mixing agent-only with client-shared
    [Required]
    public BookkeepingScope Scope { get; set; } = BookkeepingScope.ClientShared;

    // Optional audit (who performed the action) — NOT security boundary
    [MaxLength(450)]
    public string? AgentUserId { get; set; }

    [Required]
    public BookkeepingEntryType Type { get; set; } = BookkeepingEntryType.Expense;

    [Required]
    public DateTime EntryDate { get; set; } // store date (treated as local date)

    [Required]
    public decimal Amount { get; set; } // positive value

    [Required]
    public BookkeepingCategory Category { get; set; } = BookkeepingCategory.Other;

    [MaxLength(240)]
    public string? Notes { get; set; }

    // Optional link to recurring
    public int? RecurringExpenseId { get; set; }
    public RecurringExpense? RecurringExpense { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
