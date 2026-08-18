using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(
    NoStore = true,
    Location = ResponseCacheLocation.None,
    Duration = 0)]
public sealed class LegendFounderAiController : Controller
{
    private readonly LegendFounderAiConversationService _conversation;
    private readonly ILogger<LegendFounderAiController> _logger;

    public LegendFounderAiController(
        LegendFounderAiConversationService conversation,
        ILogger<LegendFounderAiController> logger)
    {
        _conversation = conversation;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-ai/chat")]
    public async Task<IActionResult> Chat(
        [FromBody] LegendFounderAiChatRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result =
                await _conversation.ReplyAsync(
                    User,
                    request,
                    cancellationToken);

            if (!result.Succeeded)
            {
                var statusCode =
                    result.FailureKind switch
                    {
                        "validation" =>
                            StatusCodes.Status400BadRequest,

                        "configuration" =>
                            StatusCodes.Status503ServiceUnavailable,

                        "timeout" =>
                            StatusCodes.Status504GatewayTimeout,

                        "provider_http" or
                        "transport" or
                        "provider_json" =>
                            StatusCodes.Status502BadGateway,

                        _ =>
                            StatusCodes.Status502BadGateway
                    };

                return StatusCode(
                    statusCode,
                    result);
            }

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
                "LEGEND Founder AI conversation failed.");

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                LegendFounderAiChatResponse.Failure(
                    "LEGEND® Ai encountered an unexpected server error."));
        }
    }
}
