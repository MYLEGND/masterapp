using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/dashboard")]
public sealed class DashboardController : Controller
{
    [HttpGet("")]
    [ParfaitInternalPage(
        "Dashboard",
        "Core",
        "Internal operating overview for Parfait commerce, growth, and analytics.",
        1,
        1)]
    public IActionResult Index()
    {
        return View(new ParfaitInternalProfileViewModel());
    }
}
