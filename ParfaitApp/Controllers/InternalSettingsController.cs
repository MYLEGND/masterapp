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

    public InternalSettingsController(IParfaitBusinessProfileService profileService)
    {
        _profileService = profileService;
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
            return View(model);
        }

        await _profileService.SaveProfileAsync(model, ct);

        TempData["ProfileStatus"] = "Parfait business profile saved.";
        return RedirectToAction(nameof(BusinessProfile));
    }
}
