using AgentPortal.Models;

namespace AgentPortal.Services;

public interface ILeadBridgeStateService
{
    LeadBridgeActiveState GetOrCreate(string agentUserId, string? queueKey, Func<(string? leadId, int position, int total)> initializer);
    LeadBridgeActiveState Update(string agentUserId, string? queueKey, string? activeLeadId, int position, int total, string? expectedVersion = null, string? deletedLeadId = null);
    /// <summary>Persist server-authoritative filter state and return updated state for broadcast.</summary>
    LeadBridgeActiveState UpdateFilters(string agentUserId, string? queueKey, string? filterState, string? expectedVersion = null);
}
