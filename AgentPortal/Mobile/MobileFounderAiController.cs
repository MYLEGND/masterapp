using AgentPortal.Security;
using AgentPortal.Services;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Founder-only native-mobile transport over the existing
/// LegendFounderAiConversationService.
///
/// This boundary owns no OpenAI provider implementation,
/// LEGEND knowledge, learning state, curriculum state,
/// translation state, model lifecycle, or durable AI authority.
/// </summary>
[ApiController]
[Route("api/v1/mobile/founder/legend-ai")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[FounderOnly]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileFounderAiController : MobileApiControllerBase
{
    private readonly LegendFounderAiConversationService _conversation;
    private readonly ILogger<MobileFounderAiController> _logger;

    public MobileFounderAiController(
        IMobileActorResolver actorResolver,
        LegendFounderAiConversationService conversation,
        ILogger<MobileFounderAiController> logger)
        : base(actorResolver)
    {
        _conversation = conversation;
        _logger = logger;
    }

    [HttpGet("access")]
    public async Task<IActionResult> Access(
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var resolved =
            await ResolveActorAsync(cancellationToken);

        if (resolved.Error is not null ||
            resolved.Actor is null)
        {
            return resolved.Error!;
        }

        return Ok(
            new MobileFounderAiAccessResponse(
                Available: true));
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(
        [FromBody] LegendFounderAiChatRequest request,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var resolved =
            await ResolveActorAsync(cancellationToken);

        if (resolved.Error is not null ||
            resolved.Actor is null)
        {
            return resolved.Error!;
        }

        try
        {
            var result =
                await _conversation.ReplyAsync(
                    User,
                    request,
                    cancellationToken);

            return Ok(result);
        }
        catch (ForbidResultException)
        {
            return Forbid();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "LEGEND Founder AI mobile conversation failed.");

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new MobileApiErrorResponse(
                    "legend_founder_ai_failed",
                    "LEGEND® Ai encountered an unexpected server error.",
                    CorrelationId(),
                    new Dictionary<string, string[]>()));
        }
    }
}

public sealed record MobileFounderAiAccessResponse(
    bool Available);
