using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/settings")]
public sealed class InternalSettingsController : Controller
{
    [HttpGet("business-profile")]
    public IActionResult BusinessProfile()
    {
        return View(new ParfaitBusinessProfileViewModel());
    }
}
