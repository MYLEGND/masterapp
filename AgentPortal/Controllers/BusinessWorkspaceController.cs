using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

public sealed class BusinessWorkspaceController(BusinessWorkspaceService workspace, MasterAppDbContext db)
    : BusinessWorkspaceControllerBase(workspace)
{
    protected override async Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct)
    {
        var profiles = await db.CommerceBusinessMembers.AsNoTracking().Where(x => x.CommerceBusinessId == id &&
            x.Status == "Active" && x.ClientProfileId.HasValue).Select(x => x.ClientProfileId!.Value).ToListAsync(ct);
        foreach (var profile in profiles)
        {
            var business = await BusinessWorkspaceAccess.ResolveAsync(db, id, profile, User.GetCanonicalUserId(),
                User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Email), capability, ct);
            if (business is not null) return business;
        }
        return null;
    }
}
