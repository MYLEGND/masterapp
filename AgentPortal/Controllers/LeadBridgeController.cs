using AgentPortal.Hubs;
using AgentPortal.Models;
using AgentPortal.Services;
using AgentPortal.Helpers;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[ApiController]
[Route("LeadBridge")]
public class LeadBridgeController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly ILeadBridgeStateService _stateService;
    private readonly IHubContext<LeadBridgeHub> _hub;
    private readonly EffectiveAgentContext _agentContext;

    public LeadBridgeController(MasterAppDbContext db, ILeadBridgeStateService stateService, IHubContext<LeadBridgeHub> hub, EffectiveAgentContext agentContext)
    {
        _db = db;
        _stateService = stateService;
        _hub = hub;
        _agentContext = agentContext;
    }

    private static string Norm(string? v) => (v ?? "").Trim();

    private static string? NormalizeBucket(string? bucket)
    {
        return WorkstationLeadBuckets.NormalizeBucket(bucket);
    }

    private static string? ResolveLeadQueueBucket(WorkstationLeadProfile lead, string? fallbackBucket = null)
        => NormalizeBucket(lead.OriginalLeadType)
           ?? NormalizeBucket(lead.Bucket)
           ?? fallbackBucket;

    private static string[] ExpandQueueBucketValues(string normalizedQueue)
        => WorkstationLeadBuckets.ExpandLifeWorkstationQueueValues(normalizedQueue);

    private string GetAgentId()
    {
        var eff = _agentContext.EffectiveAgentOid;
        if (!string.IsNullOrWhiteSpace(eff)) return eff.Trim();

        var agentId = (User?.GetStableUserId() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Missing agent id");
        return agentId;
    }

    private async Task<List<WorkstationLeadProfile>> GetOrderedLeads(string agentId, string? queueKey)
    {
        var normalizedQueue = NormalizeBucket(queueKey);
        var query = _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId);

        // Default list should not surface NotInterested unless explicitly requested.
        if (string.IsNullOrWhiteSpace(normalizedQueue))
        {
            query = query.Where(x =>
                (x.Bucket == null || x.Bucket.ToLower() != "notinterested") &&
                (x.CrmStage == null || x.CrmStage.ToLower() != "notinterested"));
        }

        if (!string.IsNullOrWhiteSpace(normalizedQueue))
        {
            var queueValues = ExpandQueueBucketValues(normalizedQueue);
            query = query.Where(x =>
                (x.OriginalLeadType != null && queueValues.Contains(x.OriginalLeadType)) ||
                ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket != null && queueValues.Contains(x.Bucket)));
        }

        var rows = await query.ToListAsync();

        rows = LeadCanonicalizer.Canonicalize(rows, null, "LeadBridge queue")
            .OrderBy(x => x.CallCount)                               // fewest calls first
            .ThenByDescending(WorkstationLeadOrder.ResolveSortValue) // then highest order
            .ToList();

        return rows;
    }

    private static LeadBridgeActiveState ToStatePayload(LeadBridgeActiveState state, string? deletedLeadId = null)
    {
        return new LeadBridgeActiveState
        {
            AgentUserId = state.AgentUserId,
            ActiveLeadId = state.ActiveLeadId,
            Position = state.Position,
            Total = state.Total,
            QueueKey = state.QueueKey,
            Version = state.Version,
            UtcUpdated = state.UtcUpdated
        };
    }

    private async Task<IActionResult> BroadcastAndReturn(string agentId, LeadBridgeActiveState state, string? deletedLeadId = null)
    {
        var payload = new
        {
            state.ActiveLeadId,
            state.Position,
            state.Total,
            state.QueueKey,
            state.Version,
            state.FilterState,
            DeletedLeadId = deletedLeadId
        };
        await _hub.Clients.Group(LeadBridgeHub.GroupName(agentId)).SendAsync("LeadChanged", payload);
        return Ok(payload);
    }

    [HttpGet("Active")]
    public async Task<IActionResult> Active([FromQuery] string? queueKey)
    {
        var agentId = GetAgentId();
        var leads = await GetOrderedLeads(agentId, queueKey);
        var state = _stateService.GetOrCreate(agentId, queueKey, () =>
        {
            var first = leads.FirstOrDefault();
            return (first?.LeadId, first == null ? 0 : 1, leads.Count);
        });
        return Ok(new
        {
            state.ActiveLeadId,
            state.Position,
            state.Total,
            state.QueueKey,
            state.Version,
            state.FilterState
        });
    }

    public record LeadBridgeSelectRequest(string? LeadId, string? QueueKey, string? Version);

    [HttpPost("Select")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select([FromForm] LeadBridgeSelectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LeadId)) return BadRequest("Lead id required");
        var agentId = GetAgentId();
        var leads = await GetOrderedLeads(agentId, req.QueueKey);
        var idx = leads.FindIndex(x => x.LeadId == req.LeadId);
        if (idx < 0) return NotFound("Lead not found in queue");

        var state = _stateService.Update(agentId, req.QueueKey, req.LeadId, idx + 1, leads.Count, req.Version);
        return await BroadcastAndReturn(agentId, state);
    }

    public record LeadBridgeNextRequest(string? QueueKey, string? Version);

    [HttpPost("Next")]
    [IgnoreAntiforgeryToken] // prevent stale/missing token from blocking Next; auth still required
    public async Task<IActionResult> Next([FromForm] LeadBridgeNextRequest req)
    {
        var agentId = GetAgentId();
        var leads = await GetOrderedLeads(agentId, req.QueueKey);
        if (leads.Count == 0) return Ok(new { ActiveLeadId = (string?)null, Position = 0, Total = 0, QueueKey = req.QueueKey, Version = req.Version });

        var current = _stateService.GetOrCreate(agentId, req.QueueKey, () => (leads.First().LeadId, 1, leads.Count));

        // If caller is behind, do not advance. Return canonical state so client reconciles first.
        if (!string.IsNullOrWhiteSpace(req.Version) && !string.Equals(current.Version, req.Version, StringComparison.Ordinal))
        {
            return await BroadcastAndReturn(agentId, current);
        }

        var idx = leads.FindIndex(x => x.LeadId == current.ActiveLeadId);
        if (idx < 0) idx = 0;
        var nextIdx = (idx + 1) % leads.Count;
        var nextLead = leads[nextIdx];

        // Advance from the canonical authoritative state after caller version has been reconciled.
        var state = _stateService.Update(agentId, req.QueueKey, nextLead.LeadId, nextIdx + 1, leads.Count, expectedVersion: null);
        return await BroadcastAndReturn(agentId, state);
    }

    public record LeadBridgeDeleteRequest(string? LeadId, string? QueueKey, string? Version);

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete([FromForm] LeadBridgeDeleteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LeadId)) return BadRequest("Lead id required");
        var agentId = GetAgentId();

        var lead = await _db.WorkstationLeadProfiles.FirstOrDefaultAsync(x => x.LeadId == req.LeadId && x.AgentUserId == agentId);
        if (lead == null) return NotFound();

        _db.WorkstationLeadProfiles.Remove(lead);
        await _db.SaveChangesAsync();

        var leads = await GetOrderedLeads(agentId, req.QueueKey);
        var nextLead = leads.FirstOrDefault();
        var state = _stateService.Update(agentId, req.QueueKey, nextLead?.LeadId, nextLead == null ? 0 : 1, leads.Count, req.Version, req.LeadId);
        return await BroadcastAndReturn(agentId, state, req.LeadId);
    }

    public record SetFiltersRequest(string? QueueKey, string? FilterState, string? Version);

    [HttpPost("SetFilters")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SetFilters([FromForm] SetFiltersRequest req)
    {
        var agentId = GetAgentId();
        var state = _stateService.UpdateFilters(agentId, req.QueueKey, req.FilterState, req.Version);
        return await BroadcastAndReturn(agentId, state);
    }
}
