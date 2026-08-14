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

    [NonAction]
    public Task<IActionResult> Index(
        string? language,
        string? pair,
        CancellationToken cancellationToken) =>
        Index(language, pair, null, cancellationToken);

    [HttpGet]
    [Route("founder/legend-connect")]
    public async Task<IActionResult> Index(
        [FromQuery] string? language,
        [FromQuery] string? pair,
        [FromQuery(Name = "account")] string? accountSearch,
        CancellationToken cancellationToken)
    {
        try
        {
            ViewData["Title"] = "Legend Connect";
            return View(await _service.GetDashboardAsync(User, language, pair, cancellationToken, accountSearch));
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

    [HttpGet]
    [Route("founder/legend-connect/capacity")]
    public async Task<IActionResult> GetProviderCapacity(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetProviderCapacityAsync(User, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Azure capacity projection failed to load.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet]
    [Route("founder/legend-connect/metrics")]
    public async Task<IActionResult> GetLiveMetrics(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetLiveMetricsAsync(User, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect live metric projection failed to load.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet]
    [Route("founder/legend-connect/metric-details")]
    public async Task<IActionResult> GetMetricDetails(
        [FromQuery(Name = "metric")] string? metricKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetMetricDetailAsync(User, metricKey, cancellationToken));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect metric detail projection failed for {MetricKey}.", metricKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
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
            if (input.FixTargetTranslation)
            {
                var verifiedTargets = await _service.SubmitVerifiedTargetsAsync(User, input, cancellationToken);
                TempData[verifiedTargets.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] =
                    verifiedTargets.Message ?? "The verified target rows require Founder review.";
                return RedirectToAction(nameof(Index), new
                {
                    language = verifiedTargets.SourceLanguageCode,
                    pair = verifiedTargets.PairKey
                });
            }

            var result = await _service.SubmitAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] =
                result.Message ?? (result.Succeeded
                    ? result.TargetLanguageCode is null
                        ? "Approved source seed was saved. The existing autonomous planner will queue eligible missing coverage."
                        : "Approved knowledge was saved."
                    : "The approved knowledge could not be saved.");
            return RedirectToAction(nameof(Index), new { language = result.SourceLanguageCode, pair = result.PairKey });
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder knowledge submission failed.");
            return FounderFailureRedirect();
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
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/quality/approve")]
    public async Task<IActionResult> ApproveProviderObservation(
        [FromForm] FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.ApproveProviderObservationAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder provider-observation approval failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/quality/reject")]
    public async Task<IActionResult> RejectProviderObservation(
        [FromForm] FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.RejectProviderObservationAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder provider-observation rejection failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/quality/unresolved")]
    public async Task<IActionResult> LeaveProviderObservationUnresolved(
        [FromForm] FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.LeaveProviderObservationUnresolvedAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder provider-observation review deferral failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/curriculum")]
    public async Task<IActionResult> SubmitCurriculum(
        [FromForm] FounderLegendConnectCurriculumInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.SubmitCurriculumAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] =
                result.Message ?? (result.Succeeded
                    ? "Founder curriculum was saved. Existing Azure expansion will add enabled target-language evidence."
                    : "The curriculum family could not be saved.");
            return RedirectToAction(nameof(Index), new { language = "en" });
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder curriculum submission failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/entitlement")]
    public async Task<IActionResult> UpdateEntitlement(
        [FromForm] FounderLegendConnectEntitlementInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.UpdateEntitlementAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index), new { account = input.ReturnAccountSearch });
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect Founder entitlement update failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/runtime-policy")]
    public async Task<IActionResult> UpdateRuntimePolicy(
        [FromForm] FounderLegendConnectRuntimePolicyInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.UpdateRuntimePolicyAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect runtime policy update failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/composition-mode")]
    public async Task<IActionResult> SetCompositionMode(
        [FromForm] FounderLegendConnectCompositionModeInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.SetCompositionModeAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect production composition mode update failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/activate")]
    public async Task<IActionResult> ActivateAutonomousAcquisition(
        [FromForm] FounderLegendConnectActivationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.ActivateAutonomousAcquisitionAsync(User, input, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect autonomous activation failed.");
            return FounderFailureRedirect();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-connect/pause")]
    public async Task<IActionResult> PauseAutonomousAcquisition(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.PauseAutonomousAcquisitionAsync(User, cancellationToken);
            TempData[result.Succeeded ? "LegendConnectSuccess" : "LegendConnectError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legend Connect autonomous pause failed.");
            return FounderFailureRedirect();
        }
    }

    private RedirectToActionResult FounderFailureRedirect()
    {
        var reference = HttpContext.TraceIdentifier;
        TempData["LegendConnectError"] = string.IsNullOrWhiteSpace(reference)
            ? "Legend Connect could not complete that command. No success was recorded; please try again."
            : $"Legend Connect could not complete that command. No success was recorded. Reference: {reference}";
        return RedirectToAction(nameof(Index));
    }
}
