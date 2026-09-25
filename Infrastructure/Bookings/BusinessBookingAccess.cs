using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Bookings;

public static class BusinessBookingAccess
{
    public static async Task<AgentProfile?> ResolveAttachedAgentAsync(
        MasterAppDbContext db,
        Guid businessId,
        Guid agentProfileId,
        CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || agentProfileId == Guid.Empty) return null;

        var agent = await db.AgentProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == agentProfileId, ct);
        if (agent is null || string.IsNullOrWhiteSpace(agent.AgentUserId)) return null;

        var businessActive = await db.CommerceBusinesses.AsNoTracking()
            .AnyAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct);
        if (!businessActive) return null;

        var memberProfileIds = await db.CommerceBusinessMembers.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId &&
                        x.Status == "Active" &&
                        x.ClientProfileId.HasValue)
            .Select(x => x.ClientProfileId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (memberProfileIds.Count == 0) return null;

        var memberClientIds = await db.ClientProfiles.AsNoTracking()
            .Where(x => memberProfileIds.Contains(x.Id))
            .Select(x => x.ClientUserId)
            .Where(x => x != null && x != "")
            .ToListAsync(ct);
        if (memberClientIds.Count == 0) return null;

        var agentId = agent.AgentUserId.Trim().ToLower();
        var agentUpn = (agent.AgentUpn ?? string.Empty).Trim().ToLower();
        var attached = await db.AgentClients.AsNoTracking().AnyAsync(link =>
            memberClientIds.Contains(link.ClientUserId) &&
            (((link.AgentUserId ?? string.Empty).Trim().ToLower() == agentId) ||
             (!string.IsNullOrWhiteSpace(agentUpn) &&
              (link.AgentUpn ?? string.Empty).Trim().ToLower() == agentUpn)), ct);

        return attached ? agent : null;
    }

    public static Task<WorkstationLeadProfile?> ResolveBusinessContactAsync(
        MasterAppDbContext db,
        Guid businessId,
        string? contactId,
        CancellationToken ct = default)
    {
        var normalized = (contactId ?? string.Empty).Trim().ToLower();
        if (businessId == Guid.Empty || normalized.Length == 0)
            return Task.FromResult<WorkstationLeadProfile?>(null);

        return db.WorkstationLeadProfiles
            .FirstOrDefaultAsync(x =>
                x.CommerceBusinessId == businessId &&
                (x.AgentUserId ?? string.Empty) == string.Empty &&
                (x.LeadId ?? string.Empty).Trim().ToLower() == normalized, ct);
    }
}
