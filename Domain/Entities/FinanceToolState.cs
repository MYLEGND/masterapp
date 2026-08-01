namespace Domain.Entities;

public class FinanceToolState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The authoritative household scope for shared financial state. The
    /// ClientProfileId remains the historical primary-owner reference only and
    /// must not be used to authorize a household member.
    /// </summary>
    public Guid? HouseholdAccountId { get; set; }

    public Guid ClientProfileId { get; set; }

    public string ToolId { get; set; } = "";

    public string JsonState { get; set; } = "{}";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
