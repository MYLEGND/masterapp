using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Services;
using AgentPortal.Models;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class DashboardController : Controller
{
    private readonly IExecutionEngine _execution;
    private readonly IBlockerService _blockers;

    public DashboardController(IExecutionEngine execution, IBlockerService blockers)
    {
        _execution = execution;
        _blockers = blockers;
    }

    [HttpGet]
    public async Task<IActionResult> Counts()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var today = await _execution.GetTodayAsync(ownerId);
        var overdue = await _execution.GetOverdueAsync(ownerId);
        var blockers = await _blockers.GetOpenByOwnerAsync(ownerId);
        return Json(new { today = today.Count, overdue = overdue.Count, blockers = blockers.Count });
    }

    private string CurrentUserId() => User.GetStableUserId();

    // GET: /Dashboard  (and /Dashboard/Index)
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();

        var vm = new DashboardExecutionViewModel
        {
            Today = await _execution.GetTodayAsync(ownerId),
            Overdue = await _execution.GetOverdueAsync(ownerId),
            Blockers = await _blockers.GetOpenByOwnerAsync(ownerId)
        };

        return View(vm); // renders Views/Dashboard/Index.cshtml
    }

    [HttpGet]
    public async Task<IActionResult> Today()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetTodayAsync(ownerId);
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Overdue()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetOverdueAsync(ownerId);
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Blockers()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _blockers.GetOpenByOwnerAsync(ownerId);
        return PartialView("~/Views/Shared/_BlockerList.cshtml", items);
    }

    public record CompleteActionRequest(Guid Id);

    [HttpPost]
    [IgnoreAntiforgeryToken] // quick-view actions use authenticated AJAX and can outlive stale anti-forgery tokens
    public async Task<IActionResult> CompleteAction([FromBody] CompleteActionRequest request)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        if (request == null || request.Id == Guid.Empty) return BadRequest();

        var updated = await _execution.CompleteActionAsync(request.Id, ownerId);
        if (updated == null) return NotFound();

        return Ok(new { success = true });
    }
}
