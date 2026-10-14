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

    public static async Task<CommerceBusiness?> ResolveAsync(MasterAppDbContext db, Guid businessId, Guid profileId,
        string actor, string? email, string capability, CancellationToken ct)
    {
        if (!await WebsiteBusinessAccess.CanAccessBusinessAsActorAsync(db, businessId, profileId, actor, email, ct)) return null;
        var member = await db.CommerceBusinessMembers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CommerceBusinessId == businessId && x.ClientProfileId == profileId && x.Status == "Active", ct);
        if (member is null) return null;
        return Allows(member, capability) ? await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == businessId, ct) : null;
    }
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
            "crm" => member.CanManageOrders || owner,
            _ => false
        };
    }

}
