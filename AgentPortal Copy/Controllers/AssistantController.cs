using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Models;

namespace AgentPortal.Controllers;

[Authorize]
[Route("Assistant")]
public class AssistantController : Controller
{
    private readonly MasterAppDbContext _db;

    public AssistantController(MasterAppDbContext db)
    {
        _db = db;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public IActionResult Index()
    {
        if (!HttpContext.Items.TryGetValue("IsAssistant", out var isAssistantObj)
            || isAssistantObj is not bool isAssistant
            || !isAssistant)
        {
            return RedirectToAction("Index", "Home");
        }

        var oid = (User.FindFirst("oid")?.Value
            ?? User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? "").Trim().ToLowerInvariant();

        var assistant = _db.AgentAssistants
            .AsNoTracking()
            .FirstOrDefault(a => a.AssistantUserId == oid);

        var vm = assistant == null
            ? null
            : new AssistantWorkspaceViewModel
            {
                Id = assistant.Id,
                FirstName = assistant.FirstName,
                LastName = assistant.LastName,
                Email = assistant.Email,
                AssistantUserId = assistant.AssistantUserId,
                ParentAgentUserId = assistant.ParentAgentUserId,
                IsActive = assistant.IsActive,
                HasLoggedIn = !string.IsNullOrWhiteSpace(assistant.AssistantUserId),
                InvitedAtUtc = assistant.InvitedAt,
                CreatedUtc = assistant.CreatedUtc,
                IsSelfView = true
            };

        return View(vm);
    }
}
