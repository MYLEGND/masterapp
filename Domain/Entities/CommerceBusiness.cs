namespace Domain.Entities;

/// <summary>
/// The authoritative tenant/business scope for a commerce operation.
/// Parfait is the first seeded business; every future storefront, team,
/// catalog, order, customer, automation, analytics, and Meta connection
/// will resolve through this scope.
/// </summary>
public sealed class CommerceBusiness
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable machine identifier, e.g. "parfait". Immutable after creation.</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string BusinessType { get; set; } = "Ecommerce";

    /// <summary>Primary public storefront host, without protocol.</summary>
    public string? PrimaryDomain { get; set; }

    public string Status { get; set; } = "Active";
    public bool IsActive { get; set; } = true;

    /// <summary>Founder-controlled business scope. Never stores secrets.</summary>
    public string OwnerEmail { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
