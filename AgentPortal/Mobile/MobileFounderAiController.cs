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
        var resolved = await ResolveAssistantActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return Ok(new MobileFounderAiAccessResponse(Available: true));
    }

    private LegendFounderAiHttpTransport Transport => new(HttpContext, _conversation, _progress, _logger);

    private async Task<MobileActorRequestResolution> ResolveAssistantActorAsync(CancellationToken ct)
    {
        var resolved = await base.ResolveActorAsync(ct);
        if (resolved.Error is null && resolved.Actor is not null &&
            !string.Equals(resolved.Actor.Actor.ParticipantType, Domain.Messaging.MessagingParticipantTypes.Agent, StringComparison.OrdinalIgnoreCase))
            return resolved with { Actor = null, Error = Forbid() };
        return resolved;
    }


    [HttpGet("business/workspaces")]
    public async Task<IActionResult> BusinessWorkspaces(CancellationToken ct)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveAssistantActorAsync(ct);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        try { return Ok(await _conversation.ListBusinessWorkspacesAsync(User, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict(new { error = "Workspace selection is unavailable." }); }
    }

    [HttpGet("business/configuration")]
    public async Task<IActionResult> BusinessConfiguration(string workspace, string provider, CancellationToken ct)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveAssistantActorAsync(ct);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        try { return Ok(await _conversation.GetBusinessConfigurationAsync(User, workspace, provider, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(); }
    }

    [HttpGet("progress/{operationId:guid}")]
    public async Task Progress(Guid operationId, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) { Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var resolved = await ResolveAssistantActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) { Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        await Transport.ProgressAsync(operationId, cancellationToken);
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken cancellationToken, int take = 50, int skip = 0, string? assistantWorkspaceKey = null)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveAssistantActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.ListConversationsAsync(take, skip, cancellationToken, assistantWorkspaceKey);
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<IActionResult> Conversation(Guid conversationId, CancellationToken cancellationToken,
        DateTime? beforeUtc = null, Guid? beforeMessageId = null, int take = 60, string? assistantWorkspaceKey = null)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveAssistantActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.GetConversationAsync(conversationId, beforeUtc, beforeMessageId, take, cancellationToken, assistantWorkspaceKey);
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] LegendFounderAiChatRequest request, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveAssistantActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return await Transport.ChatAsync(request, cancellationToken, legacyMobileJson: true);
    }
}

public sealed record MobileFounderAiAccessResponse(bool Available);
