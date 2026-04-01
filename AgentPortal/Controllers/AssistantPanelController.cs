using AgentPortal.Models;
using AgentPortal.Services;
using AgentPortal.Filters;
using Domain.Entities;
using Infrastructure.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class AssistantPanelController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly ClientProvisioningService _provisioning;
    private readonly ILogger<AssistantPanelController> _logger;

    public AssistantPanelController(
        MasterAppDbContext db,
        ClientProvisioningService provisioning,
        ILogger<AssistantPanelController> logger)
    {
        _db = db;
        _provisioning = provisioning;
        _logger = logger;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private string GetInviterName()
    {
        var display = (User.FindFirstValue("name")
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.FindFirstValue(ClaimTypes.GivenName)
            ?? "").Trim();

        if (string.IsNullOrWhiteSpace(display)) return "Your agent";

        var first = display.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "Your agent" : first;
    }

    private string GetAgentOidOrThrow()
    {
        if (HttpContext.Items.TryGetValue("EffectiveAgentOid", out var cached)
            && cached is string oid && !string.IsNullOrWhiteSpace(oid))
            return oid;

        var raw = Norm(User.FindFirst("oid")?.Value
            ?? User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value);

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Missing OID claim.");

        return raw;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var assistants = await _db.AgentAssistants
            .AsNoTracking()
            .Where(a => a.ParentAgentUserId == agentOid)
            .OrderByDescending(a => a.CreatedUtc)
            .ToListAsync();

        var vm = new AssistantPanelIndexViewModel
        {
            Assistants = assistants.Select(a => new AssistantRowViewModel
            {
                Id = a.Id,
                FirstName = a.FirstName,
                LastName = a.LastName,
                Email = a.Email,
                IsActive = a.IsActive,
                HasLoggedIn = !string.IsNullOrWhiteSpace(a.AssistantUserId),
                InvitedAt = a.InvitedAt.ToString("MMM d, yyyy")
            }).ToList()
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var a = await _db.AgentAssistants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ParentAgentUserId == agentOid);

        if (a == null) return NotFound();

        var vm = new AssistantDetailViewModel
        {
            Id = a.Id,
            FirstName = a.FirstName,
            LastName = a.LastName,
            Email = a.Email,
            IsActive = a.IsActive,
            HasLoggedIn = !string.IsNullOrWhiteSpace(a.AssistantUserId),
            AssistantUserId = a.AssistantUserId,
            ParentAgentUserId = a.ParentAgentUserId,
            InvitedAt = a.InvitedAt,
            CreatedUtc = a.CreatedUtc
        };

        return View(vm);
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateAssistantViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAssistantViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var email = Norm(model.Email);

        var exists = await _db.AgentAssistants.AnyAsync(a =>
            a.ParentAgentUserId == agentOid && a.NormalizedEmail == email);
        if (exists)
        {
            ModelState.AddModelError(nameof(model.Email), "Assistant already exists for this email.");
            return View(model);
        }

        var assistant = new AgentAssistant
        {
            ParentAgentUserId = agentOid,
            FirstName = model.FirstName.Trim(),
            LastName = model.LastName.Trim(),
            Email = email,
            NormalizedEmail = email,
            IsActive = true,
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };

        _db.AgentAssistants.Add(assistant);
        await _db.SaveChangesAsync();

        try
        {
            var inviterName = GetInviterName();
            var assistantObjectId = await _provisioning.SendAssistantInviteEmailAsync(assistant.Email, assistant.FirstName, inviterName);
            if (!string.IsNullOrWhiteSpace(assistantObjectId)
                && !string.Equals(assistant.AssistantUserId, assistantObjectId, StringComparison.OrdinalIgnoreCase))
            {
                assistant.AssistantUserId = assistantObjectId.Trim().ToLowerInvariant();
                await _db.SaveChangesAsync();
            }
            TempData["Success"] = $"Assistant invite sent to {assistant.Email}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send assistant invite for {Email}", assistant.Email);
            TempData["Warning"] = "Assistant created but invite email failed. Use Resend Invite.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendInvite(Guid id)
    {
        var agentOid = GetAgentOidOrThrow();
        var assistant = await _db.AgentAssistants.FirstOrDefaultAsync(a => a.Id == id && a.ParentAgentUserId == agentOid);
        if (assistant == null) return NotFound();

        var inviterName = GetInviterName();
        var assistantObjectId = await _provisioning.SendAssistantInviteEmailAsync(assistant.Email, assistant.FirstName, inviterName);
        if (!string.IsNullOrWhiteSpace(assistantObjectId)
            && !string.Equals(assistant.AssistantUserId, assistantObjectId, StringComparison.OrdinalIgnoreCase))
        {
            assistant.AssistantUserId = assistantObjectId.Trim().ToLowerInvariant();
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = $"Invite resent to {assistant.Email}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var agentOid = GetAgentOidOrThrow();
        var assistant = await _db.AgentAssistants.FirstOrDefaultAsync(a => a.Id == id && a.ParentAgentUserId == agentOid);
        if (assistant == null) return NotFound();

        assistant.IsActive = !assistant.IsActive;
        await _db.SaveChangesAsync();
        TempData["Success"] = assistant.IsActive ? "Assistant enabled." : "Assistant disabled.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid id)
    {
        var agentOid = GetAgentOidOrThrow();
        var assistant = await _db.AgentAssistants.FirstOrDefaultAsync(a => a.Id == id && a.ParentAgentUserId == agentOid);
        if (assistant == null) return NotFound();

        try
        {
            if (!string.IsNullOrWhiteSpace(assistant.AssistantUserId))
            {
                await _provisioning.DeleteTenantUserAsync(assistant.AssistantUserId);
            }
            else
            {
                await _provisioning.DeleteTenantUserByEmailAsync(assistant.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant removal blocked because Entra deletion failed. AssistantId={AssistantId} Email={Email}", assistant.Id, assistant.Email);
            TempData["Warning"] = "Could not remove assistant from Azure/Entra. No local deletion was performed.";
            return RedirectToAction(nameof(Index));
        }

        _db.AgentAssistants.Remove(assistant);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Assistant removed from Azure/Entra and Agent Portal.";
        return RedirectToAction(nameof(Index));
    }
}
