using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Businesses;

public sealed record OwnedBusinessAccountAccess(ClientProfile Actor, Guid BusinessId);

public static class BusinessWorkspaceAccess
{
    // A personal actor may select an entity account only through its explicit owner link.
    // Emails are presentation data, never account-switch authority.
    public static async Task<OwnedBusinessAccountAccess?> ResolveAccountOwnerAsync(MasterAppDbContext db,
        Guid accountProfileId, string? actorUserId, CancellationToken ct = default)
    {
        var actorId = Shared.Auth.IdentityKey.Normalize(actorUserId);
        if (string.IsNullOrWhiteSpace(actorId)) return null;
        var actors = await db.ClientProfiles.AsNoTracking().Where(x =>
            x.ClientUserId.ToLower() == actorId || (x.ExternalIdentityObjectId ?? "").ToLower() == actorId)
            .Take(2).ToListAsync(ct);
        if (actors.Count != 1 || string.IsNullOrWhiteSpace(actors[0].ClientUserId) || ClientRecordClassification.Resolve(actors[0].ClientUserId, actors[0].CrmNotes) != ClientRecordClassification.Client)
            return null;
        var actor = actors[0];
        var account = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountProfileId, ct);
        if (account == null || string.IsNullOrWhiteSpace(account.ClientUserId) || ClientRecordClassification.Resolve(account.ClientUserId, account.CrmNotes) != ClientRecordClassification.BusinessClient)
            return null;
        var associations = await db.CommerceBusinessMembers.AsNoTracking()
            .Where(x => x.ClientProfileId == accountProfileId && x.RoleKey == "account" && x.Status == "Active")
            .Take(2).ToListAsync(ct);
        if (associations.Count != 1) return null;
        var businessId = associations[0].CommerceBusinessId;
        if (await db.CommerceBusinessMembers.CountAsync(x => x.CommerceBusinessId == businessId &&
            x.RoleKey == "account" && x.Status == "Active", ct) != 1) return null;
        var owners = await db.CommerceBusinessMembers.AsNoTracking().Where(x =>
            x.CommerceBusinessId == businessId && x.ClientProfileId == actor.Id).Take(2).ToListAsync(ct);
        if (owners.Count != 1 || owners[0].RoleKey != "owner" || owners[0].Status != "Active" ||
            !owners[0].CanManageTeam || !owners[0].CanManageStorefront) return null;
        if (!await WebsiteBusinessAccess.CanManageAsync(db, businessId, actor.Id, ct)) return null;
        foreach (var subjectId in Shared.Auth.IdentityKey.NormalizeSet(new[] { actorId, actor.ClientUserId, actor.ExternalIdentityObjectId }))
            if (!(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
                new Domain.Accounts.AccountLifecycleSubject(subjectId, Domain.Messaging.MessagingParticipantTypes.Client, actor.Id), ct)).AllowsFullAccess)
                return null;
        foreach (var subjectId in Shared.Auth.IdentityKey.NormalizeSet(new[] { account.ClientUserId, account.ExternalIdentityObjectId }))
            if (!(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
                new Domain.Accounts.AccountLifecycleSubject(subjectId, Domain.Messaging.MessagingParticipantTypes.Client, account.Id), ct)).AllowsFullAccess)
                return null;
        return new OwnedBusinessAccountAccess(actor, businessId);
    }

    // Resolves the stable signed-in identity, never the displayed email. Explicit
    // profile selection is still reauthorized; it cannot manufacture membership.
    public static async Task<CommerceBusinessMember?> ResolveActorMembershipAsync(
        MasterAppDbContext db, Guid businessId, string actorUserId, string capability,
        Guid? selectedProfileId = null, CancellationToken ct = default)
    {
        var actor = Shared.Auth.IdentityKey.Normalize(actorUserId);
        if (businessId == Guid.Empty || string.IsNullOrWhiteSpace(actor)) return null;
        var identityMatches = await db.ClientProfiles.AsNoTracking().Where(p => p.ClientUserId.ToLower() == actor ||
            (p.ExternalIdentityObjectId ?? "").ToLower() == actor).Select(p => p.Id).Take(2).ToListAsync(ct);
        if (identityMatches.Count > 1) return null;
        var profiles = db.ClientProfiles.AsNoTracking();
        if (selectedProfileId.HasValue)
            profiles = profiles.Where(p => p.Id == selectedProfileId.Value);
        else
            profiles = profiles.Where(p => p.ClientUserId.ToLower() == actor ||
                (p.ExternalIdentityObjectId ?? "").ToLower() == actor);
        var candidates = await profiles.Where(p => db.CommerceBusinessMembers.Any(m =>
                m.CommerceBusinessId == businessId && m.ClientProfileId == p.Id && m.Status == "Active"))
            .Take(2).ToListAsync(ct);
        if (candidates.Count != 1) return null;
        var profile = candidates[0];
        var direct = Shared.Auth.IdentityKey.NormalizeSet(new[] { profile.ClientUserId, profile.ExternalIdentityObjectId }).Contains(actor);
        if (!direct)
        {
            // Delegation is an explicit existing agent/client relationship, not an
            // email match, and both the agent and client lifecycle remain active.
            var agents = await db.AgentProfiles.AsNoTracking().Where(a => a.AgentUserId.ToLower() == actor && a.IsActive)
                .Take(2).ToListAsync(ct);
            if (agents.Count != 1 || !(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
                new Domain.Accounts.AccountLifecycleSubject(actor, Domain.Messaging.MessagingParticipantTypes.Agent, agents[0].Id), ct)).AllowsFullAccess)
                return null;
        }
        foreach (var subject in Shared.Auth.IdentityKey.NormalizeSet(new[] { profile.ClientUserId, profile.ExternalIdentityObjectId }))
            if (!(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
                new Domain.Accounts.AccountLifecycleSubject(subject, Domain.Messaging.MessagingParticipantTypes.Client, profile.Id), ct)).AllowsFullAccess)
                return null;
        var members = await db.CommerceBusinessMembers.AsNoTracking().Where(m =>
            m.CommerceBusinessId == businessId && m.ClientProfileId == profile.Id && m.Status == "Active").Take(2).ToListAsync(ct);
        if (members.Count != 1 || await ResolveAsync(db, businessId, profile.Id, actor, null, capability, ct) is null) return null;
        return members[0];
    }

    public static async Task<CommerceBusiness?> ResolveAsync(MasterAppDbContext db, Guid businessId, Guid profileId,
        string actor, string? email, string capability, CancellationToken ct)
    {
        if (!await WebsiteBusinessAccess.CanAccessBusinessAsActorAsync(db, businessId, profileId, actor, email, ct)) return null;
        var member = await db.CommerceBusinessMembers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CommerceBusinessId == businessId && x.ClientProfileId == profileId && x.Status == "Active", ct);
        if (member is null) return null;
        return Allows(member, capability) ? await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == businessId, ct) : null;
    }
    public static bool IsKnownCapability(string capability) => capability is
        "website" or "catalog" or "orders" or "automations" or "team" or "settings" or "analytics" or "crm";

    // Shared by route authorization and navigation after actor/business resolution.
    public static bool Allows(CommerceBusinessMember member, string capability)
    {
        if (member.Status != "Active") return false;
        var owner = member.RoleKey.Equals("owner", StringComparison.OrdinalIgnoreCase) ||
            member.RoleKey.Equals("platform-owner", StringComparison.OrdinalIgnoreCase) ||
            member.RoleKey.Equals("account", StringComparison.OrdinalIgnoreCase);
        return capability switch
        {
            "website" => member.CanManageStorefront,
            "catalog" => member.CanManageStorefront && member.CanManageCatalog,
            "orders" => member.CanManageStorefront && member.CanManageOrders,
            "automations" => member.CanManageStorefront && member.CanManageOrders,
            "team" => owner && member.CanManageTeam,
            "settings" => owner,
            "analytics" => member.CanManageAnalytics,
            "crm" => member.CanManageCrm,
            _ => false
        };
    }

}
