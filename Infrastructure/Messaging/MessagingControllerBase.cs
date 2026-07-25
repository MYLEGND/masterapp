using Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace Infrastructure.Messaging;

[Authorize]
public abstract class MessagingControllerBase : Controller
{
    private readonly IMessagingService _messagingService;
    private readonly IMessageAttachmentStorage _attachmentStorage;
    private readonly IMessagingRealtimePublisher _realtimePublisher;
    private readonly IMessagingProfileImageResolver _profileImageResolver;
    private readonly IMessagingContactKeyProtector _contactKeys;
    private readonly IMessagingActorContextResolver _actorContextResolver;

    protected MessagingControllerBase(
        IMessagingService messagingService,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher,
        IMessagingProfileImageResolver profileImageResolver,
        IMessagingContactKeyProtector contactKeys,
        IMessagingActorContextResolver actorContextResolver)
    {
        _messagingService = messagingService;
        _attachmentStorage = attachmentStorage;
        _realtimePublisher = realtimePublisher;
        _profileImageResolver = profileImageResolver;
        _contactKeys = contactKeys;
        _actorContextResolver = actorContextResolver;
    }

    private async Task<MessagingActor?> ResolveMessagingActorAsync(CancellationToken cancellationToken)
    {
        var actor = await _actorContextResolver.ResolveAsync(HttpContext, cancellationToken);
        return actor is null ? null : new MessagingActor(actor.Value.UserId, actor.Value.ParticipantType);
    }

    [HttpGet("/Messaging")]
    public async Task<IActionResult> Index(string? search, bool includeClosed = false)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.ListConversationsAsync(
            actor,
            new MessagingConversationListQuery(search, includeClosed),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        return View();
    }

    [HttpGet("/Messaging/Conversations")]
    public async Task<IActionResult> List(string? search, bool includeClosed = false)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.ListConversationsAsync(
            actor,
            new MessagingConversationListQuery(search, includeClosed),
            HttpContext.RequestAborted);
        return result.Succeeded ? Ok(result) : Failure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("/Messaging/Recipients")]
    public async Task<IActionResult> Recipients(string? search)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.ListRecipientsAsync(actor, search, HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        var protectedRecipients = result.Recipients
            .Select(recipient => recipient with { ContactKey = _contactKeys.Protect(actor, recipient) })
            .ToList();
        return Ok(result with { Recipients = protectedRecipients });
    }

    [HttpGet("/Messaging/Participants/{userId}/Avatar")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> ParticipantAvatar(string userId, string participantType)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var participant = await _messagingService.GetAuthorizedParticipantAsync(
            actor,
            userId,
            participantType,
            HttpContext.RequestAborted);
        if (!participant.Succeeded || participant.Recipient is null)
            return Failure(participant.ErrorCode, participant.ErrorMessage);

        var identities = await _profileImageResolver.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(participant.Recipient.UserId, participant.Recipient.ParticipantType)],
            HttpContext.RequestAborted);
        if (!identities.TryGetValue((participant.Recipient.UserId, participant.Recipient.ParticipantType), out var identity))
            return NotFound();

        var image = await _profileImageResolver.ResolveAsync(identity, HttpContext.RequestAborted);
        return image is null
            ? NotFound()
            : PhysicalFile(image.PhysicalPath, image.ContentType);
    }

    [HttpGet("/Messaging/Conversations/{conversationId:guid}")]
    public async Task<IActionResult> Conversation(Guid conversationId)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.GetConversationAsync(actor, conversationId, HttpContext.RequestAborted);
        return result.Succeeded ? Ok(result) : Failure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("/Messaging/Conversations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromBody] StartMessagingConversationRequest request)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        if (!_contactKeys.TryUnprotect(actor, request.ContactKey, out var participant))
            return Failure("MESSAGING_RECIPIENT_NOT_FOUND", "The requested participant is not available.");

        var result = await _messagingService.StartConversationAsync(
            new StartMessagingConversationCommand(
                actor,
                participant.UserId,
                participant.ParticipantType,
                request.Subject,
                request.Body,
                request.ClientMessageId),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        var conversation = result.Conversation!;
        var initialMessageId = conversation.Messages.LastOrDefault()?.Id;
        await PublishConversationEventAsync(
            conversation,
            initialMessageId.HasValue ? "messageReceived" : "conversationUpdated",
            initialMessageId,
            HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("/Messaging/Conversations/{conversationId:guid}/Messages")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(Guid conversationId, [FromBody] SendMessagingMessageRequest request)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.SendMessageAsync(
            new SendMessagingMessageCommand(actor, conversationId, request.Body, request.ClientMessageId),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        await PublishConversationEventAsync(
            actor,
            conversationId,
            "messageReceived",
            result.Message!.Id,
            HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("/Messaging/Conversations/{conversationId:guid}/Read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid conversationId)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.MarkConversationReadAsync(
            new MessagingConversationActionCommand(actor, conversationId),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        await PublishConversationEventAsync(actor, conversationId, "conversationUpdated", null, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("/Messaging/Conversations/{conversationId:guid}/Muted")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMuted(Guid conversationId, [FromBody] SetMessagingConversationMutedRequest request)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.SetConversationMutedAsync(
            new SetMessagingConversationMutedCommand(actor, conversationId, request.IsMuted),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        await PublishConversationEventAsync(actor, conversationId, "conversationUpdated", null, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("/Messaging/Conversations/{conversationId:guid}/Closed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetClosed(Guid conversationId, [FromBody] SetMessagingConversationClosedRequest request)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.SetConversationClosedAsync(
            new SetMessagingConversationClosedCommand(actor, conversationId, request.IsClosed),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        await PublishConversationEventAsync(actor, conversationId, "conversationUpdated", null, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("/Messaging/Messages/{messageId:guid}/Attachments")]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachment(Guid messageId, IFormFile? file)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();
        if (file is null)
            return BadRequest(new { errorCode = "MESSAGING_ATTACHMENT_REQUIRED", errorMessage = "Choose an attachment to upload." });

        var attachmentId = Guid.NewGuid();
        await using var content = file.OpenReadStream();
        var stored = await _attachmentStorage.StoreAsync(
            attachmentId,
            file.FileName,
            file.Length,
            content,
            HttpContext.RequestAborted);
        if (!stored.Succeeded)
            return Failure(stored.ErrorCode, stored.ErrorMessage);

        var attachment = stored.Attachment!;
        var result = await _messagingService.AddPendingAttachmentAsync(
            new AddPendingMessagingAttachmentCommand(
                actor,
                messageId,
                attachmentId,
                attachment.OriginalFileName,
                attachment.StoredFileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.StoragePath),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            await _attachmentStorage.DeleteAsync(attachment.StoragePath, HttpContext.RequestAborted);
            return Failure(result.ErrorCode, result.ErrorMessage);
        }

        await PublishConversationEventAsync(
            actor,
            result.ConversationId!.Value,
            "conversationUpdated",
            messageId,
            HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpGet("/Messaging/Attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DownloadAttachment(Guid attachmentId)
    {
        var actor = await ResolveMessagingActorAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Forbid();

        var result = await _messagingService.GetAttachmentForDownloadAsync(
            new MessagingAttachmentDownloadCommand(actor, attachmentId),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
            return Failure(result.ErrorCode, result.ErrorMessage);

        var attachment = result.Attachment!;
        var content = await _attachmentStorage.OpenReadAsync(attachment.StoragePath, HttpContext.RequestAborted);
        if (content is null)
            return NotFound(new { errorCode = "MESSAGING_ATTACHMENT_CONTENT_NOT_FOUND", errorMessage = "The attachment content was not found." });

        return File(content, attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true);
    }

    private async Task PublishConversationEventAsync(
        MessagingActor actor,
        Guid conversationId,
        string eventType,
        Guid? messageId,
        CancellationToken cancellationToken)
    {
        var conversation = await _messagingService.GetConversationAsync(actor, conversationId, cancellationToken);
        if (conversation.Succeeded && conversation.Conversation is not null)
            await PublishConversationEventAsync(conversation.Conversation, eventType, messageId, cancellationToken);
    }

    private Task PublishConversationEventAsync(
        MessagingConversationDetail conversation,
        string eventType,
        Guid? messageId,
        CancellationToken cancellationToken)
    {
        return _realtimePublisher.PublishAsync(
            new MessagingRealtimeEvent(
                eventType,
                conversation.Id,
                messageId,
                DateTime.UtcNow,
                conversation.Participants
                    .Select(x => new MessagingRealtimeRecipient(x.UserId, x.ParticipantType))
                    .ToArray()),
            cancellationToken);
    }

    private IActionResult Failure(string? errorCode, string? errorMessage)
    {
        var payload = new { errorCode, errorMessage };
        if (string.Equals(errorCode, "MESSAGING_ACTOR_INVALID", StringComparison.Ordinal) ||
            errorCode?.EndsWith("FORBIDDEN", StringComparison.Ordinal) == true ||
            string.Equals(errorCode, "MESSAGING_RECIPIENT_FORBIDDEN", StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, payload);
        }

        if (errorCode?.EndsWith("NOT_FOUND", StringComparison.Ordinal) == true)
            return NotFound(payload);
        if (errorCode?.Contains("CONFLICT", StringComparison.Ordinal) == true ||
            errorCode?.Contains("CLOSED", StringComparison.Ordinal) == true ||
            errorCode?.Contains("TRANSITION", StringComparison.Ordinal) == true)
        {
            return Conflict(payload);
        }

        return BadRequest(payload);
    }
}
