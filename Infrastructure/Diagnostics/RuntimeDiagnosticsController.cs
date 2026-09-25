using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

[ApiController]
[Route("api/runtime-diagnostics")]
[AllowAnonymous] // Public observations require antiforgery, never account impersonation.
public sealed class RuntimeDiagnosticsController(RuntimeDiagnosticStore store, ILogger<RuntimeDiagnosticsController> logger) : ControllerBase
{
    [HttpGet("bootstrap")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Bootstrap([FromServices] IAntiforgery antiforgery,
        [FromServices] IOptions<RuntimeDiagnosticPublicWebsiteOptions> transport)
    {
        // Only a host explicitly configured for public website transport offers
        // bootstrap. The response has no diagnostic, account or application data.
        if (transport.Value.AllowedOrigins.Length == 0) return NotFound();
        if (!Request.IsHttps || Request.Headers.Origin.Count != 1 ||
            !transport.Value.AllowedOrigins.Contains(Request.Headers.Origin.ToString(), StringComparer.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden);
        var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        if (token is null || token.Length > 4096) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Ok(new { requestToken = token });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(24 * 1024)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Post([FromBody] RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
    {
        var publicOrigins = HttpContext.RequestServices?.GetService(typeof(IOptions<RuntimeDiagnosticPublicWebsiteOptions>))
            as IOptions<RuntimeDiagnosticPublicWebsiteOptions>;
        if (publicOrigins?.Value.AllowedOrigins.Length > 0 && Request.Headers.ContainsKey("Origin") &&
            (Request.Headers.Origin.Count != 1 || !publicOrigins.Value.AllowedOrigins.Contains(Request.Headers.Origin.ToString(), StringComparer.Ordinal)))
            return StatusCode(StatusCodes.Status403Forbidden);
        if (!RuntimeDiagnosticSanitizer.IsBounded(diagnosticEvent)) return BadRequest();
        try
        {
            var result = await store.RecordClientAsync(diagnosticEvent, User, cancellationToken);
            if (result == RuntimeDiagnosticAdmissionResult.RateLimited) return StatusCode(StatusCodes.Status429TooManyRequests);
            return Accepted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never recursively collect ingestion/storage failures or log payloads.
            logger.LogWarning("Runtime diagnostic persistence was unavailable.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
