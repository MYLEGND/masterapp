using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/businesses")]
public sealed class InternalBusinessesController : Controller
{
    private readonly IParfaitBusinessPlatformService _platform;

    public InternalBusinessesController(IParfaitBusinessPlatformService platform)
    {
        _platform = platform;
    }

    [HttpGet("")]
    [ParfaitInternalPage(
        "Businesses",
        "Platform",
        "Platform-owner console for business accounts, subscriptions, storefront scope, and tenant access.",
        2,
        1,
        FounderOnly = true)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        return View(await _platform.GetConsoleAsync(User, ct));
    }

    [HttpPost("")]
    [ParfaitInternalPageAccess("/internal/businesses")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ParfaitBusinessCreateInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var vm = await _platform.GetConsoleAsync(User, ct);
            vm.NewBusiness = input;
            return View("Index", vm);
        }

        try
        {
            await _platform.CreateBusinessAsync(User, input, ct);
            TempData["BusinessStatus"] = $"{input.DisplayName.Trim()} business account created.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            TempData["BusinessError"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }
}
