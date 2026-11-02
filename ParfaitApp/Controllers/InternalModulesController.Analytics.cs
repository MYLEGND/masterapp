using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using ParfaitApp.Services;
using ParfaitApp.Security;
using Infrastructure.Analytics;
using Shared.Analytics;

namespace ParfaitApp.Controllers;

public sealed partial class InternalModulesController
{
    [HttpGet("analytics/marketing-setup"), ParfaitInternalPageAccess("/internal/analytics")]
    public async Task<IActionResult> MarketingSetup(CancellationToken ct = default)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        return business is null ? Forbid() : await base.MarketingSetup(business.Id, ct);
    }
    [HttpPost("analytics/openai-connect"), ParfaitInternalPageAccess("/internal/analytics"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOpenAi([FromBody] MarketingProviderApiKeyRequest request, CancellationToken ct = default)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        return business is null ? Forbid() : await base.ConnectOpenAi(business.Id, new(request.AdvertiserApiKey, request.ExpectedRevision), ct);
    }
    [HttpPost("analytics/openai-refresh"), ParfaitInternalPageAccess("/internal/analytics"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshOpenAi([FromBody] MarketingProviderRevisionRequest request, CancellationToken ct = default)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        return business is null ? Forbid() : await base.RefreshOpenAi(business.Id, new(request.ConnectionRevision), ct);
    }
    [HttpPost("analytics/openai-disconnect"), ParfaitInternalPageAccess("/internal/analytics"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOpenAi([FromBody] MarketingProviderRevisionRequest request, CancellationToken ct = default)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        return business is null ? Forbid() : await base.DisconnectOpenAi(business.Id, new(request.ConnectionRevision), ct);
    }
    private async Task<CommerceBusiness?> ResolveLegacyAnalyticsBusinessAsync(CancellationToken ct)
    {
        var access = await HttpContext.RequestServices.GetRequiredService<IParfaitTeamAccessService>()
            .AuthorizePageAsync(User, Request.Path, "/internal/analytics", ct);
        if (!access.Allowed) return null;
        var business = await _parfaitScope.GetParfaitAsync(ct);
        return business.IsActive && business.Status == "Active" ? business : null;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor action &&
            action.MethodInfo.DeclaringType == typeof(BusinessAnalyticsControllerBase))
        {
            var business = await ResolveLegacyAnalyticsBusinessAsync(context.HttpContext.RequestAborted);
            if (business is null || context.ActionArguments.TryGetValue("businessId", out var requested) &&
                requested is Guid id && id != Guid.Empty && id != business.Id)
            { context.Result = Forbid(); return; }
            context.ActionArguments["businessId"] = business.Id;
        }
        await next();
    }

    protected override async Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        return business?.Id == id ? business : null;
    }
    [HttpGet("analytics/commerce")]
    public async Task<IActionResult> CommerceAnalytics(string? preset = null, DateTime? fromUtc = null, DateTime? toUtc = null,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, string? timezoneId = null,
        int? timezoneOffsetMinutes = null, CancellationToken ct = default)
    {
        var business = await ResolveLegacyAnalyticsBusinessAsync(ct);
        if (business is null) return Forbid();
        var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
        var model = await _internalAnalytics.GetDashboardAsync(business.Id, ResolveAnalyticsScope(business.Id),
            MarketingOwnerScope.Business(business.Id), preset ?? "30d", fromUtc, toUtc, qualityMode,
            timezone, timezoneId, timezoneOffsetMinutes, ct);
        return Json(ParfaitInternalAnalyticsService.StoreReport(model));
    }

    protected override ScopeContext ResolveAnalyticsScope(Guid id) => ScopeContext.ForBusiness(id).ForStore(id);
    protected override string AnalyticsRoute(Guid id) => "/internal/analytics";
    protected override void ConfigureAnalyticsView()
    {
        ViewData["AnalyticsMode"] = "store";
        ViewData["AnalyticsLayout"] = "~/Views/Shared/_InternalLayout.cshtml";
        ViewData["PersonalLink"] = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/store";
    }
    protected override async Task<BusinessWebsiteProfileInput> ReadMarketingProfileAsync(Guid id, CancellationToken ct)
    {
        await _businessProfile.GetMetaSettingsAsync(ct);
        return await base.ReadMarketingProfileAsync(id, ct);
    }
    protected override async Task SaveMarketingProfileAsync(Guid id, BusinessWebsiteProfileInput input, CancellationToken ct)
    {
        await _businessProfile.GetMetaSettingsAsync(ct);
        await base.SaveMarketingProfileAsync(id, input, ct);
    }
    protected override string AnalyticsCallbackUri(string callback) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/internal/analytics/{callback}";

    [HttpGet("analytics/external-ads/callback")]
    public new Task<IActionResult> ExternalAdsCallback(string? code = null, [FromQuery(Name = "auth_code")] string? authCode = null,
        string? state = null, string? error = null, [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default) =>
        base.ExternalAdsCallback(code, authCode, state, error, errorDescription, cancellationToken);

    [HttpGet("analytics/calendar-callback")]
    public new Task<IActionResult> CalendarCallback(string? code = null, string? state = null, string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null, CancellationToken cancellationToken = default) =>
        base.CalendarCallback(code, state, error, errorDescription, cancellationToken);
}
