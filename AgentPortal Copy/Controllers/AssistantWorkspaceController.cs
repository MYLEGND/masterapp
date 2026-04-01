using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AgentPortal.Controllers;

[Authorize]
public class AssistantWorkspaceController : Controller
{
    private readonly MasterAppDbContext _db;

    public AssistantWorkspaceController(MasterAppDbContext db)
    {
        _db = db;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private string? GetUserOid() =>
        Norm(User.FindFirst("oid")?.Value
            ?? User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value);

    private string? GetEffectiveAgentOid() =>
        HttpContext.Items.TryGetValue("EffectiveAgentOid", out var val) ? val as string : null;

    private async Task<AssistantWorkspaceViewModel?> ResolveAssistantAsync(Guid? assistantId)
    {
        var userOid = GetUserOid();
        var effectiveAgentOid = GetEffectiveAgentOid();

        // If the logged-in user is the assistant, load their record directly.
        if (!string.IsNullOrWhiteSpace(userOid))
        {
            var asAssistant = await _db.AgentAssistants
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AssistantUserId == userOid);

            if (asAssistant != null)
            {
                return ToVm(asAssistant, parentAgentOid: asAssistant.ParentAgentUserId, isSelf: true);
            }
        }

        // Otherwise the logged-in user should be the parent agent; require assistantId.
        if (!assistantId.HasValue || string.IsNullOrWhiteSpace(effectiveAgentOid))
            return null;

        var asAgent = await _db.AgentAssistants
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assistantId && a.ParentAgentUserId == Norm(effectiveAgentOid));

        return asAgent == null ? null : ToVm(asAgent, parentAgentOid: effectiveAgentOid!, isSelf: false);
    }

    private static AssistantWorkspaceViewModel ToVm(AgentAssistant a, string parentAgentOid, bool isSelf)
    {
        return new AssistantWorkspaceViewModel
        {
            Id = a.Id,
            FirstName = a.FirstName,
            LastName = a.LastName,
            Email = a.Email,
            AssistantUserId = a.AssistantUserId,
            ParentAgentUserId = parentAgentOid,
            IsActive = a.IsActive,
            HasLoggedIn = !string.IsNullOrWhiteSpace(a.AssistantUserId),
            InvitedAtUtc = a.InvitedAt,
            CreatedUtc = a.CreatedUtc,
            IsSelfView = isSelf
        };
    }

    [HttpGet]
    public async Task<IActionResult> Profile(Guid? id)
    {
        var vm = await ResolveAssistantAsync(id);
        if (vm == null) return NotFound();

        ViewData["Title"] = "Assistant Workspace";
        // Render the shared assistant home surface so agents see exactly what assistants see.
        return View("~/Views/Assistant/Index.cshtml", vm);
    }
}
