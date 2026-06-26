using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/settings")]
public sealed class InternalSettingsController : Controller
{
    private readonly IParfaitBusinessProfileService _profileService;
    private readonly IParfaitMetaAdsOAuthService _metaAdsOAuth;

    public InternalSettingsController(
        IParfaitBusinessProfileService profileService,
        IParfaitMetaAdsOAuthService metaAdsOAuth)
    {
        _profileService = profileService;
        _metaAdsOAuth = metaAdsOAuth;
    }

    [HttpGet("business-profile")]
    public async Task<IActionResult> BusinessProfile(CancellationToken ct)
    {
        return View(await _profileService.GetProfileAsync(ct));
    }

    [HttpPost("business-profile")]
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

    [AllowAnonymous]
    [HttpGet("meta-callback")]
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
    public async Task<IActionResult> MetaConnectionStatus(CancellationToken ct)
    {
        return Json(await _profileService.GetMetaConnectionStatusAsync(ct));
    }

    [HttpPost("meta-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect(CancellationToken ct)
    {
        await _profileService.DisconnectMetaAsync(ct);
        return Json(new { ok = true });
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
