using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public sealed class LegendConnectController : Controller
{
    private readonly FounderLegendConnectService _service;
    private readonly ILogger<LegendConnectController> _logger;

    public LegendConnectController(
        FounderLegendConnectService service,
        ILogger<LegendConnectController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    [Route("founder/legend-connect")]
    public async Task<IActionResult> Index(
        [FromQuery] string? language,
        [FromQuery] string? pair,
        CancellationToken cancellationToken)
    {
        try
        {
            ViewData["Title"] = "Legend Connect";
            return View(await _service.GetDashboardAsync(User, language, pair, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder dashboard failed to load.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/knowledge")]
    public async Task<IActionResult> SubmitKnowledge(
        [FromForm] FounderLegendConnectKnowledgeInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.SubmitAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] =
                result.Message ?? (result.Succeeded ? "Approved knowledge was saved." : "The approved knowledge could not be saved.");
            return RedirectToAction(nameof(Index), new { language = result.SourceLanguageCode, pair = result.PairKey });
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder knowledge submission failed.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/correction")]
    public async Task<IActionResult> CorrectKnowledge(
        [FromForm] FounderLegendConnectCorrectionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.CorrectAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] =
                result.Message ?? (result.Succeeded ? "The prior alignment was superseded with an auditable correction." : "The correction could not be saved.");
            return RedirectToAction(nameof(Index), new { language = result.SourceLanguageCode, pair = result.PairKey });
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder correction failed.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
