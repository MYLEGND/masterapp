using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileHomeController : MobileApiControllerBase
{
    private readonly IMobileHomeService _home;

    public MobileHomeController(IMobileActorResolver actorResolver, IMobileHomeService home)
        : base(actorResolver)
    {
        _home = home;
    }

    [HttpGet("home")]
    public async Task<IActionResult> Home(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _home.GetHomeAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Home is not null
            ? Ok(result.Home)
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_home_unavailable",
                result.ErrorMessage ?? "Your mobile home is not available.");
    }

    [HttpGet("financial")]
    public async Task<IActionResult> Financial(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _home.GetFinancialAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Snapshot is not null
            ? Ok(result.Snapshot)
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_financial_unavailable",
                result.ErrorMessage ?? "Financial intelligence is not available.");
    }
}
