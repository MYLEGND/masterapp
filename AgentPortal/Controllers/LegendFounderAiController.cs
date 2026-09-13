using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public sealed class LegendFounderAiController : Controller
{
    private readonly LegendFounderAiConversationService _conversation;
    private readonly LegendFounderAiProgressBroker _progress;
    private readonly ILogger<LegendFounderAiController> _logger;

    public LegendFounderAiController(
        LegendFounderAiConversationService conversation,
        LegendFounderAiProgressBroker progress,
        ILogger<LegendFounderAiController> logger)
    {
        _conversation = conversation;
        _progress = progress;
        _logger = logger;
    }

    private LegendFounderAiHttpTransport Transport => new(HttpContext, _conversation, _progress, _logger);

    [HttpGet]
    [Route("founder/legend-ai/progress/{operationId:guid}")]
    public Task Progress(Guid operationId, CancellationToken cancellationToken) =>
        Transport.ProgressAsync(operationId, cancellationToken);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-ai/chat")]
    public Task<IActionResult> Chat([FromBody] LegendFounderAiChatRequest request, CancellationToken cancellationToken) =>
        Transport.ChatAsync(request, cancellationToken);

    internal static void RecordWorkObservation(
        IDictionary<string, LegendFounderAiProgressEvent> observations,
        LegendFounderAiProgressEvent update) => LegendFounderAiHttpTransport.RecordWorkObservation(observations, update);

    private static int MapStatus(LegendFounderAiChatResponse result) => LegendFounderAiHttpTransport.MapStatus(result);
}
