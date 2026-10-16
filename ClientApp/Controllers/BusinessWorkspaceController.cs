using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Shared.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

public sealed class BusinessWorkspaceController(BusinessWorkspaceService workspace, MasterAppDbContext db,
    EffectiveClientContextService contextService, ClientIdentityContinuationService continuations, IConfiguration configuration) : BusinessWorkspaceControllerBase(workspace)
{
    protected override async Task<IActionResult> CreateWebsiteSessionAsync(Guid businessId, CancellationToken ct)
    {
        var context = await contextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null || (context.ManagedBusinessId.HasValue && context.ManagedBusinessId != businessId)) return Forbid();
        var handoff = await continuations.CreateWebsiteEditorHandoffAsync(context.BusinessActorProfileId, businessId,
            User.GetCanonicalUserId(), context.BusinessActorEmail ?? "", ct);
        var apiBase = (configuration["WebsiteContentApiBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/');
        return Json(new { handoffUrl = $"{apiBase}/api/website-content/handoff", state = handoff.OpaqueState, expiresUtc = handoff.ExpiresUtc });
    }

    protected override async Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct)
    {
        var context = await contextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        return context is null || (context.ManagedBusinessId.HasValue && context.ManagedBusinessId != id) ? null : await BusinessWorkspaceAccess.ResolveAsync(db, id, context.BusinessActorProfileId,
            User.GetCanonicalUserId(), context.BusinessActorEmail, capability, ct);
    }
}
