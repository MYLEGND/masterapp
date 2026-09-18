using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

[ApiController]
[Route("api/runtime-diagnostics")]
[AllowAnonymous] // Same-origin public-site observations require antiforgery, not account impersonation.
public sealed class RuntimeDiagnosticsController(RuntimeDiagnosticStore store, ILogger<RuntimeDiagnosticsController> logger) : ControllerBase
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(24 * 1024)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Post([FromBody] RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
    {
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
