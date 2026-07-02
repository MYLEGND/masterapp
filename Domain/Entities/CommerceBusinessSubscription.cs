namespace Domain.Entities;

public sealed class CommerceBusinessSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string PlanKey { get; set; } = "starter";
    public string PlanName { get; set; } = "Starter";
    public string Status { get; set; } = "Trial";

    public int MonthlyPriceCents { get; set; }
    public string BillingProvider { get; set; } = "Manual";
    public string? BillingCustomerId { get; set; }
    public string? BillingSubscriptionId { get; set; }

    public DateTime? TrialEndsUtc { get; set; }
    public DateTime? CurrentPeriodEndsUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
