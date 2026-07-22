using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace ClientApp.Infrastructure;

public sealed class ClientSubscriptionActiveHandler : AuthorizationHandler<ClientSubscriptionActiveRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EffectiveClientContextService _clientContextService;
    private readonly IBillingEntitlementService _entitlementService;

    public ClientSubscriptionActiveHandler(
        IHttpContextAccessor httpContextAccessor,
        EffectiveClientContextService clientContextService,
        IBillingEntitlementService entitlementService)
    {
        _httpContextAccessor = httpContextAccessor;
        _clientContextService = clientContextService;
        _entitlementService = entitlementService;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ClientSubscriptionActiveRequirement requirement)
    {
        if (!(context.User.Identity?.IsAuthenticated ?? false))
            return;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
            return;

        var resolved = await _clientContextService.ResolveAsync(context.User, httpContext.Request.Cookies, allowRelink: false);
        if (resolved?.IsAgentView == true)
        {
            context.Succeed(requirement);
            return;
        }

        if (resolved is null)
            return;

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                resolved.ClientProfileId,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            httpContext.RequestAborted);

        if (entitlement.Status is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod)
            context.Succeed(requirement);
    }
}
