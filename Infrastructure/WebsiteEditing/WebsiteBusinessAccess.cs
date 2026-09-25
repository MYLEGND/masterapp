using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace Infrastructure.WebsiteEditing;

public static class WebsiteBusinessAccess
{
    public static IQueryable<CommerceBusiness> QueryManagedBusinesses(MasterAppDbContext db, Guid clientProfileId) =>
        db.CommerceBusinesses.Where(b => b.IsActive && b.Status.ToLower() == "active" &&
            db.CommerceBusinessMembers.Any(m => m.CommerceBusinessId == b.Id &&
                m.ClientProfileId == clientProfileId && m.Status.ToLower() == "active" && m.CanManageStorefront));

    public static async Task<bool> CanPublishAsync(
        MasterAppDbContext db,
        Guid businessId,
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageAsync(db, businessId, clientProfileId, cancellationToken))
            return false;

        return await db.CommerceBusinessMembers.AsNoTracking().AnyAsync(
            member =>
                member.CommerceBusinessId == businessId &&
                member.ClientProfileId == clientProfileId &&
                member.Status.ToLower() == "active" &&
                member.CanManageStorefront &&
                (member.RoleKey.ToLower() == "owner" || member.RoleKey.ToLower() == "account"),
            cancellationToken);
    }

    public static async Task<bool> CanManageAsync(MasterAppDbContext db, Guid businessId, Guid clientProfileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == clientProfileId, cancellationToken);
        return profile is not null && (await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db, new Domain.Accounts.AccountLifecycleSubject(profile.ClientUserId, MessagingParticipantTypes.Client, profile.Id), cancellationToken)).AllowsFullAccess &&
            await QueryManagedBusinesses(db, clientProfileId).AnyAsync(b => b.Id == businessId, cancellationToken);
    }

    public static async Task<bool> CanManageAsActorAsync(
        MasterAppDbContext db, Guid businessId, Guid clientProfileId, string? actorUserId,
        string? actorEmail = null, CancellationToken cancellationToken = default) =>
        await CanManageAsync(db, businessId, clientProfileId, cancellationToken) &&
        await CanAccessBusinessAsActorAsync(db, businessId, clientProfileId, actorUserId, actorEmail, cancellationToken);

    public static async Task<bool> CanAccessBusinessAsActorAsync(
        MasterAppDbContext db, Guid businessId, Guid clientProfileId, string? actorUserId,
        string? actorEmail = null, CancellationToken cancellationToken = default)
    {
        if (!await db.CommerceBusinesses.AnyAsync(b => b.Id == businessId && b.IsActive && b.Status.ToLower() == "active" &&
            db.CommerceBusinessMembers.Any(m => m.CommerceBusinessId == businessId && m.ClientProfileId == clientProfileId && m.Status.ToLower() == "active"), cancellationToken)) return false;
        var profile = await db.ClientProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == clientProfileId, cancellationToken);
        if (profile is null)
            return false;
        if (!(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
            new Domain.Accounts.AccountLifecycleSubject(profile.ClientUserId, MessagingParticipantTypes.Client, profile.Id), cancellationToken)).AllowsFullAccess)
            return false;

        var actor = IdentityKey.Normalize(actorUserId);
        if (string.IsNullOrWhiteSpace(actor))
            return false;

        var clientIds = IdentityKey.NormalizeSet(new[]
        {
            profile.ClientUserId,
            profile.ExternalIdentityObjectId
        });
        if (clientIds.Contains(actor))
            return true;

        if (!ClientAccountManagementModes.AllowsAgentWorkspaceAccess(profile.AccountManagementMode))
            return false;

        return await db.AgentOwnsClientAsync(
            actor,
            profile.ClientUserId,
            actorEmail,
            new[] { actor },
            cancellationToken);
    }
}
