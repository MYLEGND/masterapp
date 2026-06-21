using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/dashboard")]
public sealed class DashboardController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View(new ParfaitInternalProfileViewModel());
    }
}
