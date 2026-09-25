using Domain.Entities;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public async Task<IActionResult> Analytics(Guid businessId, int days = 30, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.AnalyticsAsync(business, days, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        ViewData["AnalyticsBase"] = $"/business/{businessId}/analytics";
        ViewData["InitialScopeLabel"] = business.DisplayName;
        ViewData["InitialRangePreset"] = days == 7 ? "7d" : days == 90 ? "90d" : "30d";
        ViewData["InitialRangeLabel"] = $"Last {days} days";
        ViewData["InitialSummaryJson"] = JsonSerializer.Serialize(model.Summary);
        ViewData["BusinessWorkspace"] = model;
        return View("~/Views/WebsiteAnalytics/Index.cshtml");
    }

    [HttpGet("analytics/{**section}")]
    public async Task<IActionResult> AnalyticsData(Guid businessId, string section, string? preset = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, TrafficType trafficType = TrafficType.All,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, string? timezoneId = null,
        string? metric = null, string? visitorId = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        TimeRangeRequest range;
        try
        {
            var timezone = string.IsNullOrWhiteSpace(timezoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            range = TimeRangeRequest.FromPreset(preset ?? "30d", fromUtc, toUtc, timezone, qualityMode);
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest("Choose a valid time range and time zone."); }
        try
        {
            var result = await workspace.AnalyticsDataAsync(businessId, section, range, trafficType,
                metric, visitorId, sessionId, cancellationToken);
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
