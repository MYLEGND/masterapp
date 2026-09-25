using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Businesses;

/// <summary>Business labels are projected from linked tenants; personal identity stays unchanged.</summary>
public static class BusinessIdentityProjection
{
    public static async Task<Dictionary<Guid, string>> LoadLabelsAsync(MasterAppDbContext db, IEnumerable<Guid> profileIds,
        CancellationToken ct = default)
    {
        var ids = profileIds.Distinct().ToArray();
        var rows = await (from member in db.CommerceBusinessMembers.AsNoTracking()
                          join business in db.CommerceBusinesses.AsNoTracking() on member.CommerceBusinessId equals business.Id
                          where member.ClientProfileId.HasValue && ids.Contains(member.ClientProfileId.Value) &&
                            member.Status == "Active" && (member.RoleKey == "owner" || member.RoleKey == "account") && business.IsActive
                          select new { ProfileId = member.ClientProfileId!.Value, business.DisplayName }).ToListAsync(ct);
        return rows.GroupBy(x => x.ProfileId).ToDictionary(x => x.Key,
            x => string.Join(" / ", x.Select(r => r.DisplayName).Distinct().OrderBy(n => n)));
    }
}
