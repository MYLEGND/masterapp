using Microsoft.AspNetCore.Mvc;
using ProtectWebsite.Services.MetaSignal;

namespace Protect_Website.Controllers;

[Route("analytics")]
public sealed class AnalyticsController : Controller
{
    private readonly IMetaSignalIntelligenceService _metaSignalIntelligence;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(
        IMetaSignalIntelligenceService metaSignalIntelligence,
        ILogger<AnalyticsController> logger)
    {
        _metaSignalIntelligence = metaSignalIntelligence;
        _logger = logger;
    }

    [HttpPost("meta-signal")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> MetaSignal([FromBody] MetaSignalIngestRequest? request, CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest(new { accepted = false, error = "Invalid meta signal payload." });

        try
        {
            var result = await _metaSignalIntelligence.IngestAsync(request, HttpContext, cancellationToken);
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meta signal ingest failed for event={EventName}", request.EventName);
            return Json(new MetaSignalProcessResult
            {
                Accepted = false,
                Skipped = true,
                EventName = request.EventName ?? string.Empty,
                EventId = request.EventId ?? string.Empty,
                MetaServerStatus = "error",
                MetaServerNote = "server_exception"
            });
        }
    }
}
