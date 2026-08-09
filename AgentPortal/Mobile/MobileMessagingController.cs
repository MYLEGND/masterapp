using Domain.Entities;
using Domain.Messaging;
using AgentPortal.Security;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly IControlledResourceAccessService _controlledResources;

    public MobileMessagingController(
        IMobileActorResolver actorResolver,
        IMessagingService messaging,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher,
        IMessagingProfileImageResolver profiles,
        IControlledResourceAccessService controlledResources)
        : base(actorResolver)
    {
        _messaging = messaging;
        _attachmentStorage = attachmentStorage;
        _realtimePublisher = realtimePublisher;
        _profiles = profiles;
        _controlledResources = controlledResources;
    }

    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        var resolution = await ResolveActorAsync(
            cancellationToken,
            allowSelectionRequired: true,
            allowLifecycleRestricted: true);
        if (resolution.Error is not null)
            return resolution.Error;

        return Ok(new MobileSessionResponse(
            true,
            resolution.Actor is null ? null : await ToActorDtoAsync(resolution.Actor, cancellationToken),
            resolution.PermittedActors.Select(actor => actor.Actor.ParticipantType).ToArray(),
            resolution.RequiresParticipantSelection,
            await CapabilitiesAsync(resolution.Actor, cancellationToken),
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
            CorrelationId(),
            await CapabilitiesAsync(resolution.SelectedActor, cancellationToken)));
    }

    [HttpGet("messaging/conversations")]
    public async Task<IActionResult> ListConversations(
        CancellationToken cancellationToken,
        [FromQuery] int? take = null,
        [FromQuery] int? skip = null)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListConversationsAsync(
            resolved.Actor!.Actor,
            // A group picture belongs to the conversation, not to any member
            // profile. Include that authoritative media in the inbox so an
            // owner-selected picture cannot disappear outside the thread.
            new MessagingConversationListQuery(
                Take: Math.Clamp(take ?? 24, 1, 50),
                IncludeGroupImages: true,
                Skip: Math.Max(skip ?? 0, 0)),
            cancellationToken);
        if (!result.Succeeded)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var participants = result.Conversations.Select(conversation => conversation.Counterparty);
        var identities = await ResolveParticipantIdentitiesAsync(participants, cancellationToken);
        var avatars = await ResolveParticipantAvatarsAsync(identities.Values, cancellationToken);
        var response = new List<MobileConversationSummaryDto>();
        foreach (var conversation in result.Conversations)
        {
            response.Add(new MobileConversationSummaryDto(
                conversation.Id,
                conversation.ConversationType,
                conversation.Subject ?? identities.GetDisplayName(conversation.Counterparty) ?? "Conversation",
                ToParticipantDto(
                    conversation.Counterparty,
                    identities,
                    AvatarFor(conversation.Counterparty, identities, avatars)),
                conversation.LastMessagePreview,
                conversation.LastMessageUtc,
                conversation.UnreadCount,
                conversation.IsClosed,
                conversation.Purpose,
                // Group artwork is separate conversation-owned media. Member
                // avatars above come from one batch against the same typed
                // profile authority used by the rest of the mobile app.
                MobileAvatarProjection.FromGroupImage(
                    conversation.Id,
                    conversation.GroupImage),
                conversation.IsPinned,
                conversation.IsMuted));
        }

        return Ok(response);
    }

    [HttpGet("messaging/conversations/{conversationId:guid}/image")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> ConversationImage(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var image = await _messaging.GetConversationImageAsync(
            resolved.Actor!.Actor,
            conversationId,
            cancellationToken);

        return image is null
            ? NotFound()
            : File(
                image.Content,
                image.ContentType);
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
            identities.TryGetValue(
                MessagingParticipantIdentityKey.Create(
                    recipient.UserId,
                    recipient.ParticipantType),
                out var identity);
            response.Add((new MobileMessagingRecipientDto(
                new MobileLogicalIdentityDto(recipient.UserId, recipient.ParticipantType),
                identity?.ProfileId.ToString("D") ?? string.Empty,
                identity?.DisplayName ?? recipient.DisplayName,
                recipient.Email,
                recipient.RelationshipLabel,
                recipient.ExistingConversationId,
                identity is null ? null : await ToAvatarDtoAsync(identity, cancellationToken))) with
            {
                RoleLabel = identity?.RoleLabel,
                IsVerified = identity?.IsVerified ?? false
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

    [HttpPost("messaging/groups")]
    public async Task<IActionResult> CreateGroup(
        [FromBody] MobileCreateGroupRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var members = request?.Participants?
            .Select(member => new MessagingParticipantReference(
                member.UserId ?? string.Empty,
                member.ParticipantType ?? string.Empty))
            .ToArray() ?? Array.Empty<MessagingParticipantReference>();
        if (!TryToGroupImage(request?.GroupImage, out var groupImage))
            return Error(StatusCodes.Status400BadRequest, "mobile_group_image_invalid", "Choose a supported group image.");

        var result = await _messaging.CreateGroupAsync(
            new CreateMessagingGroupCommand(
                resolved.Actor!.Actor,
                members,
                request?.Subject ?? string.Empty,
                request?.InitialMessageBody,
                GroupImage: groupImage,
                Meeting: ToGroupMeetingSetup(request?.Meeting)),
            cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken));
    }

    [HttpPost("messaging/verification-requests")]
    public async Task<IActionResult> StartVerificationRequest(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.StartVerificationRequestAsync(
            resolved.Actor!.Actor,
            cancellationToken);
        if (!result.Succeeded || result.Request is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(new MobileVerificationRequestDto(
            result.Request.Id,
            result.Request.Status,
            result.Request.RequestedUtc,
            result.Request.ResourceType));
    }

    [HttpPost("messaging/controlled-resources/{resourceType}/requests")]
    public async Task<IActionResult> StartControlledResourceRequest(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.StartControlledResourceRequestAsync(
            new StartControlledResourceRequestCommand(resolved.Actor!.Actor, resourceType),
            cancellationToken);
        if (!result.Succeeded || result.Request is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(new MobileVerificationRequestDto(
            result.Request.Id,
            result.Request.Status,
            result.Request.RequestedUtc,
            result.Request.ResourceType));
    }

    [HttpPost("messaging/verification-requests/{requestId:guid}/resolution")]
    public async Task<IActionResult> ResolveVerificationRequest(
        Guid requestId,
        [FromBody] MobileVerificationResolutionRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ResolveVerificationReviewRequestAsync(
            new ResolveVerificationReviewRequestCommand(
                resolved.Actor!.Actor,
                requestId,
                request?.Approve == true,
                request?.Note),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("messaging/controlled-resource-requests/{requestId:guid}/resolution")]
    public async Task<IActionResult> ResolveControlledResourceRequest(
        Guid requestId,
        [FromBody] MobileVerificationResolutionRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ResolveControlledResourceRequestAsync(
            new ResolveControlledResourceRequestCommand(
                resolved.Actor!.Actor,
                requestId,
                request?.Approve == true,
                request?.Note),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("messaging/controlled-resources/languages")]
    public async Task<IActionResult> CommunicationLanguages(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListCommunicationLanguagesAsync(
            resolved.Actor!.Actor,
            cancellationToken);
        return result.Succeeded
            ? Ok(result.Languages.Select(language => new MobileCommunicationLanguageDto(
                language.Code,
                language.DisplayName)))
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("messaging/activity")]
    public async Task<IActionResult> Activity([FromQuery] int? take, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListActivityNotificationsAsync(
            resolved.Actor!.Actor,
            take ?? 50,
            cancellationToken);
        return result.Succeeded
            ? Ok(result.Notifications.Select(notification => new MobileActivityNotificationDto(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Detail,
                notification.OccurredUtc,
                notification.ControlledResourceRequestId)))
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("messaging/controlled-resources/{resourceType}/recipients")]
    public async Task<IActionResult> ControlledResourceRecipients(
        string resourceType,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.ListControlledResourceRecipientsAsync(
            resolved.Actor!.Actor,
            resourceType,
            search,
            cancellationToken);
        if (!result.Succeeded)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var identities = await ResolveParticipantIdentitiesAsync(
            result.Recipients.Select(entry => new MessagingParticipantSummary(
                entry.Recipient.UserId,
                entry.Recipient.ParticipantType,
                entry.Recipient.DisplayName)),
            cancellationToken);
        var response = new List<MobileMessagingRecipientDto>();
        foreach (var entry in result.Recipients)
        {
            var recipient = entry.Recipient;
            identities.TryGetValue(
                MessagingParticipantIdentityKey.Create(
                    recipient.UserId,
                    recipient.ParticipantType),
                out var identity);
            response.Add((new MobileMessagingRecipientDto(
                new MobileLogicalIdentityDto(recipient.UserId, recipient.ParticipantType),
                identity?.ProfileId.ToString("D") ?? string.Empty,
                identity?.DisplayName ?? recipient.DisplayName,
                recipient.Email,
                recipient.RelationshipLabel,
                recipient.ExistingConversationId,
                identity is null ? null : await ToAvatarDtoAsync(identity, cancellationToken))) with
            {
                RoleLabel = identity?.RoleLabel,
                IsVerified = identity?.IsVerified ?? false,
                ResourceType = entry.ResourceType,
                ResourceAccessState = entry.AccessState
            });
        }

        return Ok(response);
    }

    [HttpPut("messaging/controlled-resources/{resourceType}/recipients")]
    public async Task<IActionResult> SetControlledResourceGrant(
        string resourceType,
        [FromBody] MobileControlledResourceGrantRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.SetControlledResourceGrantAsync(
            new SetControlledResourceGrantCommand(
                resolved.Actor!.Actor,
                resourceType,
                request?.TargetUserId ?? string.Empty,
                request?.TargetParticipantType ?? string.Empty,
                request?.IsGranted == true),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("messaging/groups/{conversationId:guid}")]
    public async Task<IActionResult> UpdateGroupProfile(
        Guid conversationId,
        [FromBody] MobileUpdateMessagingGroupRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (!TryToGroupImage(request?.GroupImage, out var groupImage))
            return Error(StatusCodes.Status400BadRequest, "mobile_group_image_invalid", "Choose a supported group image.");

        var result = await _messaging.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                resolved.Actor!.Actor,
                conversationId,
                request?.Subject ?? string.Empty,
                groupImage,
                ToGroupMeetingSetup(request?.Meeting)),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("messaging/conversations/{conversationId:guid}/participants")]
    public async Task<IActionResult> AddGroupParticipant(
        Guid conversationId,
        [FromBody] MobileGroupParticipantRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.AddGroupParticipantAsync(
            new AddMessagingGroupParticipantCommand(
                resolved.Actor!.Actor,
                conversationId,
                request?.UserId ?? string.Empty,
                request?.ParticipantType ?? string.Empty),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("messaging/groups/{conversationId:guid}/collaborators")]
    public async Task<IActionResult> SetGroupCollaborator(
        Guid conversationId,
        [FromBody] MobileGroupCollaboratorRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.SetGroupManagerAsync(
            new SetMessagingGroupManagerCommand(
                resolved.Actor!.Actor,
                conversationId,
                request?.UserId ?? string.Empty,
                request?.ParticipantType ?? string.Empty,
                request?.IsManager == true),
            cancellationToken);

        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("messaging/groups/{conversationId:guid}")]
    public async Task<IActionResult> DeleteGroup(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.DeleteGroupAsync(
            new DeleteMessagingGroupCommand(
                resolved.Actor!.Actor,
                conversationId),
            cancellationToken);

        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("messaging/groups/{conversationId:guid}/promotion")]
    public async Task<IActionResult> SetGroupPromotion(
        Guid conversationId,
        [FromBody] MobileGroupPromotionRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (request?.IsPromoted is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_group_promotion_invalid", "Choose whether to promote this group.");

        var result = await _messaging.SetGroupPromotionAsync(
            new SetMessagingGroupPromotionCommand(
                resolved.Actor!.Actor,
                conversationId,
                request.IsPromoted.Value),
            cancellationToken);
        return result.Succeeded && result.Conversation is not null
            ? Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken))
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("messaging/groups/{conversationId:guid}/join")]
    public async Task<IActionResult> JoinPromotedGroup(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.JoinPromotedGroupAsync(
            new JoinPromotedMessagingGroupCommand(resolved.Actor!.Actor, conversationId),
            cancellationToken);
        return result.Succeeded && result.Conversation is not null
            ? Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken))
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("messaging/conversations/{conversationId:guid}")]
    public async Task<IActionResult> Conversation(
        Guid conversationId,
        [FromQuery] DateTime? beforeUtc,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.GetConversationPageAsync(
            resolved.Actor!.Actor,
            conversationId,
            new MessagingConversationMessagePageQuery(
                beforeUtc,
                take ?? 60,
                IncludeGroupImage: true),
            cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        return Ok(await ToConversationDtoAsync(result.Conversation, resolved.Actor.Actor, cancellationToken));
    }

    [HttpGet("messaging/conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> Messages(
        Guid conversationId,
        [FromQuery] DateTime? beforeUtc,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.GetConversationPageAsync(
            resolved.Actor!.Actor,
            conversationId,
            new MessagingConversationMessagePageQuery(
                beforeUtc,
                take ?? 60,
                IncludeGroupImage: false),
            cancellationToken);
        if (!result.Succeeded || result.Conversation is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);

        var identities = await ResolveParticipantIdentitiesAsync(
            ConversationIdentities(result.Conversation),
            cancellationToken);
        var avatars = await ResolveParticipantAvatarsAsync(identities.Values, cancellationToken);
        var messages = new List<MobileMessageDto>();
        foreach (var message in result.Conversation.Messages)
            messages.Add(ToMessageDto(message, resolved.Actor.Actor, identities, avatars));
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
        var avatars = await ResolveParticipantAvatarsAsync(identities.Values, cancellationToken);
        return Ok(ToMessageDto(result.Message, resolved.Actor.Actor, identities, avatars));
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

    [HttpPut("messaging/conversations/{conversationId:guid}/pin")]
    public async Task<IActionResult> SetConversationPinned(
        Guid conversationId,
        [FromBody] MobileConversationPinnedRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (request?.IsPinned is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_messaging_invalid", "Choose whether to pin this conversation.");

        var result = await _messaging.SetConversationPinnedAsync(
            new SetMessagingConversationPinnedCommand(
                resolved.Actor!.Actor,
                conversationId,
                request.IsPinned.Value),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("messaging/conversations/{conversationId:guid}/mute")]
    public async Task<IActionResult> SetConversationMuted(
        Guid conversationId,
        [FromBody] MobileConversationMutedRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (request?.IsMuted is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_messaging_invalid", "Choose whether to mute this conversation.");

        var result = await _messaging.SetConversationMutedAsync(
            new SetMessagingConversationMutedCommand(
                resolved.Actor!.Actor,
                conversationId,
                request.IsMuted.Value),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("messaging/conversations/{conversationId:guid}")]
    public async Task<IActionResult> RemoveConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.RemoveConversationForActorAsync(
            new RemoveMessagingConversationCommand(resolved.Actor!.Actor, conversationId),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("messaging/conversations/{conversationId:guid}/messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.DeleteMessageAsync(
            new DeleteMessagingMessageCommand(resolved.Actor!.Actor, conversationId, messageId),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("messaging/conversations/{conversationId:guid}/call-options")]
    public async Task<IActionResult> ConversationCallOptions(Guid conversationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _messaging.GetConversationCallOptionsAsync(
            resolved.Actor!.Actor,
            conversationId,
            cancellationToken);
        return result.Succeeded && result.Options is not null
            ? Ok(new MobileConversationCallOptionsDto(
                result.Options.ConversationId,
                result.Options.DisplayName,
                result.Options.PhoneNumber,
                result.Options.FaceTimeAddress))
            : MessagingFailure(result.ErrorCode, result.ErrorMessage);
    }

    private async Task<MobileConversationDetailDto> ToConversationDtoAsync(
        MessagingConversationDetail conversation,
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        var identities = await ResolveParticipantIdentitiesAsync(
            ConversationIdentities(conversation),
            cancellationToken);
        var avatars = await ResolveParticipantAvatarsAsync(identities.Values, cancellationToken);

        var participants = new List<MobileParticipantDto>();
        foreach (var participant in conversation.Participants)
        {
            var participantDto = ToParticipantDto(
                participant,
                identities,
                AvatarFor(participant, identities, avatars));

            participants.Add(participantDto with
            {
                IsGroupManager = participant.IsGroupManager
            });
        }

        var messages = new List<MobileMessageDto>();
        foreach (var message in conversation.Messages)
            messages.Add(ToMessageDto(message, actor, identities, avatars));

        MobileGroupMeetingDto? meeting = null;
        if (conversation.Meeting is not null)
        {
            meeting = new MobileGroupMeetingDto(
                ToParticipantDto(
                    conversation.Meeting.Host,
                    identities,
                    AvatarFor(conversation.Meeting.Host, identities, avatars)),
                conversation.Meeting.LinkLabel,
                conversation.Meeting.LinkUrl,
                conversation.Meeting.Schedule is null
                    ? null
                    : new MobileGroupMeetingScheduleDto(
                        conversation.Meeting.Schedule.Frequency,
                        conversation.Meeting.Schedule.Weekdays ?? Array.Empty<string>(),
                        conversation.Meeting.Schedule.LocalTime,
                        conversation.Meeting.Schedule.TimeZoneId,
                        conversation.Meeting.Schedule.StartsUtc,
                        conversation.Meeting.Schedule.CustomDescription));
        }

        return new MobileConversationDetailDto(
            conversation.Id,
            conversation.ConversationType,
            conversation.Subject ?? "Conversation",
            participants,
            messages,
            conversation.IsMuted,
            conversation.IsClosed,
            conversation.CanManageMembers,
            conversation.Purpose,
            MobileAvatarProjection.FromGroupImage(
                conversation.Id,
                conversation.GroupImage)) with
        {
            CanManageCollaborators = conversation.CanManageCollaborators,
            CanDeleteGroup = conversation.CanDeleteGroup,
            IsPromoted = conversation.IsPromoted,
            PromotionStartedUtc = conversation.PromotionStartedUtc,
            PromotionEndedUtc = conversation.PromotionEndedUtc,
            CanManagePromotion = conversation.CanManagePromotion,
            Meeting = meeting,
            CanManageMeeting = conversation.CanManageMeeting,
            HasOlderMessages = conversation.HasOlderMessages
        };
    }

    private static IEnumerable<MessagingParticipantSummary> ConversationIdentities(
        MessagingConversationDetail conversation) =>
        conversation.Participants.Concat(
            conversation.Messages.Select(message => new MessagingParticipantSummary(
                message.SenderUserId,
                message.SenderType,
                string.Empty))).Concat(
        conversation.Messages
                .Where(message => message.Reply is not null)
                .Select(message => new MessagingParticipantSummary(
                    message.Reply!.SenderUserId,
                    message.Reply.SenderType,
                    string.Empty))).Concat(
            conversation.Meeting is null
                ? Array.Empty<MessagingParticipantSummary>()
                : [conversation.Meeting.Host]);

    private static MessagingGroupMeetingSetup? ToGroupMeetingSetup(
        MobileGroupMeetingRequest? request) =>
        request is null
            ? null
            : new MessagingGroupMeetingSetup(
                request.Host is null
                    ? null
                    : new MessagingParticipantReference(
                        request.Host.UserId ?? string.Empty,
                        request.Host.ParticipantType ?? string.Empty),
                request.LinkLabel,
                request.LinkUrl,
                request.Schedule is null
                    ? null
                    : new MessagingGroupMeetingSchedule(
                        request.Schedule.Frequency ?? string.Empty,
                        request.Schedule.Weekdays,
                        request.Schedule.LocalTime,
                        request.Schedule.TimeZoneId,
                        request.Schedule.StartsUtc,
                        request.Schedule.CustomDescription));

    private async Task<IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>> ResolveParticipantIdentitiesAsync(
        IEnumerable<MessagingParticipantSummary> participants,
        CancellationToken cancellationToken) =>
        await _profiles.ResolveIdentitiesAsync(
            participants.Select(participant => new MessagingParticipantReference(participant.UserId, participant.ParticipantType)),
            cancellationToken);

    private Task<IReadOnlyDictionary<MessagingProfileImageKey, MobileAvatarDto>> ResolveParticipantAvatarsAsync(
        IEnumerable<MessagingParticipantIdentity> identities,
        CancellationToken cancellationToken) =>
        MobileAvatarProjection.ResolveManyAsync(_profiles, identities, cancellationToken);

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

    private static MobileParticipantDto ToParticipantDto(
        MessagingParticipantSummary participant,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        MobileAvatarDto? avatar = null)
    {
        identities.TryGetValue(
            MessagingParticipantIdentityKey.Create(
                participant.UserId,
                participant.ParticipantType),
            out var identity);
        return (new MobileParticipantDto(
            new MobileLogicalIdentityDto(participant.UserId, participant.ParticipantType),
            identity?.ProfileId.ToString("D") ?? string.Empty,
            identity?.DisplayName ?? participant.DisplayName,
            avatar)) with
        {
            RoleLabel = identity?.RoleLabel,
            IsVerified = identity?.IsVerified ?? false
        };
    }

    private static MobileMessageDto ToMessageDto(
        MessagingMessageSummary message,
        MessagingActor actor,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        IReadOnlyDictionary<MessagingProfileImageKey, MobileAvatarDto> avatars) => new(
        message.Id,
        message.ConversationId,
        ToParticipantDto(
            new MessagingParticipantSummary(message.SenderUserId, message.SenderType, string.Empty),
            identities,
            AvatarFor(message.SenderUserId, message.SenderType, identities, avatars)),
        message.Body,
        message.SentUtc,
        message.Attachments.Select(ToAttachmentDto).ToArray(),
        string.Equals(message.SenderUserId, actor.UserId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(message.SenderType, actor.ParticipantType, StringComparison.Ordinal),
        message.IsDeleted,
        message.Reply is null
            ? null
            : new MobileReplyPreviewDto(
                message.Reply.Id,
                ToParticipantDto(
                    new MessagingParticipantSummary(
                        message.Reply.SenderUserId,
                        message.Reply.SenderType,
                        string.Empty),
                    identities,
                    AvatarFor(message.Reply.SenderUserId, message.Reply.SenderType, identities, avatars)),
                message.Reply.Body,
                message.Reply.IsDeleted),
        message.VerificationReview is null
            ? null
            : new MobileVerificationReviewDto(
                message.VerificationReview.Id,
                message.VerificationReview.RequesterUserId,
                message.VerificationReview.RequesterParticipantType,
                message.VerificationReview.Status,
                message.VerificationReview.RequestedUtc,
                message.VerificationReview.CanResolve,
                message.VerificationReview.ResourceType),
        message.Translation is null
            ? null
            : new MobileMessageTranslationDto(
                message.Translation.OriginalLanguage,
                message.Translation.TargetLanguage,
                message.Translation.Provider),
        message.OriginalBody);

    private static MobileAvatarDto? AvatarFor(
        MessagingParticipantSummary participant,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        IReadOnlyDictionary<MessagingProfileImageKey, MobileAvatarDto> avatars) =>
        AvatarFor(participant.UserId, participant.ParticipantType, identities, avatars);

    private static MobileAvatarDto? AvatarFor(
        string userId,
        string participantType,
        IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        IReadOnlyDictionary<MessagingProfileImageKey, MobileAvatarDto> avatars) =>
        identities.TryGetValue(
            MessagingParticipantIdentityKey.Create(userId, participantType),
            out var identity) &&
        avatars.TryGetValue(MessagingProfileImageKey.From(identity), out var avatar)
            ? avatar
            : null;

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

    private static bool TryToGroupImage(
        MobileGroupImageRequest? request,
        out MessagingGroupImage? groupImage)
    {
        groupImage = null;
        if (request is null)
            return true;
        if (string.IsNullOrWhiteSpace(request.ContentType) ||
            string.IsNullOrWhiteSpace(request.Base64Content))
            return false;
        try
        {
            groupImage = new MessagingGroupImage(
                Convert.FromBase64String(request.Base64Content),
                request.ContentType);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<MobileCapabilitiesDto> CapabilitiesAsync(
        MobileResolvedActor? actor,
        CancellationToken cancellationToken)
    {
        var isFounder = FounderGuard.IsFounder(User);
        var canManageScripture = isFounder || (actor is not null &&
            (await _controlledResources.GetAccessAsync(
                actor.Actor,
                ControlledResourceTypes.ScriptureManagement,
                cancellationToken)).State == ControlledResourceAccessStates.Granted);
        var canManageCommunity = isFounder || (actor is not null &&
            (await _controlledResources.GetAccessAsync(
                actor.Actor,
                ControlledResourceTypes.CommunityManagement,
                cancellationToken)).State == ControlledResourceAccessStates.Granted);
        return new MobileCapabilitiesDto(true, isFounder, canManageScripture, canManageCommunity);
    }

    private IActionResult MessagingFailure(string? errorCode, string? errorMessage)
    {
        var statusCode = string.Equals(errorCode, "MESSAGING_CONVERSATION_NOT_FOUND", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status403Forbidden;
        return Error(statusCode, "mobile_messaging_rejected", errorMessage ?? "This messaging action is not available.");
    }

}

public sealed record MobileLogicalIdentityDto(string UserId, string ParticipantType);

public sealed record MobileAvatarDto(string Kind, string ContentType, string ResourcePath);

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
    public string? RoleLabel { get; init; }
    public bool IsVerified { get; init; }
    public bool IsGroupManager { get; init; }
}

public sealed record MobileSessionResponse(
    bool Authenticated,
    MobileActorDto? Actor,
    IReadOnlyList<string> PermittedParticipantTypes,
    bool RequiresParticipantSelection,
    MobileCapabilitiesDto Capabilities,
    string CorrelationId);

public sealed record MobileCapabilitiesDto(
    bool Messaging,
    bool IsFounder = false,
    bool CanManageScripture = false,
    bool CanManageCommunity = false);

public sealed record MobileRoleSelectionResponse(
    MobileActorDto Actor,
    IReadOnlyList<string> PermittedParticipantTypes,
    string CorrelationId,
    MobileCapabilitiesDto? Capabilities = null);

public sealed record MobileSelectRoleRequest(string? ParticipantType);

public sealed record MobileConversationSummaryDto(
    Guid Id,
    string ConversationType,
    string Title,
    MobileParticipantDto Counterparty,
    string? LastMessagePreview,
    DateTime? LastMessageUtc,
    int UnreadCount,
    bool IsClosed,
    string? Purpose,
    MobileAvatarDto? GroupAvatar,
    bool IsPinned,
    bool IsMuted);

public sealed record MobileConversationDetailDto(
    Guid Id,
    string ConversationType,
    string Title,
    IReadOnlyList<MobileParticipantDto> Participants,
    IReadOnlyList<MobileMessageDto> Messages,
    bool IsMuted,
    bool IsClosed,
    bool CanManageMembers,
    string? Purpose,
    MobileAvatarDto? GroupAvatar)
{
    public bool CanManageCollaborators { get; init; }
    public bool CanDeleteGroup { get; init; }
    public bool IsPromoted { get; init; }
    public DateTime? PromotionStartedUtc { get; init; }
    public DateTime? PromotionEndedUtc { get; init; }
    public bool CanManagePromotion { get; init; }
    public MobileGroupMeetingDto? Meeting { get; init; }
    public bool CanManageMeeting { get; init; }
    public bool HasOlderMessages { get; init; }
}

public sealed record MobileGroupMeetingDto(
    MobileParticipantDto Host,
    string? LinkLabel,
    string? LinkUrl,
    MobileGroupMeetingScheduleDto? Schedule);

public sealed record MobileGroupMeetingScheduleDto(
    string Frequency,
    IReadOnlyList<string> Weekdays,
    string? LocalTime,
    string? TimeZoneId,
    DateTime? StartsUtc,
    string? CustomDescription);

public sealed record MobileMessageDto(
    Guid Id,
    Guid ConversationId,
    MobileParticipantDto Sender,
    string Body,
    DateTime SentUtc,
    IReadOnlyList<MobileMessageAttachmentDto> Attachments,
    bool IsMine,
    bool IsDeleted,
    MobileReplyPreviewDto? Reply = null,
    MobileVerificationReviewDto? VerificationReview = null,
    MobileMessageTranslationDto? Translation = null,
    string? OriginalBody = null);

public sealed record MobileMessageTranslationDto(
    string OriginalLanguage,
    string TargetLanguage,
    string Provider);

public sealed record MobileVerificationReviewDto(
    Guid Id,
    string RequesterUserId,
    string RequesterParticipantType,
    string Status,
    DateTime RequestedUtc,
    bool CanResolve,
    string ResourceType = ControlledResourceTypes.VerificationBadge);

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
public sealed record MobileConversationPinnedRequest(bool? IsPinned);
public sealed record MobileConversationMutedRequest(bool? IsMuted);
public sealed record MobileConversationCallOptionsDto(
    Guid ConversationId,
    string DisplayName,
    string? PhoneNumber,
    string? FaceTimeAddress);
public sealed record MobileStartConversationRequest(
    string? TargetUserId,
    string? TargetParticipantType,
    string? InitialMessageBody);

public sealed record MobileCreateGroupRequest(
    string? Subject,
    IReadOnlyList<MobileGroupParticipantRequest>? Participants,
    string? InitialMessageBody = null,
    MobileGroupImageRequest? GroupImage = null,
    MobileGroupMeetingRequest? Meeting = null);

public sealed record MobileUpdateMessagingGroupRequest(
    string? Subject,
    MobileGroupImageRequest? GroupImage = null,
    MobileGroupMeetingRequest? Meeting = null);

public sealed record MobileGroupPromotionRequest(bool? IsPromoted);

public sealed record MobileGroupImageRequest(
    string? ContentType,
    string? Base64Content);

public sealed record MobileVerificationResolutionRequest(bool? Approve, string? Note = null);

public sealed record MobileVerificationRequestDto(
    Guid Id,
    string Status,
    DateTime RequestedUtc,
    string ResourceType = ControlledResourceTypes.VerificationBadge);

public sealed record MobileActivityNotificationDto(
    Guid Id,
    string Kind,
    string Title,
    string Detail,
    DateTime OccurredUtc,
    Guid? ControlledResourceRequestId);

public sealed record MobileControlledResourceGrantRequest(
    string? TargetUserId,
    string? TargetParticipantType,
    bool? IsGranted);

public sealed record MobileCommunicationLanguageDto(string Code, string DisplayName);

public sealed record MobileGroupCollaboratorRequest(
    string UserId,
    string ParticipantType,
    bool IsManager);

public sealed record MobileGroupParticipantRequest(
    string? UserId,
    string? ParticipantType);

public sealed record MobileGroupMeetingRequest(
    MobileGroupParticipantRequest? Host = null,
    string? LinkLabel = null,
    string? LinkUrl = null,
    MobileGroupMeetingScheduleRequest? Schedule = null);

public sealed record MobileGroupMeetingScheduleRequest(
    string? Frequency,
    IReadOnlyList<string>? Weekdays = null,
    string? LocalTime = null,
    string? TimeZoneId = null,
    DateTime? StartsUtc = null,
    string? CustomDescription = null);

public sealed record MobileMessagingRecipientDto(
    MobileLogicalIdentityDto Identity,
    string ProfileId,
    string DisplayName,
    string? Email,
    string? RelationshipLabel,
    Guid? ExistingConversationId,
    MobileAvatarDto? Avatar)
{
    public string? RoleLabel { get; init; }
    public bool IsVerified { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceAccessState { get; init; }
}

internal static class MobileParticipantIdentityDictionaryExtensions
{
    public static string? GetDisplayName(
        this IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity> identities,
        MessagingParticipantSummary participant) =>
        identities.TryGetValue(
            MessagingParticipantIdentityKey.Create(
                participant.UserId,
                participant.ParticipantType),
            out var identity)
            ? identity.DisplayName
            : participant.DisplayName;
}
