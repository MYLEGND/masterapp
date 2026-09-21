using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

/// <summary>Published snapshots are immutable. Draft changes never alter the public pointer.</summary>
public static class WebsiteContentStore
{
    public static async Task<WebsiteContentVersion?> PublishedBusinessAsync(MasterAppDbContext db, Guid businessId, CancellationToken cancellationToken = default)
    {
        if (!await db.CommerceBusinesses.AnyAsync(b => b.Id == businessId && b.IsActive && b.Status == "Active", cancellationToken)) return null;
        var owner = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
        return await (from state in db.Set<WebsiteContentState>().AsNoTracking()
                      join version in db.Set<WebsiteContentVersion>().AsNoTracking() on state.PublishedVersionId equals version.Id
                      where state.OwnerKey == owner && state.SiteKey == WebsiteEditorSiteKeys.Business && version.StateId == state.Id
                      select version).SingleOrDefaultAsync(cancellationToken);
    }
}
