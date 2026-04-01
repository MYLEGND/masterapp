using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public class AgencyCommandController : Controller
{
    private readonly AgencyCommandService _service;
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgencyCommandController> _logger;

    public AgencyCommandController(AgencyCommandService service, MasterAppDbContext db, ILogger<AgencyCommandController> logger)
    {
        _service = service;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    [Route("agency-command")]
    public async Task<IActionResult> Index()
    {
        try
        {
            ViewData["Title"] = "Agency Command";
            var vm = await _service.GetDashboardAsync(User);
            return View(vm);
        }
        catch (ForbidResultException)
        {
            // Defense-in-depth: service layer enforces founder identity.
            return Forbid();
        }
    }

    [HttpGet]
    [Route("agency-command/agent/{agentId}")]
    public async Task<IActionResult> Detail(string agentId)
    {
        try
        {
            var vm = await _service.GetAgentDetailAsync(User, agentId);
            if (vm == null) return NotFound();

            ViewData["Title"] = $"Agency Command | {vm.Agent.FullName}";
            return View(vm);
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Agency Command detail for {AgentId}", agentId);
            return StatusCode(500);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("agency-command/set-order")]
    public async Task<IActionResult> SetOrder(string agentId, int? order)
    {
        try
        {
            FounderGuard.EnsureFounderOrThrow(User);
            var key = (agentId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key)) return BadRequest("agentId required");

            var profiles = await _db.AgentProfiles
                .OrderBy(a => a.DisplayOrder ?? int.MaxValue)
                .ThenBy(a => a.FullName ?? a.AgentUpn)
                .ToListAsync();

            var profile = profiles.FirstOrDefault(a => a.AgentUserId.ToLower() == key.ToLower());
            if (profile == null) return NotFound();

            // Remove target from current ordering list so we can re-insert cleanly.
            profiles.Remove(profile);

            if (!order.HasValue || order.Value <= 0)
            {
                // Null means "unsorted" — place after all ordered agents.
                profile.DisplayOrder = null;
                for (var i = 0; i < profiles.Count; i++)
                    profiles[i].DisplayOrder = i + 1;
            }
            else
            {
                var desired = Math.Max(1, order.Value);
                var insertIndex = Math.Min(desired - 1, profiles.Count);
                profiles.Insert(insertIndex, profile);

                // Re-sequence all visible cards so positions are unique and gapless.
                for (var i = 0; i < profiles.Count; i++)
                    profiles[i].DisplayOrder = i + 1;
            }

            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["OrderSaved"] = true;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting display order for {AgentId}", agentId);
            return StatusCode(500);
        }
    }
}
