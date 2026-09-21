using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

public static class WebsiteBusinessAccess
{
    public static IQueryable<CommerceBusiness> QueryManagedBusinesses(MasterAppDbContext db, Guid clientProfileId) =>
        db.CommerceBusinesses.Where(b => b.IsActive && b.Status == "Active" &&
            db.CommerceBusinessMembers.Any(m => m.CommerceBusinessId == b.Id &&
                m.ClientProfileId == clientProfileId && m.Status == "Active" && m.CanManageStorefront));

    public static async Task<bool> CanManageAsync(MasterAppDbContext db, Guid businessId, Guid clientProfileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == clientProfileId, cancellationToken);
        return profile is not null && (await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db, new Domain.Accounts.AccountLifecycleSubject(profile.ClientUserId, MessagingParticipantTypes.Client, profile.Id), cancellationToken)).AllowsFullAccess && ClientRecordClassification.Resolve(profile.ClientUserId, profile.CrmNotes) == ClientRecordClassification.BusinessClient &&
            await QueryManagedBusinesses(db, clientProfileId).AnyAsync(b => b.Id == businessId, cancellationToken);
    }
}
