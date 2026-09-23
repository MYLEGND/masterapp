using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

public sealed class BusinessWorkspaceController(BusinessWorkspaceService workspace, MasterAppDbContext db, IConfiguration configuration)
    : BusinessWorkspaceControllerBase(workspace)
{
    protected override async Task<IActionResult> CreateWebsiteSessionAsync(Guid businessId, CancellationToken ct)
    {
        var actor = User.GetCanonicalUserId();
        var email = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Email);
        var profiles = await db.CommerceBusinessMembers.AsNoTracking().Where(x => x.CommerceBusinessId == businessId &&
            x.Status == "Active" && x.ClientProfileId.HasValue).Select(x => x.ClientProfileId!.Value).ToListAsync(ct);
        foreach (var profile in profiles)
        {
            if (!await WebsiteBusinessAccess.CanManageAsActorAsync(db, businessId, profile, actor, email, ct)) continue;
            var handoff = await WebsiteEditorHandoffService.CreateAsync(db, profile, businessId, actor, email ?? "", ct);
            var apiBase = (configuration["LandingRoutes:BaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/');
            return Json(new { handoffUrl = $"{apiBase}/api/website-content/handoff", state = handoff.OpaqueState, expiresUtc = handoff.ExpiresUtc });
        }
        return Forbid();
    }

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
