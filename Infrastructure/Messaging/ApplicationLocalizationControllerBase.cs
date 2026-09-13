using Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace Infrastructure.Messaging;

[Authorize]
[Route("localization")]
public abstract class ApplicationLocalizationControllerBase(
    IMessagingActorContextResolver actors,
    IApplicationLocalizationService localization) : Controller
{
    [HttpGet("catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Catalog(CancellationToken cancellationToken)
    {
        var actor = await actors.ResolveAsync(HttpContext, cancellationToken);
        if (actor is null) return Forbid();
        return Ok(await localization.GetCatalogAsync(
            new MessagingActor(actor.Value.UserId, actor.Value.ParticipantType), cancellationToken));
    }
}
