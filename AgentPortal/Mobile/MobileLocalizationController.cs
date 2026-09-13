using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Authenticated application-copy projection. The target language is never a
/// client parameter; it is resolved from the current actor's canonical account
/// preference by IApplicationLocalizationService.
/// </summary>
[ApiController]
[Route("api/v1/mobile/localization")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileLocalizationController : MobileApiControllerBase
{
    private readonly IApplicationLocalizationService _localization;

    public MobileLocalizationController(
        IMobileActorResolver actorResolver,
        IApplicationLocalizationService localization)
        : base(actorResolver)
    {
        _localization = localization;
    }

    [HttpGet("catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Catalog(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        return Ok(await _localization.GetCatalogAsync(
            resolved.Actor!.Actor,
            cancellationToken));
    }
}
