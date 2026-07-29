using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileMessagingController : MobileApiControllerBase
{
    private readonly IMessagingService _messaging;
    private readonly IMessageAttachmentStorage _attachmentStorage;
    private readonly IMessagingRealtimePublisher _realtimePublisher;
    private readonly IMessagingProfileImageResolver _profiles;
    private readonly MasterAppDbContext _db;

    public MobileMessagingController(
        IMobileActorResolver actorResolver,
        IMessagingService messaging,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher,
        IMessagingProfileImageResolver profiles,
        MasterAppDbContext db)
        : base(actorResolver)
    {
        _messaging = messaging;
        _attachmentStorage = attachmentStorage;
        _realtimePublisher = realtimePublisher;
        _profiles = profiles;
        _db = db;
    }

    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        var resolution = await ResolveActorAsync(cancellationToken, allowSelectionRequired: true);
        if (resolution.Error is not null)
            return resolution.Error;

        return Ok(new MobileSessionResponse(
            true,
            resolution.Actor is null ? null : await ToActorDtoAsync(resolution.Actor, cancellationToken),
            resolution.PermittedActors.Select(actor => actor.Actor.ParticipantType).ToArray(),
            resolution.RequiresParticipantSelection,
            new MobileCapabilitiesDto(true),
            CorrelationId()));
    }

    [HttpPost("session/select-role")]
    public async Task<IActionResult> SelectRole(
        [FromBody] MobileSelectRoleRequest? request,
        CancellationToken cancellationToken)
    {
        var resolution = await ActorResolver.ResolveAsync(
            User,
            request?.ParticipantType,
            cancellationToken);
        if (!resolution.Succeeded || resolution.SelectedActor is null)
            return Error(StatusCodes.Status403Forbidden, resolution.ErrorCode ?? "mobile_role_forbidden", resolution.ErrorMessage ?? "The selected mobile role is not available.");

        return Ok(new MobileRoleSelectionResponse(
            await ToActorDtoAsync(resolution.SelectedActor, cancellationToken),
            resolution.PermittedActors.Select(actor => actor.Actor.ParticipantType).ToArray(),
            CorrelationId()));
    }

    [HttpGet("messaging/conversations")]
    public async Task<IActionResult> ListConversations(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListConversationsAsync(
            resolved.Actor!.Actor,
            new MessagingConversationListQuery(),
            cancellationToken);
        if (!result.Succeeded)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var participants = result.Conversations.Select(conversation => conversation.Counterparty);
        var identities = await ResolveParticipantIdentitiesAsync(participants, cancellationToken);
        var response = new List<MobileConversationSummaryDto>();
        foreach (var conversation in result.Conversations)
        {
            response.Add(new MobileConversationSummaryDto(
                conversation.Id,
                conversation.Subject ?? identities.GetDisplayName(conversation.Counterparty) ?? "Conversation",
                await ToParticipantDtoAsync(conversation.Counterparty, identities, cancellationToken),
                conversation.LastMessagePreview,
                conversation.LastMessageUtc,
                conversation.UnreadCount,
                conversation.IsClosed));
        }

        return Ok(response);
    }

    [HttpGet("messaging/recipients")]
    public async Task<IActionResult> Recipients(
        [FromQuery] string? search,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListRecipientsAsync(
            resolved.Actor!.Actor,
            search,
            scope,
            cancellationToken);
        if (!result.Succeeded)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var identities = await ResolveParticipantIdentitiesAsync(
            result.Recipients.Select(recipient => new MessagingParticipantSummary(
                recipient.UserId,
                recipient.ParticipantType,
                recipient.DisplayName)),
            cancellationToken);
        var response = new List<MobileMessagingRecipientDto>();
        foreach (var recipient in result.Recipients)
        {
            identities.TryGetValue((recipient.UserId, recipient.ParticipantType), out var identity);
            response.Add((new MobileMessagingRecipientDto(
                new MobileLogicalIdentityDto(recipient.UserId, recipient.ParticipantType),
                identity?.ProfileId.ToString("D") ?? string.Empty,
                identity?.DisplayName ?? recipient.DisplayName,
                recipient.Email,
                recipient.RelationshipLabel,
                recipient.ExistingConversationId,
                identity is null ? null : await ToAvatarDtoAsync(identity, cancellationToken))) with
            {
                Title = await ResolveCanonicalAgentTitleAsync(
                    identity,
                    cancellationToken)
            });
        }

        return Ok(response);
    }

    [HttpPost("messaging/conversations")]
    public async Task<IActionResult> StartConversation(
        [FromBody] MobileStartConversationRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.StartConversationAsync(
            new StartMessagingConversationCommand(
                resolved.Actor!.Actor,
                request?.TargetUserId ?? string.Empty,
                request?.TargetParticipantType ?? string.Empty,
                InitialMessageBody: request?.InitialMessageBody),
            cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken));
    }

    [HttpGet("messaging/conversations/{conversationId:guid}")]
    public async Task<IActionResult> Conversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.GetConversationAsync(resolved.Actor!.Actor, conversationId, cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken));
    }

    [HttpGet("messaging/conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid conversationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.GetConversationAsync(resolved.Actor!.Actor, conversationId, cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var identities = await ResolveParticipantIdentitiesAsync(result.Conversation.Participants, cancellationToken);
        var messages = new List<MobileMessageDto>();
        foreach (var message in result.Conversation.Messages)
            messages.Add(await ToMessageDtoAsync(message, resolved.Actor.Actor, identities, cancellationToken));
        return Ok(messages);
    }

    [HttpPost("messaging/conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> SendMessage(
        Guid conversationId,
        [FromBody] MobileSendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.SendMessageAsync(
            new SendMessagingMessageCommand(
                resolved.Actor!.Actor,
                conversationId,
                request?.Body ?? string.Empty,
                ReplyToMessageId: request?.ReplyToMessageId),
            cancellationToken);
        if (!result.Succeeded || result.Message is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var identities = await ResolveParticipantIdentitiesAsync(
            [new MessagingParticipantSummary(
                result.Message.SenderUserId,
                result.Message.SenderType,
                string.Empty)],
            cancellationToken);
        return Ok(await ToMessageDtoAsync(result.Message, resolved.Actor.Actor, identities, cancellationToken));
    }

    [HttpPost("messaging/conversations/{conversationId:guid}/messages/{messageId:guid}/attachments")]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachment(
        Guid conversationId,
        Guid messageId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (resolved.Actor is null)
            return Error(StatusCodes.Status403Forbidden, "mobile_actor_unavailable", "Messaging is not available for this user.");
        if (file is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_attachment_required", "Choose an attachment to upload.");

        var actor = resolved.Actor.Actor;

        var attachmentId = Guid.NewGuid();
        await using var content = file.OpenReadStream();
        var stored = await _attachmentStorage.StoreAsync(
            attachmentId,
            file.FileName,
            file.Length,
            content,
            cancellationToken);
        if (!stored.Succeeded || stored.Attachment is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_attachment_rejected", stored.ErrorMessage ?? "This attachment is not permitted.");

        var attachment = stored.Attachment;
        var result = await _messaging.AddPendingAttachmentAsync(
            new AddPendingMessagingAttachmentCommand(
                actor,
                messageId,
                attachmentId,
                attachment.OriginalFileName,
                attachment.StoredFileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.StoragePath),
            cancellationToken);
        if (!result.Succeeded || result.Attachment is null || result.ConversationId != conversationId)
        {
            await _attachmentStorage.DeleteAsync(attachment.StoragePath, cancellationToken);
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);
        }

        var conversation = await _messaging.GetConversationAsync(actor, conversationId, cancellationToken);
        if (conversation.Succeeded && conversation.Conversation is not null)
        {
            await _realtimePublisher.PublishAsync(
                new MessagingRealtimeEvent(
                    "conversationUpdated",
                    conversationId,
                    messageId,
                    DateTime.UtcNow,
                    conversation.Conversation.Participants
                        .Select(participant => new MessagingRealtimeRecipient(
                            participant.UserId,
                            participant.ParticipantType))
                        .ToArray()),
                cancellationToken);
        }

        return Ok(ToAttachmentDto(result.Attachment));
    }

    [HttpPost("messaging/conversations/{conversationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid conversationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.MarkConversationReadAsync(
            new MessagingConversationActionCommand(resolved.Actor!.Actor, conversationId),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    private async Task<MobileConversationDetailDto> ToConversationDtoAsync(
        MessagingConversationDetail conversation,
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        var identities = await ResolveParticipantIdentitiesAsync(conversation.Participants, cancellationToken);
        var participants = new List<MobileParticipantDto>();
        foreach (var participant in conversation.Participants)
            participants.Add(await ToParticipantDtoAsync(participant, identities, cancellationToken));

        var messages = new List<MobileMessageDto>();
        foreach (var message in conversation.Messages)
            messages.Add(await ToMessageDtoAsync(message, actor, identities, cancellationToken));

        return new MobileConversationDetailDto(
            conversation.Id,
            conversation.Subject ?? "Conversation",
            participants,
            messages,
            conversation.IsMuted,
            conversation.IsClosed);
    }

    private async Task<IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>> ResolveParticipantIdentitiesAsync(
        IEnumerable<MessagingParticipantSummary> participants,
        CancellationToken cancellationToken) =>
        await _profiles.ResolveIdentitiesAsync(
            participants.Select(participant => new MessagingParticipantReference(participant.UserId, participant.ParticipantType)),
            cancellationToken);

    private async Task<MobileActorDto> ToActorDtoAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken)
    {
        var identity = new MessagingParticipantIdentity(
            actor.Actor.UserId,
            actor.Actor.ParticipantType,
            actor.ProfileId,
            actor.DisplayName,
            null,
            string.Empty);
        return new MobileActorDto(
            new MobileLogicalIdentityDto(actor.Actor.UserId, actor.Actor.ParticipantType),
            actor.ProfileId.ToString("D"),
            actor.DisplayName,
            await ToAvatarDtoAsync(identity, cancellationToken));
    }

    private async Task<MobileParticipantDto> ToParticipantDtoAsync(
        MessagingParticipantSummary participant,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        CancellationToken cancellationToken)
    {
        identities.TryGetValue((participant.UserId, participant.ParticipantType), out var identity);
        return (new MobileParticipantDto(
            new MobileLogicalIdentityDto(participant.UserId, participant.ParticipantType),
            identity?.ProfileId.ToString("D") ?? string.Empty,
            identity?.DisplayName ?? participant.DisplayName,
            identity is null ? null : await ToAvatarDtoAsync(identity, cancellationToken))) with
        {
            Title = await ResolveCanonicalAgentTitleAsync(
                identity,
                cancellationToken)
        };
    }

    private async Task<MobileMessageDto> ToMessageDtoAsync(
        MessagingMessageSummary message,
        MessagingActor actor,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        CancellationToken cancellationToken) => new(
        message.Id,
        message.ConversationId,
        await ToParticipantDtoAsync(
            new MessagingParticipantSummary(message.SenderUserId, message.SenderType, string.Empty),
            identities,
            cancellationToken),
        message.Body,
        message.SentUtc,
        message.Attachments.Select(ToAttachmentDto).ToArray(),
        string.Equals(message.SenderUserId, actor.UserId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(message.SenderType, actor.ParticipantType, StringComparison.Ordinal),
        message.Reply is null
            ? null
            : new MobileReplyPreviewDto(
                message.Reply.Id,
                await ToParticipantDtoAsync(
                    new MessagingParticipantSummary(
                        message.Reply.SenderUserId,
                        message.Reply.SenderType,
                        string.Empty),
                    identities,
                    cancellationToken),
                message.Reply.Body,
                message.Reply.IsDeleted));

    private async Task<string?> ResolveCanonicalAgentTitleAsync(
        MessagingParticipantIdentity? identity,
        CancellationToken cancellationToken)
    {
        if (identity is null ||
            !string.Equals(
                identity.ParticipantType,
                MessagingParticipantTypes.Agent,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = await _db.AgentProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == identity.ProfileId)
            .Select(profile => profile.Title)
            .SingleOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(title)
            ? null
            : title.Trim();
    }

    private async Task<MobileAvatarDto?> ToAvatarDtoAsync(
        MessagingParticipantIdentity identity,
        CancellationToken cancellationToken) =>
        await MobileAvatarProjection.ResolveAsync(_profiles, identity, cancellationToken);

    private static MobileMessageAttachmentDto ToAttachmentDto(MessagingAttachmentSummary attachment) => new(
        attachment.Id,
        attachment.OriginalFileName,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.ScanStatus,
        attachment.CreatedUtc,
        attachment.CanDownload);

    private IActionResult MessagingFailure(string? errorCode, string? errorMessage)
    {
        var statusCode = string.Equals(errorCode, "MESSAGING_CONVERSATION_NOT_FOUND", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status403Forbidden;
        return Error(statusCode, "mobile_messaging_rejected", errorMessage ?? "This messaging action is not available.");
    }

}

public sealed record MobileLogicalIdentityDto(string UserId, string ParticipantType);

public sealed record MobileAvatarDto(string Kind, string ContentType, string Base64Content);

public sealed record MobileActorDto(
    MobileLogicalIdentityDto Identity,
    string ProfileId,
    string DisplayName,
    MobileAvatarDto? Avatar);

public sealed record MobileParticipantDto(
    MobileLogicalIdentityDto Identity,
    string ProfileId,
    string DisplayName,
    MobileAvatarDto? Avatar)
{
    public string? Title { get; init; }
}

public sealed record MobileSessionResponse(
    bool Authenticated,
    MobileActorDto? Actor,
    IReadOnlyList<string> PermittedParticipantTypes,
    bool RequiresParticipantSelection,
    MobileCapabilitiesDto Capabilities,
    string CorrelationId);

public sealed record MobileCapabilitiesDto(bool Messaging);

public sealed record MobileRoleSelectionResponse(
    MobileActorDto Actor,
    IReadOnlyList<string> PermittedParticipantTypes,
    string CorrelationId);

public sealed record MobileSelectRoleRequest(string? ParticipantType);

public sealed record MobileConversationSummaryDto(
    Guid Id,
    string Title,
    MobileParticipantDto Counterparty,
    string? LastMessagePreview,
    DateTime? LastMessageUtc,
    int UnreadCount,
    bool IsClosed);

public sealed record MobileConversationDetailDto(
    Guid Id,
    string Title,
    IReadOnlyList<MobileParticipantDto> Participants,
    IReadOnlyList<MobileMessageDto> Messages,
    bool IsMuted,
    bool IsClosed);

public sealed record MobileMessageDto(
    Guid Id,
    Guid ConversationId,
    MobileParticipantDto Sender,
    string Body,
    DateTime SentUtc,
    IReadOnlyList<MobileMessageAttachmentDto> Attachments,
    bool IsMine,
    MobileReplyPreviewDto? Reply = null);

public sealed record MobileReplyPreviewDto(
    Guid Id,
    MobileParticipantDto Sender,
    string Body,
    bool IsDeleted);

public sealed record MobileMessageAttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string ScanStatus,
    DateTime CreatedUtc,
    bool CanDownload);

public sealed record MobileSendMessageRequest(
    string? Body,
    Guid? ReplyToMessageId = null);
public sealed record MobileStartConversationRequest(
    string? TargetUserId,
    string? TargetParticipantType,
    string? InitialMessageBody);

public sealed record MobileMessagingRecipientDto(
    MobileLogicalIdentityDto Identity,
    string ProfileId,
    string DisplayName,
    string? Email,
    string? RelationshipLabel,
    Guid? ExistingConversationId,
    MobileAvatarDto? Avatar)
{
    public string? Title { get; init; }
}

internal static class MobileParticipantIdentityDictionaryExtensions
{
    public static string? GetDisplayName(
        this IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        MessagingParticipantSummary participant) =>
        identities.TryGetValue((participant.UserId, participant.ParticipantType), out var identity)
            ? identity.DisplayName
            : participant.DisplayName;
}
