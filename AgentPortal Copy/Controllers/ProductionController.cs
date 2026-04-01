
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[ValidateAntiForgeryToken]
public class ProductionController : Controller
{
    private readonly ProductionService _production;
    private readonly EffectiveAgentContext _agentContext;

    public ProductionController(ProductionService production, EffectiveAgentContext agentContext)
    {
        _production = production;
        _agentContext = agentContext;
    }

    [HttpPost]
    [Route("production/reset/all-leads")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetAllLeads()
    {
        var agent = GetEffectiveAgent();
        // Remove all production records for this agent's leads
        await _production.DeleteAllForAgentAsync(agent, ProductionSide.Lead);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok();
        return Redirect(Url.Action("Index", "Leads")!);
    }

    [HttpGet]
    [IgnoreAntiforgeryToken]
    [Route("production/history/lead")]
    public async Task<IActionResult> LeadHistory(string leadId)
    {
        try
        {
            var agent = GetEffectiveAgent();
            if (string.IsNullOrWhiteSpace(leadId))
                return BadRequest(new { error = "Missing leadId" });
            var history = await _production.GetHistoryAsync(agent, ProductionSide.Lead, leadId, null);
            return Json(history.Select(p => new { id = p.Id, amount = p.Amount, personalAmount = p.PersonalAmount, status = p.Status.ToString(), notes = p.Notes, updated = p.UpdatedUtc }));
        }
        catch (Exception ex)
        {
            // Log the error (optionally inject ILogger<ProductionController> for real logging)
            return StatusCode(500, new { error = "Server error in LeadHistory", detail = ex.Message });
        }
    }

    [HttpGet]
    [IgnoreAntiforgeryToken]
    [Route("production/history/client")]
    public async Task<IActionResult> ClientHistory(string clientUserId)
    {
        var agent = GetEffectiveAgent();
        var history = await _production.GetHistoryAsync(agent, ProductionSide.Client, null, clientUserId);
        return Json(history.Select(p => new { id = p.Id, amount = p.Amount, personalAmount = p.PersonalAmount, status = p.Status.ToString(), notes = p.Notes, updated = p.UpdatedUtc }));
    }

    [HttpGet]
    [IgnoreAntiforgeryToken]
    [Route("production/summary/leads")]
    public async Task<IActionResult> LeadSummary()
    {
        var agent = GetEffectiveAgent();
        var totals = await _production.GetAgentTotalsAsync(agent, ProductionSide.Lead);
        return Json(new
        {
            submitted = totals.Submitted,
            issued = totals.Issued,
            paid = totals.Paid,
            personal = totals.Personal,
            countSubmitted = totals.CountSubmitted,
            countIssued = totals.CountIssued,
            countPaid = totals.CountPaid,
            countPersonal = totals.CountPersonal
        });
    }

    [HttpGet]
    [IgnoreAntiforgeryToken]
    [Route("production/summary/clients")]
    public async Task<IActionResult> ClientSummary()
    {
        var agent = GetEffectiveAgent();
        var totals = await _production.GetAgentTotalsAsync(agent, ProductionSide.Client);
        return Json(new
        {
            submitted = totals.Submitted,
            issued = totals.Issued,
            paid = totals.Paid,
            personal = totals.Personal,
            countSubmitted = totals.CountSubmitted,
            countIssued = totals.CountIssued,
            countPaid = totals.CountPaid,
            countPersonal = totals.CountPersonal
        });
    }

    [HttpPost]
    [Route("production/update")]
    public async Task<IActionResult> Update(Guid id, decimal amount, decimal personalAmount, ProductionStatus status, string? notes, string? returnUrl = null)
    {
        var agent = GetEffectiveAgent();
        await _production.UpdateAsync(User?.Identity?.Name ?? agent, agent, id, status, amount, personalAmount, notes);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ok = true });
        return Redirect(string.IsNullOrWhiteSpace(returnUrl) ? Url.Action("Index", "Leads")! : returnUrl);
    }

    private string GetEffectiveAgent() =>
        _agentContext.EffectiveAgentOid
        ?? (User?.Identity?.Name ?? string.Empty);

    [HttpPost]
    [Route("production/add/lead")]
    public async Task<IActionResult> AddLead(string leadId, decimal amount, decimal personalAmount, ProductionStatus status, string? notes, string? returnUrl = null)
    {
        var agent = GetEffectiveAgent();
        await _production.UpsertAsync(User?.Identity?.Name ?? agent, agent, ProductionSide.Lead, status, amount, personalAmount, leadId, null, notes);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ok = true });
        return Redirect(string.IsNullOrWhiteSpace(returnUrl) ? Url.Action("Index", "Leads")! : returnUrl);
    }

    [HttpPost]
    [Route("production/add/client")]
    public async Task<IActionResult> AddClient(string clientUserId, decimal amount, decimal personalAmount, ProductionStatus status, string? notes, string? returnUrl = null)
    {
        var agent = GetEffectiveAgent();
        await _production.UpsertAsync(User?.Identity?.Name ?? agent, agent, ProductionSide.Client, status, amount, personalAmount, null, clientUserId, notes);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ok = true });
        return Redirect(string.IsNullOrWhiteSpace(returnUrl) ? Url.Action("Index", "Clients")! : returnUrl);
    }

    [HttpPost]
    [Route("production/delete")]
    public async Task<IActionResult> Delete(Guid id, string? returnUrl = null)
    {
        var agent = GetEffectiveAgent();
        await _production.DeleteAsync(User?.Identity?.Name ?? agent, agent, id);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok();
        return Redirect(string.IsNullOrWhiteSpace(returnUrl) ? Url.Action("Index", "Leads")! : returnUrl);
    }

    [HttpPost]
    [Route("production/reset/lead")]
    public async Task<IActionResult> ResetLead(string leadId)
    {
        var agent = GetEffectiveAgent();
        await _production.DeleteForContactAsync(User?.Identity?.Name ?? agent, agent, ProductionSide.Lead, leadId, null);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok();
        return Redirect(Url.Action("Index", "Leads")!);
    }

    [HttpPost]
    [Route("production/reset/client")]
    public async Task<IActionResult> ResetClient(string clientUserId)
    {
        var agent = GetEffectiveAgent();
        await _production.DeleteForContactAsync(User?.Identity?.Name ?? agent, agent, ProductionSide.Client, null, clientUserId);
        if (Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return Ok();
        return Redirect(Url.Action("Index", "Clients")!);
    }
}
