using AgentPortal.Security;
using AgentPortal.Services;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/founder/legend-ai")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileFounderAiController : MobileApiControllerBase
{
    private readonly LegendFounderAiConversationService _conversation;
    private readonly LegendFounderAiProgressBroker _progress;
    private readonly ILogger<MobileFounderAiController> _logger;

    public MobileFounderAiController(
        IMobileActorResolver actorResolver,
        LegendFounderAiConversationService conversation,
        LegendFounderAiProgressBroker progress,
        ILogger<MobileFounderAiController> logger) : base(actorResolver)
    {
        _conversation = conversation;
        _progress = progress;
        _logger = logger;
    }

    [HttpGet("access")]
    public async Task<IActionResult> Access(CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return Ok(new MobileFounderAiAccessResponse(Available: true));
    }

    private LegendFounderAiHttpTransport Transport => new(HttpContext, _conversation, _progress, _logger);

    [HttpGet("progress/{operationId:guid}")]
    public async Task Progress(Guid operationId, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) { Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) { Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        await Transport.ProgressAsync(operationId, cancellationToken);
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken cancellationToken, int take = 50, int skip = 0)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.ListConversationsAsync(take, skip, cancellationToken);
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<IActionResult> Conversation(Guid conversationId, CancellationToken cancellationToken,
        DateTime? beforeUtc = null, Guid? beforeMessageId = null, int take = 60)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.GetConversationAsync(conversationId, beforeUtc, beforeMessageId, take, cancellationToken);
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] LegendFounderAiChatRequest request, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.ChatAsync(request, cancellationToken, legacyMobileJson: true);
    }
}

public sealed record MobileFounderAiAccessResponse(bool Available);
