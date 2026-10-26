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

        return await IsAttachedAgentAsync(db, businessId, agentProfileId, ct) ? agent : null;
    }

    // Canonical authorization expressed as a server-side boolean. The metadata
    // path never materializes a client/profile/contact or returns an identity.
    public static Task<bool> IsAttachedAgentAsync(MasterAppDbContext db, Guid businessId,
        Guid agentProfileId, CancellationToken ct = default) =>
        db.AgentProfiles.AsNoTracking().Where(agent => agent.Id == agentProfileId &&
            agent.AgentUserId != null && agent.AgentUserId.Trim() != "" &&
            db.CommerceBusinesses.Any(business => business.Id == businessId && business.IsActive && business.Status == "Active"))
        .AnyAsync(agent => db.CommerceBusinessMembers.Any(member => member.CommerceBusinessId == businessId &&
            member.Status == "Active" && member.ClientProfileId.HasValue &&
            db.ClientProfiles.Any(client => client.Id == member.ClientProfileId.Value && client.ClientUserId != null && client.ClientUserId != "" &&
                db.AgentClients.Any(link => link.ClientUserId == client.ClientUserId &&
                    ((link.AgentUserId ?? "").Trim().ToLower() == agent.AgentUserId.Trim().ToLower() ||
                     (agent.AgentUpn != null && agent.AgentUpn.Trim() != "" &&
                      (link.AgentUpn ?? "").Trim().ToLower() == agent.AgentUpn.Trim().ToLower()))))), ct);

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
