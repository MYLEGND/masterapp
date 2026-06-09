using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace Infrastructure.Data;

/// <summary>
/// Shared DB ownership checks used across portals.
/// These methods intentionally use normalized key comparisons to prevent
/// case/format drift from creating authorization gaps.
/// </summary>
public static class OwnershipQueries
{
    public static Task<bool> AgentOwnsClientAsync(
        this MasterAppDbContext db,
        string agentOid,
        string clientUserId,
        string? agentUpn = null,
        IEnumerable<string>? agentIdCandidates = null,
        CancellationToken ct = default)
    {
        var agentKey = IdentityKey.Normalize(agentOid);
        var clientKey = IdentityKey.Normalize(clientUserId);
        if (string.IsNullOrWhiteSpace(agentKey) || string.IsNullOrWhiteSpace(clientKey))
        {
            return Task.FromResult(false);
        }

        var candidateIds = IdentityKey.NormalizeSet(new[] { agentKey });
        if (agentIdCandidates != null)
        {
            foreach (var candidate in agentIdCandidates)
            {
                var key = IdentityKey.Normalize(candidate);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    candidateIds.Add(key);
                }
            }
        }

        var upnKey = IdentityKey.Normalize(agentUpn);

        return db.AgentClients
            .AsNoTracking()
            .AnyAsync(link =>
                    (link.ClientUserId ?? string.Empty).ToLower() == clientKey &&
                    (
                        candidateIds.Contains((link.AgentUserId ?? string.Empty).ToLower()) ||
                        (!string.IsNullOrWhiteSpace(upnKey) && (link.AgentUpn ?? string.Empty).ToLower() == upnKey)
                    ),
                ct);
    }

    public static Task<bool> AgentOwnsLeadAsync(
        this MasterAppDbContext db,
        string agentOid,
        string leadId,
        CancellationToken ct = default)
    {
        var agentKey = IdentityKey.Normalize(agentOid);
        var leadKey = IdentityKey.Normalize(leadId);
        if (string.IsNullOrWhiteSpace(agentKey) || string.IsNullOrWhiteSpace(leadKey))
        {
            return Task.FromResult(false);
        }

        return db.WorkstationLeadProfiles
            .AsNoTracking()
            .AnyAsync(lead =>
                    (lead.AgentUserId ?? string.Empty).ToLower() == agentKey &&
                    (lead.LeadId ?? string.Empty).ToLower() == leadKey,
                ct);
    }

    public static async Task<string?> ResolveOwnedClientIdForActionAsync(
        this MasterAppDbContext db,
        Guid actionId,
        string agentOid,
        string? agentUpn = null,
        IEnumerable<string>? agentIdCandidates = null,
        CancellationToken ct = default)
    {
        var action = await db.ActionItems
            .AsNoTracking()
            .Where(x => x.Id == actionId && x.RelatedEntityType == RelatedEntityType.Client)
            .Select(x => x.RelatedEntityId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        return await db.AgentOwnsClientAsync(agentOid, action, agentUpn, agentIdCandidates, ct)
            ? IdentityKey.Normalize(action)
            : null;
    }
}
