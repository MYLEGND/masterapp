using Domain.Entities;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;
using Shared.Crm;
using Shared.Analytics;
using System.Text.Json;

namespace Infrastructure.Businesses;

[Authorize]
[Route("business/{businessId:guid}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public abstract partial class BusinessWorkspaceControllerBase(BusinessWorkspaceService workspace) : Controller
{
    protected abstract Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct);

    [HttpGet("website-session")]
    public async Task<IActionResult> WebsiteSession(Guid businessId, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "website", cancellationToken) is null) return Forbid();
        return await CreateWebsiteSessionAsync(businessId, cancellationToken);
    }

    protected virtual Task<IActionResult> CreateWebsiteSessionAsync(Guid businessId, CancellationToken ct) =>
        Task.FromResult<IActionResult>(Forbid());

    [HttpGet("crm/api/Clients/QuickView")]
    public async Task<IActionResult> ClientQuickView(Guid businessId, string clientUserId, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var result = await workspace.QuickViewAsync(businessId, clientUserId, "Client", cancellationToken);
        return result is null ? NotFound() : Json(result);
    }

    [HttpGet("crm/api/Leads/Lead")]
    public async Task<IActionResult> LeadQuickView(Guid businessId, string id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var result = await workspace.QuickViewAsync(businessId, id, "Lead", cancellationToken);
        return result is null ? NotFound() : Json(result);
    }

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/SaveQuickView")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SaveQuickView(Guid businessId, string recordSet, [FromBody] BusinessCrmQuickViewRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.SaveQuickViewAsync(businessId,
            recordSet == "Clients" ? "Client" : "Lead", input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpPost("crm/api/Clients/AddActivity")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddActivity(Guid businessId, [FromBody] BusinessCrmActivityRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.AddActivityAsync(businessId,
            input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/Reorder")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Reorder(Guid businessId, string recordSet, [FromBody] BusinessCrmReorderRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.ReorderAsync(businessId,
            recordSet == "Clients" ? "Client" : "Lead", input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    private async Task<IActionResult> ExecuteCrmWrite(Guid businessId, Func<Task<object?>> write, CancellationToken ct)
    {
        if (await ResolveBusinessAsync(businessId, "crm", ct) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try
        {
            var result = await write();
            return result is null ? NotFound() : Json(result);
        }
        catch (DbUpdateConcurrencyException) { return Conflict("This contact changed in another session. Reload before saving."); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("crm/api/Clients/BulkUpdate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkUpdate(Guid businessId, [FromBody] BusinessCrmBulkRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.BulkUpdateAsync(businessId,
            input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpGet("clients")]
    [HttpGet("clients/{contactId}")]
    public Task<IActionResult> Clients(Guid businessId, string? contactId, string? search = null, int page = 1,
        CancellationToken cancellationToken = default) =>
        RenderCrmAsync(businessId, contactId, "Client", search, page, cancellationToken);

    [HttpGet("leads")]
    [HttpGet("leads/{contactId}")]
    public Task<IActionResult> Leads(Guid businessId, string? contactId, string? search = null, int page = 1,
        CancellationToken cancellationToken = default) =>
        RenderCrmAsync(businessId, contactId, "Lead", search, page, cancellationToken);

    // Preserve existing bookmarks without maintaining a third CRM page.
    [HttpGet("crm")]
    [HttpGet("crm/{contactId}")]
    public async Task<IActionResult> Crm(Guid businessId, string? contactId, string kind = "Lead", string? search = null,
        int page = 1, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        return RedirectToAction(kind == "Client" ? nameof(Clients) : nameof(Leads),
            new { businessId, contactId, search, page });
    }

    private async Task<IActionResult> RenderCrmAsync(Guid businessId, string? contactId, string kind, string? search,
        int page, CancellationToken cancellationToken)
    {
        var business = await ResolveBusinessAsync(businessId, "crm", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.CrmAsync(business, kind, search, page, contactId, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        if (contactId is not null && model.Selected is null) return NotFound();
        ViewData["BusinessWorkspace"] = model;
        ViewData["Title"] = model.Kind == "Client" ? model.Preferences.ClientLabel : model.Preferences.LeadLabel;
        ViewBag.Search = model.Search;
        ViewBag.TotalClients = model.Total;
        return View(model.Kind == "Client" ? "~/Views/Clients/Index.cshtml" : "~/Views/Leads/Index.cshtml", model.CanonicalContacts);
    }

    [HttpPost("crm/{contactId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContact(Guid businessId, string contactId, BusinessCrmEdit input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try { if (!await workspace.UpdateAsync(businessId, contactId, input, User.GetCanonicalUserId(), cancellationToken)) return NotFound(); }
        catch (DbUpdateConcurrencyException) { return Conflict("This contact changed in another session. Reload before saving."); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        return RedirectToAction(input.Kind == "Client" ? nameof(Clients) : nameof(Leads), new { businessId, contactId });
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(
        Guid businessId,
        int days = 30,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();

        var preset = days == 7 ? "7d" : days == 90 ? "90d" : "30d";
        var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
        var range = TimeRangeRequest.FromPreset(preset, viewerTz: timezone);
        var model = await workspace.AnalyticsAsync(business, range, cancellationToken);

        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        ViewData["AnalyticsBase"] = $"/business/{businessId}/analytics";
        ViewData["InitialScopeLabel"] = business.DisplayName;
        ViewData["InitialRangePreset"] = range.Preset;
        ViewData["InitialRangeLabel"] = range.Label;
        ViewData["InitialSummaryJson"] = JsonSerializer.Serialize(model.Summary);
        ViewData["BusinessWorkspace"] = model;
        ViewData["AnalyticsCanMetaAds"] = true;
        ViewData["AnalyticsCanAiReview"] = false;
        ViewData["AnalyticsCanAgentPerformance"] = false;
        ViewData["AnalyticsCanIncidentMonitor"] = false;
        return View("~/Views/WebsiteAnalytics/Index.cshtml");
    }

    public sealed record BusinessAdvertisingPromotionRequest(PromotionProposalRequest Promotion);
    public sealed record BusinessAdvertisingProposalActionRequest(Guid ProposalId, string Revision);
    public sealed record BusinessAdvertisingCampaignStatusRequest(string CampaignId, string CampaignName, string Status);
    public sealed record BusinessAdvertisingCampaignBudgetRequest(string CampaignId, string CampaignName, OpenAiAdsBudget Budget);

    [HttpGet("analytics/advertising")]
    public async Task<IActionResult> Advertising(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.GetAsync(MarketingOwnerScope.Business(businessId), cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/promote/draft")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingPromotionDraft(
        Guid businessId,
        [FromBody] BusinessAdvertisingPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.DraftPromotionAsync(MarketingOwnerScope.Business(businessId), request.Promotion, cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/promote/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingPromotionPropose(
        Guid businessId,
        [FromBody] BusinessAdvertisingPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ProposePromotionAsync(
                MarketingOwnerScope.Business(businessId),
                request.Promotion,
                User.GetCanonicalUserId(),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/campaign/status/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingCampaignStatusPropose(
        Guid businessId,
        [FromBody] BusinessAdvertisingCampaignStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ProposeCampaignStatusAsync(
                MarketingOwnerScope.Business(businessId),
                new Infrastructure.Analytics.AdvertisingCampaignStatusProposalRequest(
                    request.CampaignId, request.CampaignName, request.Status),
                User.GetCanonicalUserId(),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/campaign/budget/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingCampaignBudgetPropose(
        Guid businessId,
        [FromBody] BusinessAdvertisingCampaignBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ProposeCampaignBudgetAsync(
                MarketingOwnerScope.Business(businessId),
                new Infrastructure.Analytics.AdvertisingCampaignBudgetProposalRequest(
                    request.CampaignId, request.CampaignName, request.Budget),
                User.GetCanonicalUserId(),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingApprove(
        Guid businessId,
        [FromBody] BusinessAdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ApproveAsync(
                MarketingOwnerScope.Business(businessId), request.ProposalId, User.GetCanonicalUserId(), request.Revision, cancellationToken));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before approving." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/execute")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingExecute(
        Guid businessId,
        [FromBody] BusinessAdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ExecuteAsync(
                MarketingOwnerScope.Business(businessId), request.ProposalId, request.Revision, cancellationToken));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before execution." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/advertising/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingReject(
        Guid businessId,
        [FromBody] BusinessAdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.RejectAsync(
                MarketingOwnerScope.Business(businessId), request.ProposalId, User.GetCanonicalUserId(), request.Revision, cancellationToken));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before rejecting." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("analytics/advertising/campaign/{campaignId}/insights")]
    public async Task<IActionResult> AdvertisingCampaignInsights(
        Guid businessId,
        string campaignId,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(preset ?? "7d", fromUtc, toUtc, timezone);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
            return Json(await service.CampaignInsightsAsync(
                MarketingOwnerScope.Business(businessId),
                campaignId,
                new OpenAiAdsInsightsQuery(range.FromUtc, range.ToUtc, "daily"),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    public sealed record BusinessMarketingManagerPlanRequest(
        string? Preset,
        DateTime? FromUtc,
        DateTime? ToUtc,
        TrafficQualityMode QualityMode,
        string? TimezoneId,
        int? TimezoneOffsetMinutes,
        MarketingManagerGoalRequest Goal);

    [HttpGet("analytics/marketing-manager/context")]
    [HttpGet("analytics/ai-review-snapshot")]
    public async Task<IActionResult> MarketingAnalyticsContext(Guid businessId,
        [FromQuery] string? preset = "30d", [FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        [FromQuery] string? timezoneId = null, [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc,
            AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes), qualityMode);
        var builder = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.WebsiteAnalyticsAiDataBuilder>();
        var payload = await builder.BuildAsync(range, ScopeContext.ForBusiness(businessId), range.Label,
            "business", "All Traffic", TrafficType.All, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        if (Request.Path.Value?.EndsWith("ai-review-snapshot", StringComparison.OrdinalIgnoreCase) == true)
            return Json(new { snapshotText = Infrastructure.Analytics.WebsiteAnalyticsAiDataBuilder.FormatSnapshot(payload),
                generatedAtLocal = payload.GeneratedUtc.ToString("o"), payload.ScopeLabel, payload.RangeLabel,
                trafficFilterLabel = payload.TrafficFilter, payload.Warnings });
        return Json(payload);
    }

    [HttpPost("analytics/marketing-manager/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarketingManagerPlan(
        Guid businessId,
        [FromBody] BusinessMarketingManagerPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();

        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(
                request.TimezoneId,
                request.TimezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(request.Preset) ? "30d" : request.Preset,
                request.FromUtc,
                request.ToUtc,
                timezone,
                request.QualityMode);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IMarketingManagerService>();
            return Json(await service.PlanAsync(
                MarketingOwnerScope.Business(businessId),
                ScopeContext.ForBusiness(businessId),
                range,
                request.Goal,
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/marketing-manager/performance")]
    public async Task<IActionResult> MarketingManagerPerformance(
        Guid businessId,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();

        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                timezone,
                qualityMode);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IUnifiedMarketingPerformanceService>();
            return Json(await service.GetAsync(
                MarketingOwnerScope.Business(businessId),
                ScopeContext.ForBusiness(businessId),
                range,
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   Infrastructure.Analytics.OpenAiAdsExecutionException or
                                   TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/growth-economics")]
    public async Task<IActionResult> GrowthEconomics(
        Guid businessId,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                timezone,
                qualityMode);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IBlendedGrowthEconomicsService>();
            return Json(await service.GetAsync(
                MarketingOwnerScope.Business(businessId),
                ScopeContext.ForBusiness(businessId),
                range,
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   Infrastructure.Analytics.OpenAiAdsExecutionException or
                                   TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/openai-onboarding")]
    public async Task<IActionResult> OpenAiOnboarding(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsOnboardingService>();
        try { return Json(await service.GetAsync(MarketingOwnerScope.Business(businessId), cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("analytics/openai-product-feed")]
    public async Task<IActionResult> OpenAiProductFeed(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiProductFeedService>();
        try { return Json(await service.GetAsync(MarketingOwnerScope.Business(businessId), cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analytics/openai-product-feed/publish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishOpenAiProductFeed(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiProductFeedService>();
        try { return Json(await service.PublishAsync(MarketingOwnerScope.Business(businessId), cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("analytics/meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns(
        Guid businessId,
        string? preset = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        string? timezoneId = null,
        int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(preset ?? "30d", fromUtc, toUtc, timezone, qualityMode);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IMetaAdsService>();
            return Json(await service.GetCampaignsAsync(range, ScopeContext.ForBusiness(businessId), cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/meta-connection-status")]
    public async Task<IActionResult> MetaConnectionStatus(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
        var row = await store.GetStatusAsync(MarketingOwnerScope.Business(businessId), cancellationToken);
        var connected = row is not null &&
            !row.DisconnectedUtc.HasValue &&
            !string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext) &&
            (!row.AccessTokenExpiresUtc.HasValue || row.AccessTokenExpiresUtc > DateTime.UtcNow);
        return Json(new
        {
            connected,
            requiresAgentScope = false,
            accountId = row?.AdAccountId,
            accountName = row?.AdAccountName,
            businessId = row?.MetaBusinessManagerId,
            businessName = row?.MetaBusinessManagerName,
            metaUserName = row?.MetaUserName,
            connectedUtc = row?.ConnectedUtc,
            accessTokenExpiresUtc = row?.AccessTokenExpiresUtc,
            message = connected ? null : "Meta Ads not connected for this business."
        });
    }

    [HttpGet("analytics/meta-connect")]
    public async Task<IActionResult> MetaConnect(
        Guid businessId,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var fallback = $"/business/{businessId:D}/analytics";
        try
        {
            var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/business/meta-callback";
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingMetaAdsOAuthService>();
            var connect = oauth.BuildConnectUrl(
                MarketingOwnerScope.Business(businessId),
                string.IsNullOrWhiteSpace(returnUrl) ? fallback : returnUrl,
                callback);
            return Redirect(connect);
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("/business/meta-callback")]
    public async Task<IActionResult> MetaCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var fallback = "/";
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingMetaAdsOAuthService>();
            var inspected = oauth.InspectState(state ?? string.Empty);
            var businessId = inspected.Owner.CommerceBusinessId
                ?? throw new InvalidOperationException("Meta OAuth state is not business-scoped.");
            fallback = $"/business/{businessId:D}/analytics";

            if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(message)}");
            }

            var result = await oauth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, cancellationToken);
            if (result.Owner.CommerceBusinessId != businessId || result.Owner.AgentTrackingProfileId.HasValue)
                throw new InvalidOperationException("Meta OAuth owner scope does not match this business.");

            var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
            await store.SaveAdsAsync(result.Owner, result.Connection, cancellationToken);
            return Redirect($"{result.ReturnUrl}{(result.ReturnUrl.Contains('?') ? '&' : '?')}meta=connected");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpPost("analytics/meta-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
        await store.DisconnectAsync(MarketingOwnerScope.Business(businessId), cancellationToken);
        return Json(new { ok = true });
    }

    public sealed record BusinessMarketingSetupUpdateRequest(
        Guid MarketingRevision,
        Guid ProfileRevision,
        string? MetaPixelId,
        string? MetaTestEventCode,
        bool BookingEnabled,
        string? MicrosoftBookingsEmbedUrl,
        string? FallbackBookingUrl,
        string? BookingPageIdOrMailbox,
        string? CalendarEmail);

    public sealed record BusinessOpenAiConnectRequest(string AdvertiserApiKey, Guid? ExpectedRevision);
    public sealed record BusinessOpenAiRevisionRequest(Guid ConnectionRevision);

    [HttpGet("analytics/marketing-setup")]
    public async Task<IActionResult> MarketingSetup(Guid businessId, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();

        var owner = MarketingOwnerScope.Business(businessId);
        var profileService = ActivatorUtilities.CreateInstance<Infrastructure.WebsiteEditing.BusinessWebsiteProfileService>(HttpContext.RequestServices);
        var profile = await profileService.GetAsync(businessId, cancellationToken);
        var calendarAuthority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
        var calendarConnection = await calendarAuthority.GetAsync(owner, cancellationToken);
        var setup = await HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingProviderSetupProjection>()
            .ReadAsync(owner, cancellationToken);
        var evidence = setup.Evidence;
        var openAiConnection = setup.Connection;
        var openAiHealth = setup.Health;
        var openAiProvider = setup.Account;
        var openAiMeasurement = setup.Capability;
        var openAiProviderError = setup.OpenAiError;
        var accountReady =
            string.Equals(openAiProvider?.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(openAiProvider?.ReviewStatus, OpenAiAdsReviewStatuses.Approved, StringComparison.Ordinal) &&
            openAiConnection.PixelConfigured &&
            openAiConnection.ConversionsApiConfigured && !string.IsNullOrWhiteSpace(openAiConnection.ConversionDataSourceId);

        return Json(new
        {
            source = "canonical_business_marketing_setup",
            evidence,
            evidenceError = setup.EvidenceError,
            agentProfileId = (Guid?)null,
            agentName = business.DisplayName,
            status = new
            {
                publicReady = business.IsActive && string.Equals(business.Status, "Active", StringComparison.OrdinalIgnoreCase),
                metaCustomPixel = !string.IsNullOrWhiteSpace(profile.Settings.MetaPixelId),
                openAiReady = accountReady,
                googleReady = setup.Google.Ready,
                googleOptimizationReady = setup.GoogleMeasurement.MappingReady,
                tiktokReady = setup.TikTok.Ready,
                tiktokOptimizationReady = setup.TikTokMeasurement.MappingReady,
                bookingPersonalLive = profile.Settings.BookingEnabled &&
                    (!string.IsNullOrWhiteSpace(profile.Settings.BookingEmbedUrl) || !string.IsNullOrWhiteSpace(profile.Settings.BookingFallbackUrl)),
                calendarLinked = calendarConnection.Connected
            },
            marketing = new
            {
                revision = profile.Settings.ConnectionRevision,
                metaPixelId = profile.Settings.MetaPixelId,
                metaTestEventCode = profile.Settings.MetaTestEventCode,
                metaTestEventsConfigured = !string.IsNullOrWhiteSpace(profile.Settings.MetaTestEventCode),
                metaAdsConnected = setup.Meta.Connected,
                metaAccount = setup.Meta.AccountName ?? setup.Meta.AccountId,
                available = setup.Meta.Available, error = setup.Meta.Error,
                metaCapiConfiguredSecurely = setup.Meta.CapiConfigured,
                metaCapiManagedAutomatically = true
            },
            google = Infrastructure.Analytics.MarketingProviderSetupProjection.External(
                setup.Google, setup.GoogleMeasurement),
            tiktok = Infrastructure.Analytics.MarketingProviderSetupProjection.External(
                setup.TikTok, setup.TikTokMeasurement),
            openAi = new
            {
                revision = openAiConnection.Revision,
                exists = openAiConnection.Exists,
                connected = openAiConnection.Connected,
                accountId = openAiConnection.AccountId,
                accountName = openAiConnection.AccountName,
                role = openAiConnection.Role,
                permissions = openAiConnection.Permissions,
                authorizationMethod = openAiConnection.AuthorizationMethod,
                connectionMethod = openAiConnection.Connected ? "Advertiser API key verified" : null,
                providerRole = openAiConnection.Role,
                accountStatus = openAiProvider?.Status,
                accountUrl = openAiProvider?.AccountUrl,
                previewUrl = openAiProvider?.PreviewUrl,
                timezone = openAiProvider?.Timezone,
                currencyCode = openAiProvider?.CurrencyCode,
                reviewStatus = openAiProvider?.ReviewStatus ?? openAiConnection.ReviewStatus,
                reviewReason = openAiProvider?.ReviewReason,
                providerStatusFresh = openAiProvider is not null,
                providerStatusError = openAiProviderError,
                measurementCapabilityStatus = openAiMeasurement?.Status,
                measurementCapabilityHttpStatus = openAiMeasurement?.HttpStatusCode,
                measurementCapabilityDetail = openAiMeasurement?.Detail,
                pixelId = openAiConnection.PixelId,
                pixelConfigured = openAiConnection.PixelConfigured,
                conversionsApiConfigured = openAiConnection.ConversionsApiConfigured,
                conversionDataSourceId = openAiConnection.ConversionDataSourceId,
                lastVerifiedUtc = openAiConnection.LastVerifiedUtc,
                connectedUtc = openAiConnection.ConnectedUtc,
                health = new
                {
                    status = openAiHealth.Status,
                    pending = openAiHealth.PendingDeliveries,
                    retrying = openAiHealth.RetryableDeliveries,
                    failed = openAiHealth.FailedDeliveries,
                    sent = openAiHealth.SentDeliveries,
                    otherDestinationReceipts = openAiHealth.OtherDestinationReceipts,
                    otherDestinationUnresolved = openAiHealth.OtherDestinationUnresolved,
                    lastSentUtc = openAiHealth.LastSentUtc,
                    providerMonitoringAvailable = openAiHealth.ProviderMonitoringAvailable,
                    recentProviderEvents = openAiHealth.RecentProviderEvents
                }
            },
            calendar = new
            {
                connected = calendarConnection.Connected,
                revision = calendarConnection.Revision,
                accountName = calendarConnection.AccountName,
                email = calendarConnection.Email,
                authorizationMethod = calendarConnection.AuthorizationMethod,
                permissions = calendarConnection.Permissions,
                connectedUtc = calendarConnection.ConnectedUtc,
                lastVerifiedUtc = calendarConnection.LastVerifiedUtc,
                accessTokenExpiresUtc = calendarConnection.AccessTokenExpiresUtc
            },
            booking = new
            {
                revision = profile.Settings.ProfileRevision,
                enabled = profile.Settings.BookingEnabled,
                microsoftBookingsEmbedUrl = profile.Settings.BookingEmbedUrl,
                fallbackBookingUrl = profile.Settings.BookingFallbackUrl,
                bookingPageIdOrMailbox = profile.Settings.BookingMailboxId,
                calendarEmail = profile.Settings.BookingCalendarEmail
            }
        });
    }

    public sealed record BusinessExternalAdsAccountRequest(string Provider, string AccountId);
    public sealed record BusinessExternalAdsDisconnectRequest(string Provider);

    [HttpGet("analytics/external-ads/connect")]
    public async Task<IActionResult> ExternalAdsConnect(
        Guid businessId,
        [FromQuery] string provider,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var fallback = $"/business/{businessId:D}/analytics";
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : fallback;
        try
        {
            var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/business/external-ads/callback";
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            return Redirect(oauth.BuildConnectUrl(
                MarketingOwnerScope.Business(businessId),
                provider,
                target,
                callback));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}provider=external&status=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("/business/external-ads/callback")]
    public async Task<IActionResult> ExternalAdsCallback(
        [FromQuery] string? code = null,
        [FromQuery(Name = "auth_code")] string? authCode = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var fallback = "/";
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            var inspected = oauth.InspectState(state ?? string.Empty);
            var businessId = inspected.Owner.CommerceBusinessId
                ?? throw new InvalidOperationException("External Ads OAuth state is not business-scoped.");
            fallback = $"/business/{businessId:D}/analytics";
            if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
            var target = Url.IsLocalUrl(inspected.ReturnUrl) ? inspected.ReturnUrl : fallback;

            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}provider={Uri.EscapeDataString(inspected.Provider)}&status=error&message={Uri.EscapeDataString(message)}");
            }

            var result = await oauth.CompleteCallbackAsync(
                inspected.Provider,
                authCode ?? code ?? string.Empty,
                state ?? string.Empty,
                cancellationToken);
            if (result.Owner != inspected.Owner)
                throw new InvalidOperationException("External Ads OAuth owner scope changed during authorization.");

            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}provider={Uri.EscapeDataString(result.Provider)}&status=connected");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Redirect($"{fallback}{(fallback.Contains('?') ? '&' : '?')}provider=external&status=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("analytics/external-ads/accounts")]
    public async Task<IActionResult> ExternalAdsAccounts(
        Guid businessId,
        [FromQuery] string provider,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            return Json(new
            {
                provider,
                accounts = await oauth.GetAccountsAsync(MarketingOwnerScope.Business(businessId), provider, cancellationToken)
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("analytics/external-ads/select-account")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalAdsSelectAccount(
        Guid businessId,
        [FromBody] BusinessExternalAdsAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            await oauth.SelectAccountAsync(
                MarketingOwnerScope.Business(businessId),
                request.Provider,
                request.AccountId,
                cancellationToken);
            return await MarketingSetup(businessId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("analytics/external-ads/measurement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalAdsMeasurement(
        Guid businessId,
        [FromBody] MarketingProviderMeasurementUpdate request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        try
        {
            var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
            await store.SaveProviderMeasurementConfigurationAsync(
                MarketingOwnerScope.Business(businessId),
                request,
                cancellationToken);
            return await MarketingSetup(businessId, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Marketing provider connection changed. Reload and try again." });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("analytics/external-ads/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalAdsDisconnect(
        Guid businessId,
        [FromBody] BusinessExternalAdsDisconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        try
        {
            var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
            await store.DisconnectAsync(
                MarketingOwnerScope.Business(businessId),
                request.Provider,
                cancellationToken);
            return await MarketingSetup(businessId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    public sealed record BusinessCalendarConnectionRevisionRequest(Guid ConnectionRevision);

    [HttpGet("analytics/calendar-connect")]
    public async Task<IActionResult> CalendarConnect(
        Guid businessId,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var fallback = $"/business/{businessId:D}/analytics";
        try
        {
            var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/business/calendar-callback";
            var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
            var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : fallback;
            return Redirect(authority.BuildConnectUrl(
                MarketingOwnerScope.Business(businessId),
                target,
                callback));
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}?calendar=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("/business/calendar-callback")]
    public async Task<IActionResult> CalendarCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var fallback = "/";
        try
        {
            var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
            var inspected = authority.InspectState(state ?? string.Empty);
            var businessId = inspected.Owner.CommerceBusinessId
                ?? throw new InvalidOperationException("Microsoft Calendar OAuth state is not business-scoped.");
            fallback = $"/business/{businessId:D}/analytics";
            if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
            var target = Url.IsLocalUrl(inspected.ReturnUrl) ? inspected.ReturnUrl : fallback;

            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}calendar=error&message={Uri.EscapeDataString(message)}");
            }

            var connected = await authority.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, cancellationToken);
            if (connected.Owner != inspected.Owner || !connected.Connected)
                throw new InvalidOperationException("Microsoft Calendar authorization could not be verified for this business.");

            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}calendar=connected");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}{(fallback.Contains('?') ? '&' : '?')}calendar=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpPost("analytics/calendar-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CalendarDisconnect(
        Guid businessId,
        [FromBody] BusinessCalendarConnectionRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
        try
        {
            await authority.DisconnectAsync(
                MarketingOwnerScope.Business(businessId),
                request.ConnectionRevision,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Microsoft Calendar connection changed. Reload and try again." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpPost("analytics/marketing-setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMarketingSetup(
        Guid businessId,
        [FromBody] BusinessMarketingSetupUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();

        var profileService = ActivatorUtilities.CreateInstance<Infrastructure.WebsiteEditing.BusinessWebsiteProfileService>(HttpContext.RequestServices);
        try
        {
            await profileService.SaveAsync(businessId, new Infrastructure.WebsiteEditing.BusinessWebsiteProfileInput
            {
                ProfileRevision = request.ProfileRevision,
                ConnectionRevision = request.MarketingRevision,
                BookingEnabled = request.BookingEnabled,
                BookingEmbedUrl = request.MicrosoftBookingsEmbedUrl,
                BookingFallbackUrl = request.FallbackBookingUrl,
                BookingMailboxId = request.BookingPageIdOrMailbox,
                BookingCalendarEmail = request.CalendarEmail,
                MetaPixelId = request.MetaPixelId,
                MetaTestEventCode = request.MetaTestEventCode
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Marketing Setup changed. Reload and try again." });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpPost("analytics/openai-connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        try
        {
            await connector.ConnectAsync(MarketingOwnerScope.Business(businessId), request.AdvertiserApiKey, request.ExpectedRevision, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { return BadRequest(new { message = ex.Message }); }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (HttpRequestException)
        { return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be verified right now." }); }

        return await MarketingCommandReceiptAsync(businessId, cancellationToken);
    }

    [HttpPost("analytics/openai-refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        Infrastructure.Analytics.OpenAiAdsRefreshResult refresh;
        try
        {
            refresh = await connector.RefreshAsync(MarketingOwnerScope.Business(businessId), request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (InvalidOperationException ex)
        { return BadRequest(new { message = ex.Message }); }
        catch (HttpRequestException)
        { return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be refreshed right now." }); }

        return await MarketingCommandReceiptAsync(businessId, cancellationToken, refresh.PixelProvisioning);
    }

    [HttpPost("analytics/openai-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsAccountConnectionAuthority>();
        try
        {
            await authority.DisconnectAsync(MarketingOwnerScope.Business(businessId), request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (InvalidOperationException ex)
        { return BadRequest(new { message = ex.Message }); }

        return await MarketingCommandReceiptAsync(businessId, cancellationToken);
    }

    private async Task<IActionResult> MarketingCommandReceiptAsync(Guid businessId, CancellationToken cancellationToken, object? pixelProvisioning = null)
    {
        // The mutation has committed. A read failure must not turn its receipt into a failed command.
        try
        {
            var refreshed = await MarketingSetup(businessId, cancellationToken);
            return Json(new { ok = true, setup = (refreshed as JsonResult)?.Value,
                setupStatus = refreshed is JsonResult ? "available" : "unavailable", pixelProvisioning });
        }
        catch (Exception)
        { return Json(new { ok = true, setup = (object?)null, setupStatus = "unavailable", pixelProvisioning }); }
    }

    [HttpGet("analytics/{**section}")]
    public async Task<IActionResult> AnalyticsData(Guid businessId, string section, string? preset = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, TrafficType trafficType = TrafficType.All,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, string? timezoneId = null,
        int? timezoneOffsetMinutes = null, string? metric = null, string? visitorId = null, string? sessionId = null,
        string? quoteType = null, string? campaign = null, string? pageMode = null, string? scoreTier = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        TimeRangeRequest range;
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            range = TimeRangeRequest.FromPreset(preset ?? "30d", fromUtc, toUtc, timezone, qualityMode);
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest("Choose a valid time range and time zone."); }
        try
        {
            var result = await workspace.AnalyticsDataAsync(businessId, section, range, trafficType,
                metric, visitorId, sessionId, quoteType, campaign, pageMode, scoreTier, cancellationToken);
            return result is null ? NotFound() : Json(result);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(Guid businessId, CancellationToken cancellationToken)
    {
        var business = await ResolveBusinessAsync(businessId, "settings", cancellationToken);
        if (business is null) return Forbid();
        return View("~/Views/Shared/BusinessWorkspaceSettings.cshtml", await workspace.CustomizeAsync(business, cancellationToken));
    }

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(Guid businessId, BusinessWorkspaceSettingsInput input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try { await workspace.CustomizeAsync(businessId, input, cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict("Settings changed in another session. Reload before saving."); }
        catch (ValidationException ex) { return BadRequest(ex.Message); }
        return RedirectToAction(nameof(Settings), new { businessId });
    }
}
