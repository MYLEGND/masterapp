using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Shared.Auth;

namespace ClientApp.Controllers;

public sealed class BusinessWorkspaceController(BusinessWorkspaceService workspace, MasterAppDbContext db,
    EffectiveClientContextService contextService) : BusinessWorkspaceControllerBase(workspace)
{
    protected override async Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct)
    {
        var context = await contextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        return context is null ? null : await BusinessWorkspaceAccess.ResolveAsync(db, id, context.Profile.Id,
            User.GetCanonicalUserId(), context.IsAgentView ? context.AgentEmail : context.Profile.Email, capability, ct);
    }
}
