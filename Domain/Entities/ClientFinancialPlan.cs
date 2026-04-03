using System;

namespace Domain.Entities;

/// <summary>
/// Unified, server-backed financial plan (Accumulation + Distribution) per client.
/// Single row per ClientProfileId; Version enables future optimistic checks.
/// </summary>
public class ClientFinancialPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public string JsonData { get; set; } = "{}";

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public string UpdatedBy { get; set; } = "";

    public int Version { get; set; } = 1;

    public bool IsDeleted { get; set; } = false;
}
