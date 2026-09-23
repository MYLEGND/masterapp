using Domain.Entities;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using Shared.Crm;

namespace Infrastructure.Businesses;

[Authorize]
[Route("business/{businessId:guid}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public abstract class BusinessWorkspaceControllerBase(BusinessWorkspaceService workspace) : Controller
{
    protected abstract Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct);

    [HttpGet("crm")]
    [HttpGet("crm/{contactId}")]
    public async Task<IActionResult> Crm(Guid businessId, string? contactId, string kind = "Lead", string? search = null, int page = 1, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "crm", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.CrmAsync(business, kind, search, page, contactId, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        if (contactId is not null && model.Selected is null) return NotFound();
        return View("~/Views/Shared/BusinessWorkspace.cshtml", model);
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
        return RedirectToAction(nameof(Crm), new { businessId, contactId, kind = input.Kind });
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(Guid businessId, int days = 30, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.AnalyticsAsync(business, days, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        return View("~/Views/Shared/BusinessWorkspace.cshtml", model);
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(Guid businessId, CancellationToken cancellationToken)
    {
        var business = await ResolveBusinessAsync(businessId, "settings", cancellationToken);
        if (business is null) return Forbid();
        return View("~/Views/Shared/BusinessWorkspace.cshtml", await workspace.CustomizeAsync(business, cancellationToken));
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
