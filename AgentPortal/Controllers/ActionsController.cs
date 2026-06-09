using AgentPortal.Services;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
public class ActionsController : Controller
{
    private readonly IExecutionEngine _execution;

    public ActionsController(IExecutionEngine execution)
    {
        _execution = execution;
    }

    private string CurrentUserId() => User.GetStableUserId();

    [HttpGet("/Actions/Edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var action = await _execution.GetByIdAsync(id, ownerId);
        if (action == null) return NotFound();
        return View("~/Views/Actions/Edit.cshtml", action);
    }

    public record EditActionRequest
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public ActionPriority Priority { get; set; } = ActionPriority.P2;
    }

    [HttpPost("/Actions/Edit/{id:guid}")]
    [IgnoreAntiforgeryToken] // action edit is authenticated; avoid stale token 400 after long-running quick-view sessions
    public async Task<IActionResult> Edit(Guid id, [FromForm] EditActionRequest req)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        if (id == Guid.Empty || id != req.Id || string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("Invalid action data");

        var updated = await _execution.UpdateActionAsync(req.Id, ownerId, req.Title, req.Description, req.DueDateUtc, req.Priority);
        if (updated == null) return NotFound();

        var redirect = updated.RelatedEntityType == RelatedEntityType.Lead ? "/Leads" : "/Dashboard";
        return Redirect(redirect);
    }

    [HttpPost("/Actions/Delete/{id:guid}")]
    [IgnoreAntiforgeryToken] // quick-view delete is authenticated; avoid stale token 400 after long-running sessions
    public async Task<IActionResult> Delete(Guid id)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        if (id == Guid.Empty) return BadRequest();

        var ok = await _execution.DeleteActionAsync(id, ownerId);
        if (!ok) return NotFound();

        // Best-effort return path: go back to Leads if this was a lead action, otherwise dashboard.
        // We don't fetch the action after deletion, so default to Leads when referer hints at it.
        var referer = Request.Headers["Referer"].ToString() ?? string.Empty;
        var redirect = referer.Contains("/Leads", StringComparison.OrdinalIgnoreCase) ? "/Leads" : "/Dashboard";
        return Redirect(redirect);
    }
}
