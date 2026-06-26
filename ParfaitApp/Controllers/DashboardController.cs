using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Security;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/dashboard")]
public sealed class DashboardController : Controller
{
    private readonly ParfaitInternalWorkspaceService _workspace;

    public DashboardController(ParfaitInternalWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    [HttpGet("")]
    [ParfaitInternalPage(
        "Dashboard",
        "Core",
        "Internal operating overview for Parfait commerce, growth, and analytics.",
        1,
        1)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        return View(await _workspace.GetSnapshotAsync(ct));
    }
}
