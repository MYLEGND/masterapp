using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/settings")]
public sealed class InternalSettingsController : Controller
{
    private readonly IParfaitBusinessProfileService _profileService;
    private readonly IParfaitMetaAdsOAuthService _metaAdsOAuth;
    private readonly IParfaitTeamAccessService _teamAccess;

    public InternalSettingsController(
        IParfaitBusinessProfileService profileService,
        IParfaitMetaAdsOAuthService metaAdsOAuth,
        IParfaitTeamAccessService teamAccess)
    {
        _profileService = profileService;
        _metaAdsOAuth = metaAdsOAuth;
        _teamAccess = teamAccess;
    }

    [HttpGet("business-profile")]
    [ParfaitInternalPage(
        "Business Profile",
        "Settings",
        "Store identity, checkout ownership, Meta configuration, and analytics alignment.",
        2,
        1)]
    public async Task<IActionResult> BusinessProfile(CancellationToken ct)
    {
        return View(await _profileService.GetProfileAsync(ct));
    }

    [HttpPost("business-profile")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessProfile(ParfaitBusinessProfileViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var current = await _profileService.GetProfileAsync(ct);
            model.HasSecureMetaCapiAccessToken = current.HasSecureMetaCapiAccessToken;
            model.HasActiveMetaAdsConnection = current.HasActiveMetaAdsConnection;
            model.MetaConnectionLabel = current.MetaConnectionLabel;
            model.DomainStatus = current.DomainStatus;
            model.AnalyticsStatus = current.AnalyticsStatus;
            model.TrustStatus = current.TrustStatus;
            return View(model);
        }

        await _profileService.SaveProfileAsync(model, ct);

        TempData["ProfileStatus"] = "Parfait business profile saved.";
        return RedirectToAction(nameof(BusinessProfile));
    }

    [HttpGet("meta-connect")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    public IActionResult MetaConnect([FromQuery] string? returnUrl = null)
    {
        var target = ResolveReturnUrl(returnUrl);

        try
        {
            var connectUrl = _metaAdsOAuth.BuildConnectUrl(target);
            return Redirect(connectUrl);
        }
        catch (InvalidOperationException ex)
        {
            return Redirect(AppendMetaStatus(target, "error", ex.Message));
        }
    }

    [HttpGet("meta-callback")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    public async Task<IActionResult> MetaCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null)
    {
        var target = Url.Action(nameof(BusinessProfile), "InternalSettings") ?? "/internal/settings/business-profile";

        if (!string.IsNullOrWhiteSpace(error))
        {
            var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
            return Redirect(AppendMetaStatus(target, "error", message));
        }

        try
        {
            var record = await _metaAdsOAuth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, HttpContext.RequestAborted);
            await _profileService.SaveMetaConnectionAsync(record, HttpContext.RequestAborted);
            return Redirect(AppendMetaStatus(target, "connected"));
        }
        catch (InvalidOperationException ex)
        {
            return Redirect(AppendMetaStatus(target, "error", ex.Message));
        }
        catch
        {
            return Redirect(AppendMetaStatus(target, "error", "Meta connection failed unexpectedly. Please try again."));
        }
    }

    [HttpGet("meta-connection-status")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    public async Task<IActionResult> MetaConnectionStatus(CancellationToken ct)
    {
        return Json(await _profileService.GetMetaConnectionStatusAsync(ct));
    }

    [HttpPost("meta-disconnect")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect(CancellationToken ct)
    {
        await _profileService.DisconnectMetaAsync(ct);
        return Json(new { ok = true });
    }

    [HttpGet("team")]
    [ParfaitInternalPage(
        "Team",
        "Settings",
        "Founder-managed team access, page visibility, and internal route permissions.",
        2,
        2)]
    public async Task<IActionResult> Team(CancellationToken ct)
    {
        return View(await _teamAccess.GetTeamManagementViewModelAsync(ct));
    }

    [HttpPost("team/add")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTeamMember([Bind(Prefix = "NewMember")] ParfaitTeamCreateMemberInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var vm = await _teamAccess.GetTeamManagementViewModelAsync(ct);
            vm.NewMember = input;
            return View("Team", vm);
        }

        try
        {
            var inviteScheme = Request.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                ? Request.Scheme
                : Uri.UriSchemeHttps;
            var inviteUrl = Url.Action(
                                nameof(InternalController.Login),
                                "Internal",
                                new { returnUrl = "/internal" },
                                inviteScheme)
                            ?? $"{inviteScheme}://{Request.Host}/internal/login";
            var invitedBy = User.FindFirst("name")?.Value
                            ?? User.Identity?.Name
                            ?? User.FindFirst("preferred_username")?.Value
                            ?? "Parfait Admin";

            await _teamAccess.AddMemberAsync(input, inviteUrl, invitedBy, ct);
            TempData["TeamStatus"] = $"{input.Email.Trim()} added to Parfait internal and invited by email.";
            return RedirectToAction(nameof(Team));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("NewMember.Email", ex.Message);
            var vm = await _teamAccess.GetTeamManagementViewModelAsync(ct);
            vm.NewMember = input;
            return View("Team", vm);
        }
    }

    [HttpPost("team/{id:guid}")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTeamMember(Guid id, ParfaitTeamUpdateMemberInput input, CancellationToken ct)
    {
        try
        {
            await _teamAccess.UpdateMemberAsync(id, input, ct);
            TempData["TeamStatus"] = "Team member permissions updated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["TeamError"] = ex.Message;
        }

        return RedirectToAction(nameof(Team));
    }

    [HttpPost("team/{id:guid}/remove")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveTeamMember(Guid id, CancellationToken ct)
    {
        try
        {
            await _teamAccess.RemoveMemberAsync(id, ct);
            TempData["TeamStatus"] = "Team member removed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["TeamError"] = ex.Message;
        }

        return RedirectToAction(nameof(Team));
    }

    private string ResolveReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;

        return Url.Action(nameof(BusinessProfile), "InternalSettings") ?? "/internal/settings/business-profile";
    }

    private static string AppendMetaStatus(string target, string meta, string? message = null)
    {
        var separator = target.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = $"{target}{separator}meta={Uri.EscapeDataString(meta)}";

        if (!string.IsNullOrWhiteSpace(message))
            url += $"&message={Uri.EscapeDataString(message)}";

        return url;
    }
}
