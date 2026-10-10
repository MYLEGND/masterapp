using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Businesses;

public static class BusinessWorkspaceAccess
{
    public static async Task<CommerceBusiness?> ResolveAsync(MasterAppDbContext db, Guid businessId, Guid profileId,
        string actor, string? email, string capability, CancellationToken ct)
    {
        if (!await WebsiteBusinessAccess.CanAccessBusinessAsActorAsync(db, businessId, profileId, actor, email, ct)) return null;
        var member = await db.CommerceBusinessMembers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CommerceBusinessId == businessId && x.ClientProfileId == profileId && x.Status == "Active", ct);
        if (member is null) return null;
        var owner = member.RoleKey.Equals("owner", StringComparison.OrdinalIgnoreCase);
        var allowed = capability switch
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
        return allowed ? await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == businessId, ct) : null;
    }
}
