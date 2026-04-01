using System;
using System.ComponentModel.DataAnnotations;

namespace Domain.Entities;

public enum RecurrenceFrequency
{
    Weekly = 0,
    Monthly = 1,
    Quarterly = 2,
    Annual = 3
}

public class RecurringExpense
{
    public int Id { get; set; }

    // ✅ HARD DATA OWNER (workspace boundary)
    // ClientShared → OwnerUserId = CLIENT OID
    // AgentPrivate → OwnerUserId = AGENT OID
    [Required, MaxLength(450)]
    public string OwnerUserId { get; set; } = "";

    // ✅ MUST match BookkeepingScope enum (stored as int in SQL)
    [Required]
    public BookkeepingScope Scope { get; set; } = BookkeepingScope.ClientShared;

    // Optional audit (who created/edited) — NOT used for filtering/security
    [MaxLength(450)]
    public string? AgentUserId { get; set; }

    // ✅ NEW: supports recurring income + recurring expense
    [Required]
    public BookkeepingEntryType Type { get; set; } = BookkeepingEntryType.Expense;

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    // Stored as positive; Type determines whether it’s income or expense
    [Required]
    public decimal Amount { get; set; }

    [Required]
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Monthly;

    [Required]
    public BookkeepingCategory Category { get; set; } = BookkeepingCategory.Other;

    public DateTime StartDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? NextDueDate { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(240)]
    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
