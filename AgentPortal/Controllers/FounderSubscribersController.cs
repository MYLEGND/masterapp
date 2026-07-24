using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public sealed class FounderSubscribersController : Controller
{
    private readonly FounderSubscribersService _service;
    private readonly FounderImpersonationService _impersonation;
    private readonly ILogger<FounderSubscribersController> _logger;

    public FounderSubscribersController(
        FounderSubscribersService service,
        FounderImpersonationService impersonation,
        ILogger<FounderSubscribersController> logger)
    {
        _service = service;
        _impersonation = impersonation;
        _logger = logger;
    }

    [HttpGet]
    [Route("founder/subscribers")]
    public async Task<IActionResult> Index([FromQuery] FounderSubscribersQuery query, CancellationToken cancellationToken)
    {
        try
        {
            ViewData["Title"] = "Subscribers";
            return View(await _service.GetDashboardAsync(User, query, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Founder subscriber command center failed to load.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet]
    [Route("founder/subscribers/pricing-group")]
    public async Task<IActionResult> PricingGroup(
        int monthlyAmountCents,
        string currency,
        [FromQuery] FounderSubscribersQuery query,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _service.GetPricingGroupAsync(User, monthlyAmountCents, currency, query, page, cancellationToken);
            return model is null ? NotFound() : PartialView("_PricingGroupSubscribers", model);
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Founder subscriber pricing group failed to load. Amount={MonthlyAmountCents} Currency={Currency}", monthlyAmountCents, currency);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet]
    [Route("founder/subscribers/cancelled")]
    public async Task<IActionResult> Cancelled(int page = 1, CancellationToken cancellationToken = default)
    {
        try
        {
            return PartialView("_PricingGroupSubscribers", await _service.GetCancelledSubscribersAsync(User, page, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Founder cancelled subscriber details failed to load.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/subscribers/open-client-context")]
    public async Task<IActionResult> OpenClientContext(
        Guid clientProfileId,
        string agentUserId,
        string destination,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await _service.ResolveClientContextAsync(User, clientProfileId, agentUserId, cancellationToken);
            if (context is null)
                return NotFound();

            await _impersonation.StartAsync(HttpContext, User, context.AgentUserId, cancellationToken);

            var clientUserId = Uri.EscapeDataString(context.ClientUserId);
            var target = (destination ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "client" => $"/ClientWorkspace/Index?clientUserId={clientUserId}",
                "crm" => $"/Clients?clientUserId={clientUserId}",
                "timeline" => $"/Clients?clientUserId={clientUserId}#timeline",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(target))
                return BadRequest("A valid subscriber destination is required.");

            _logger.LogInformation(
                "Founder opened {Destination} for subscriber profile {ClientProfileId} in owner context {AgentUserId}",
                destination,
                clientProfileId,
                context.AgentUserId);

            return LocalRedirect(target);
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Founder subscriber quick action failed. Profile={ClientProfileId} Destination={Destination}", clientProfileId, destination);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
