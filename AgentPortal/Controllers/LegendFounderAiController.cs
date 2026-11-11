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

    [HttpGet("founder/legend-ai/conversations")]
    public Task<IActionResult> Conversations(CancellationToken cancellationToken, int take = 50, int skip = 0, string? assistantWorkspaceKey = null) =>
        Transport.ListConversationsAsync(take, skip, cancellationToken, assistantWorkspaceKey);

    [HttpGet("founder/legend-ai/conversations/{conversationId:guid}")]
    public Task<IActionResult> Conversation(Guid conversationId, CancellationToken cancellationToken,
        DateTime? beforeUtc = null, Guid? beforeMessageId = null, int take = 60, string? assistantWorkspaceKey = null) =>
        Transport.GetConversationAsync(conversationId, beforeUtc, beforeMessageId, take, cancellationToken, assistantWorkspaceKey);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-ai/chat")]
    public Task<IActionResult> Chat([FromBody] LegendFounderAiChatRequest request, CancellationToken cancellationToken) =>
        Transport.ChatAsync(request, cancellationToken);

    [HttpGet("founder/legend-ai/business/workspaces")]
    public async Task<IActionResult> BusinessWorkspaces(CancellationToken ct)
    {
        try { return Ok(await _conversation.ListBusinessWorkspacesAsync(User, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict(new { error = "Workspace selection is unavailable." }); }
    }

    [HttpPost("founder/legend-ai/business/dictation")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_600_000)]
    public async Task<IActionResult> BusinessDictation([FromBody] BusinessAssistantDictationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid || request is null || string.IsNullOrWhiteSpace(request.Workspace)) return BadRequest();
        try { return Ok(new { transcript = await _conversation.TranscribeBusinessAsync(User, request, ct) }); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(new { error = "Record 0.1–60 seconds of microphone audio in a selected workspace." }); }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { return StatusCode(503, new { error = "Dictation could not complete. Check the selected workspace's dictation configuration and funding. No message was sent; no automatic retry was made." }); }
    }

    [HttpGet("founder/legend-ai/business/media")]
    public async Task<IActionResult> BusinessMedia(string workspace, CancellationToken ct)
    {
        try { return Ok(await _conversation.ListBusinessMediaAsync(User, workspace, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(); }
    }

    [HttpGet("founder/legend-ai/business/media/{id:guid}")]
    public async Task<IActionResult> BusinessMediaFile(Guid id, string workspace, CancellationToken ct, bool download = false)
    {
        try
        {
            var media = await _conversation.OpenBusinessMediaAsync(User, workspace, id, ct);
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (media is null) return NotFound();
            if (media.Value.Asset.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                await media.Value.Content.DisposeAsync();
                return NotFound();
            }
            return download ? File(media.Value.Content, media.Value.Asset.ContentType, id +
                (media.Value.Asset.ContentType == "image/png" ? ".png" : media.Value.Asset.ContentType == "image/jpeg" ? ".jpg" : ".webp"))
                : File(media.Value.Content, media.Value.Asset.ContentType);
        }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(); }
    }

    [HttpGet("founder/legend-ai/business/configuration")]
    public async Task<IActionResult> BusinessConfiguration(string workspace, string provider, CancellationToken ct)
    {
        try { return Ok(await _conversation.GetBusinessConfigurationAsync(User, workspace, provider, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(new { error = "Select an authorized workspace and supported provider." }); }
    }

    [HttpPost("founder/legend-ai/business/configuration")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBusinessConfiguration([FromBody] BusinessAssistantSettingsRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid || request.Configuration is null) return BadRequest();
        try { return Ok(await _conversation.SaveBusinessConfigurationAsync(User, request.Workspace,
            request.Configuration, request.ExpectedRevision, request.Credential, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(new { error = "A complete bounded provider and funding configuration is required." }); }
        catch (InvalidOperationException) { return Conflict(new { error = "Configuration changed or is unavailable. Reload before saving." }); }
    }

    [HttpPost("founder/legend-ai/business/model-access")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessModelAccess([FromBody] BusinessAssistantModelAccessRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid || request is null || string.IsNullOrWhiteSpace(request.Workspace)) return BadRequest();
        try { return Ok(await _conversation.InspectBusinessModelAccessAsync(User, request.Workspace, ct)); }
        catch (ForbidResultException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        { return StatusCode(503, new { error = "Provider model access could not be verified. No inference was requested." }); }
    }

    [HttpGet("founder/legend-ai/actions/{proposalId:guid}")]
    public async Task<IActionResult> ReviewAction(Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await _conversation.GetActionProposalAsync(User, proposalId, cancellationToken);
        if (proposal is null) return NotFound();
        if (!Guid.TryParse(proposal.ConversationId, out var conversationId)) return NotFound();
        var history = await _conversation.GetConversationPageAsync(User, conversationId,
            new Domain.Messaging.MessagingConversationMessagePageQuery(Take: 1, IncludeGroupImage: false), cancellationToken);
        if (!history.Succeeded || history.Conversation is null) return Conflict();
        ViewData["ProposalId"] = proposal.ProposalId;
        ViewData["Revision"] = proposal.Revision;
        ViewData["ReviewDigest"] = proposal.ReviewDigest;
        ViewData["Arguments"] = proposal.CanonicalArgumentsJson;
        ViewData["Binding"] = proposal.ReviewBindingJson;
        ViewData["Tool"] = proposal.ToolName;
        ViewData["State"] = proposal.State;
        ViewData["ExpiresUtc"] = proposal.ReviewExpiresUtc;
        ViewData["CanApprove"] = proposal.State == "Proposed" && proposal.ReviewExpiresUtc > DateTime.UtcNow;
        ViewData["ExpectedLastMessageId"] = history.Conversation.Messages.LastOrDefault()?.Id;
        ViewData["OperationId"] = Guid.NewGuid();
        return View("ReviewAction");
    }

    [HttpPost("founder/legend-ai/actions/{proposalId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAction(Guid proposalId, string expectedRevision, string reviewDigest,
        Guid? expectedLastMessageId, Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(expectedRevision) ||
            string.IsNullOrWhiteSpace(reviewDigest) || reviewDigest.Length != 64)
            return BadRequest();
        var result = await _conversation.ApproveActionProposalAsync(User, proposalId, expectedRevision,
            reviewDigest, expectedLastMessageId, operationId, cancellationToken);
        ViewData["Receipt"] = result.Message ?? result.Error;
        ViewData["Succeeded"] = result.Succeeded;
        Response.StatusCode = result.Succeeded ? 200 : 409;
        return View("ActionReceipt");
    }

    private static int MapStatus(LegendFounderAiChatResponse result) => LegendFounderAiHttpTransport.MapStatus(result);
}

public sealed record BusinessAssistantSettingsRequest(string Workspace,
    Infrastructure.Analytics.BusinessAssistantConfiguration? Configuration, Guid ExpectedRevision, string? Credential);
public sealed record BusinessAssistantModelAccessRequest(string Workspace);
