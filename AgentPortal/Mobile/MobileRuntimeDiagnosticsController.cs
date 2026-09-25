using Infrastructure.Diagnostics;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Diagnostics;

namespace AgentPortal.Mobile;

// Transport adapter only: admission, classification and persistence remain shared.
[ApiController]
[Route("api/v1/mobile/runtime-diagnostics")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileRuntimeDiagnosticsController(
    IMobileActorResolver actors,
    RuntimeDiagnosticStore store) : MobileApiControllerBase(actors)
{
    [HttpPost]
    [RequestSizeLimit(24 * 1024)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Post([FromBody] RuntimeDiagnosticEvent observation, CancellationToken cancellationToken)
    {
        var actor = await ResolveActorAsync(cancellationToken);
        if (actor.Error is not null) return actor.Error;
        try
        {
            var result = await store.RecordClientAsync(observation, User, cancellationToken);
            return result == RuntimeDiagnosticAdmissionResult.RateLimited
                ? StatusCode(StatusCodes.Status429TooManyRequests) : Accepted();
        }
        catch (ArgumentException) { return BadRequest(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
