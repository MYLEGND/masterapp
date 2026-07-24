namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// Links a detected recurring stream to an existing stable item inside the
/// authoritative Expense Lens FinanceToolState JSON.
/// </summary>
public sealed class ExpenseLensStreamLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public Guid RecurringFinancialStreamId { get; set; }

    public string ExpenseLensToolId { get; set; } = "ExpenseLens";

    public string ExpenseLensItemId { get; set; } = "";

    public string Status { get; set; } = "Suggested";

    public string? ConfirmedByUserId { get; set; }

    public DateTime? ConfirmedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
