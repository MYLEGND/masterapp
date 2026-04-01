using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Domain.Entities;

public enum ProductionSide
{
    Lead = 0,
    Client = 1
}

public enum ProductionStatus
{
    Submitted = 0,
    Issued = 1,
    Paid = 2
}

/// <summary>
/// Source-of-truth record for production / revenue. One row per deal.
/// Counts are mutually exclusive by current Status to avoid double-counting.
/// </summary>
public class ProductionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(450)]
    public string AgentUserId { get; set; } = ""; // OID owner

    public ProductionSide Side { get; set; }
    public ProductionStatus Status { get; set; } = ProductionStatus.Submitted;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PersonalAmount { get; set; }

    [MaxLength(128)]
    public string? LeadId { get; set; }

    [MaxLength(450)]
    public string? ClientUserId { get; set; }

    [MaxLength(240)]
    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
