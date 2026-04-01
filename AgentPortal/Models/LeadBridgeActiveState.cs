using System;

namespace AgentPortal.Models;

/// <summary>
/// Server-owned active lead state per agent. This is the single source of truth for cross-device sync.
/// </summary>
public class LeadBridgeActiveState
{
    public string AgentUserId { get; set; } = string.Empty;
    public string? ActiveLeadId { get; set; }
    public int Position { get; set; }
    public int Total { get; set; }
    public string? QueueKey { get; set; }
    public string Version { get; set; } = string.Empty; // rowversion/concurrency token
    public DateTime UtcUpdated { get; set; } = DateTime.UtcNow;
    /// <summary>Server-canonical JSON filter state for this agent+queue: { state, stage, calls, search }.</summary>
    public string? FilterState { get; set; }
}
