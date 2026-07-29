using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed class MessagingService : IMessagingService
{
    private const int MaximumConversationSubjectLength = 240;
    private const int MaximumMessageBodyLength = 10_000;
    private const int MaximumClientMessageIdLength = 100;
    private const int MaximumAttachmentNameLength = 255;
    private const int MaximumAttachmentContentTypeLength = 150;
    private const int MaximumAttachmentStoragePathLength = 1_000;
    private const int MaximumAuditDetailLength = 1_000;

    private readonly MasterAppDbContext _db;
    private readonly ILogger<MessagingService> _logger;
    private readonly ICommunityTextModerationService _moderation;
    private readonly IMessagingProfileImageResolver _participantIdentities;

    public MessagingService(
        MasterAppDbContext db,
        ILogger<MessagingService> logger,
        ICommunityTextModerationService moderation,
        IMessagingProfileImageResolver participantIdentities)
    {
        _db = db;
        _logger = logger;
        _moderation = moderation;
        _participantIdentities = participantIdentities;
    }

    public async Task<MessagingConversationListResult> ListConversationsAsync(
        MessagingActor actor,
        MessagingConversationListQuery query,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationListResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var take = Math.Clamp(query.Take, 1, 100);
        var search = NormalizeOptional(query.Search);
        if (!Fits(search, MaximumConversationSubjectLength))
            return MessagingConversationListResult.Failure("MESSAGING_SEARCH_INVALID", "The conversation search text is too long.");

        var conversationsQuery = await AuthorizedConversationsQueryAsync(actor, cancellationToken);
        if (!query.IncludeClosed)
            conversationsQuery = conversationsQuery.Where(x => !x.IsClosed);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchPattern = $"%{search}%";
            conversationsQuery = conversationsQuery.Where(x =>
                (x.Subject != null && EF.Functions.Like(x.Subject, searchPattern)) ||
                x.Messages.Any(message => !message.IsDeleted && EF.Functions.Like(message.Body, searchPattern)));
        }

        var conversations = await conversationsQuery
            .OrderByDescending(x => x.LastMessageUtc ?? x.CreatedUtc)
            .Take(take)
            .Select(x => new ConversationRow(
                x.Id,
                x.ConversationType,
                x.Subject,
                x.LastMessageUtc,
                x.IsClosed))
            .ToListAsync(cancellationToken);

        if (conversations.Count == 0)
            return new MessagingConversationListResult(true, null, null, Array.Empty<MessagingConversationSummary>());

        var conversationIds = conversations.Select(x => x.Id).ToArray();
        var participants = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(x => conversationIds.Contains(x.ConversationId) && x.IsActive)
            .Select(x => new ParticipantRow(
                x.ConversationId,
                x.UserId,
                x.ParticipantType,
                x.LastReadUtc,
                x.IsMuted))
            .ToListAsync(cancellationToken);

        var messages = await _db.InternalMessages
            .AsNoTracking()
            .Where(x => conversationIds.Contains(x.ConversationId))
            .Select(x => new MessageListRow(
                x.Id,
                x.ConversationId,
                x.SenderUserId,
                x.SenderType,
                x.Body,
                x.SentUtc,
                x.IsDeleted))
            .ToListAsync(cancellationToken);

        var clientParticipantIds = participants
            .Where(x => x.ParticipantType == MessagingParticipantTypes.Client)
            .Select(x => x.UserId.ToLower())
            .Distinct()
            .ToArray();
        var activeClientMembershipIds = clientParticipantIds.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : (await ActiveClientMembershipUserIdsQuery()
                .Where(clientUserId => clientParticipantIds.Contains(clientUserId))
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var displayNames = await LoadDisplayNamesAsync(participants, cancellationToken);
        var actorUserId = NormalizeUserId(actor.UserId);
        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        var result = new List<MessagingConversationSummary>(conversations.Count);
        foreach (var conversation in conversations)
        {
            var conversationParticipants = participants
                .Where(x => x.ConversationId == conversation.Id)
                .ToList();
            var currentParticipant = conversationParticipants.FirstOrDefault(x =>
                IsSameParticipant(x.UserId, x.ParticipantType, actorUserId, actorParticipantType));
            if (currentParticipant is null)
                continue;

            var counterparty = conversationParticipants.FirstOrDefault(x =>
                !IsSameParticipant(x.UserId, x.ParticipantType, actorUserId, actorParticipantType));
            if (counterparty is null)
                continue;

            var conversationMessages = messages
                .Where(x => x.ConversationId == conversation.Id)
                .OrderByDescending(x => x.SentUtc)
                .ToList();
            var unreadCount = conversationMessages.Count(x =>
                !x.IsDeleted &&
                !IsSameParticipant(x.SenderUserId, x.SenderType, actorUserId, actorParticipantType) &&
                (!currentParticipant.LastReadUtc.HasValue || x.SentUtc > currentParticipant.LastReadUtc.Value));
            var latest = conversationMessages.FirstOrDefault(x => !x.IsDeleted);
            var isArchivedMembership =
                conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
                conversationParticipants
                    .Where(x => x.ParticipantType == MessagingParticipantTypes.Client)
                    .Any(x => !activeClientMembershipIds.Contains(x.UserId));

            result.Add(new MessagingConversationSummary(
                conversation.Id,
                conversation.ConversationType,
                conversation.Subject,
                conversation.LastMessageUtc,
                conversation.IsClosed,
                isArchivedMembership,
                unreadCount,
                ToParticipantSummary(counterparty, displayNames),
                latest is null ? null : Preview(latest.Body)));
        }

        return new MessagingConversationListResult(true, null, null, result);
    }

    public async Task<MessagingConversationResult> GetConversationAsync(
        MessagingActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .Where(x => x.Id == conversationId)
            .Select(x => new ConversationDetailRow(
                x.Id,
                x.ConversationType,
                x.Subject,
                x.CreatedUtc,
                x.LastMessageUtc,
                x.IsClosed))
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        var participants = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.IsActive)
            .Select(x => new ParticipantRow(x.ConversationId, x.UserId, x.ParticipantType, x.LastReadUtc, x.IsMuted))
            .ToListAsync(cancellationToken);
        var attachments = await _db.MessageAttachments
            .AsNoTracking()
            .Where(x => x.InternalMessage.ConversationId == conversationId)
            .Select(x => new AttachmentRow(
                x.Id,
                x.InternalMessageId,
                x.OriginalFileName,
                x.ContentType,
                x.SizeBytes,
                x.ScanStatus,
                x.CreatedUtc))
            .ToListAsync(cancellationToken);
        var messages = await _db.InternalMessages
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.SentUtc)
            .Select(x => new MessageDetailRow(
                x.Id,
                x.ConversationId,
                x.SenderUserId,
                x.SenderType,
                x.Body,
                x.SentUtc,
                x.EditedUtc,
                x.IsDeleted,
                x.ReplyToMessageId,
                x.ReplyToMessage == null
                    ? null
                    : new ReplyDetailRow(
                        x.ReplyToMessage.Id,
                        x.ReplyToMessage.SenderUserId,
                        x.ReplyToMessage.SenderType,
                        x.ReplyToMessage.Body,
                        x.ReplyToMessage.IsDeleted)))
            .ToListAsync(cancellationToken);
        var displayNames = await LoadDisplayNamesAsync(participants, cancellationToken);
        var currentParticipant = participants.FirstOrDefault(x =>
            IsSameParticipant(x.UserId, x.ParticipantType, actor.UserId, actor.ParticipantType));
        var isArchivedMembership =
            conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
            !await ConversationHasActiveClientMembershipAsync(conversation.Id, cancellationToken);

        var detail = new MessagingConversationDetail(
            conversation.Id,
            conversation.ConversationType,
            conversation.Subject,
            conversation.CreatedUtc,
            conversation.LastMessageUtc,
            conversation.IsClosed,
            isArchivedMembership,
            currentParticipant?.IsMuted == true,
            participants.Select(x => ToParticipantSummary(x, displayNames)).ToList(),
            messages.Select(message => ToMessageSummary(message, attachments)).ToList());

        return new MessagingConversationResult(true, null, null, detail);
    }

    public async Task<MessagingRecipientListResult> ListRecipientsAsync(
        MessagingActor actor,
        string? search = null,
        string? recipientScope = null,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingRecipientListResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        search = NormalizeOptional(search);
        if (!Fits(search, MaximumConversationSubjectLength))
            return MessagingRecipientListResult.Failure("MESSAGING_SEARCH_INVALID", "The recipient search text is too long.");

        if (!TryNormalizeRecipientScope(actor, recipientScope, out var normalizedScope))
            return MessagingRecipientListResult.Failure("MESSAGING_RECIPIENT_SCOPE_INVALID", "The recipient collection is not available for this user.");

        var results = await ListAuthorizedRecipientsAsync(actor, normalizedScope, cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            results = results.Where(x => MatchesContactSearch(x, search))
                .ToList();
        }

        return new MessagingRecipientListResult(true, null, null, results.Take(100).ToList());
    }

    public async Task<MessagingRecipientResult> GetAuthorizedParticipantAsync(
        MessagingActor actor,
        string userId,
        string participantType,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedParticipantType = NormalizeRequired(participantType);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingRecipientResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (string.IsNullOrWhiteSpace(normalizedUserId) || !IsParticipantType(normalizedParticipantType))
            return MessagingRecipientResult.Failure("MESSAGING_RECIPIENT_NOT_FOUND", "The requested participant is not available.");

        var participant = (await ListAuthorizedRecipientsAsync(actor, recipientScope: null, cancellationToken))
            .FirstOrDefault(x =>
                string.Equals(x.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ParticipantType, normalizedParticipantType, StringComparison.Ordinal));
        return participant is null
            ? MessagingRecipientResult.Failure("MESSAGING_RECIPIENT_NOT_FOUND", "The requested participant is not available.")
            : new MessagingRecipientResult(true, null, null, participant);
    }

    public async Task<MessagingConversationResult> StartConversationAsync(
        StartMessagingConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var targetUserId = NormalizeUserId(command.TargetUserId);
        var targetParticipantType = NormalizeRequired(command.TargetParticipantType);
        var subject = NormalizeOptional(command.Subject);
        var initialMessage = NormalizeOptional(command.InitialMessageBody);
        var clientMessageId = NormalizeOptional(command.ClientMessageId);

        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (string.IsNullOrWhiteSpace(targetUserId) ||
            !Fits(targetUserId, 450) ||
            !Fits(subject, MaximumConversationSubjectLength) ||
            !Fits(initialMessage, MaximumMessageBodyLength) ||
            !Fits(clientMessageId, MaximumClientMessageIdLength))
        {
            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_INVALID", "The requested conversation is invalid.");
        }

        var requestedParticipantCount = 2;
        if (!TryBuildDirectParticipants(actor, targetUserId, targetParticipantType, out var participants))
        {
            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_INVALID", "The requested conversation is invalid.");
        }

        var moderation = _moderation.Evaluate(initialMessage, "MessagingConversationStart");
        if (!moderation.IsAllowed)
        {
            AddAudit(actor.UserId, "ContentBlocked", null, null, targetUserId, moderation.ReasonCode, DateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return MessagingConversationResult.Failure("MESSAGING_CONTENT_BLOCKED", RespectfulCommunicationMessage);
        }

        var conversationType = GetConversationType(actor.ParticipantType, targetParticipantType);
        if (conversationType is null)
            return MessagingConversationResult.Failure("MESSAGING_RECIPIENT_FORBIDDEN", "Messaging is not permitted for the requested recipient.");

        var authorizedRecipient = await GetAuthorizedParticipantAsync(
            actor,
            targetUserId,
            targetParticipantType,
            cancellationToken);
        if (!authorizedRecipient.Succeeded)
            return MessagingConversationResult.Failure("MESSAGING_RECIPIENT_FORBIDDEN", "Messaging is not permitted for the requested recipient.");

        var directConversationKey = BuildDirectConversationKey(
            conversationType,
            actor.UserId,
            actor.ParticipantType,
            targetUserId,
            targetParticipantType);

        var existing = await FindDirectConversationAsync(
            conversationType,
            directConversationKey,
            cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Messaging direct conversation reused. ActorUserId={ActorUserId} TargetUserId={TargetUserId} ConversationId={ConversationId} ConversationType={ConversationType} RequestedParticipantCount={RequestedParticipantCount} DistinctParticipantCount={DistinctParticipantCount}",
                actor.UserId,
                targetUserId,
                existing.Id,
                conversationType,
                requestedParticipantCount,
                participants.Count);
            return await ContinueExistingConversationAsync(actor, existing.Id, initialMessage, clientMessageId, cancellationToken);
        }

        var nowUtc = DateTime.UtcNow;
        var conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = conversationType!,
            DirectConversationKey = directConversationKey,
            Subject = subject,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            CreatedByUserId = actor.UserId
        };
        _db.MessageConversations.Add(conversation);
        _db.MessageConversationParticipants.AddRange(participants.Select(participant => new MessageConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = participant.UserId,
            ParticipantType = participant.ParticipantType,
            IsActive = true,
            JoinedUtc = nowUtc
        }));

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            var message = new InternalMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                SenderUserId = actor.UserId,
                SenderType = actor.ParticipantType,
                Body = initialMessage,
                SentUtc = nowUtc,
                ClientMessageId = clientMessageId
            };
            _db.InternalMessages.Add(message);
            conversation.LastMessageUtc = nowUtc;
            AddAudit(actor.UserId, "MessageSent", conversation.Id, message.Id, null, null, nowUtc);
        }

        AddAudit(actor.UserId, "ConversationCreated", conversation.Id, null, targetUserId, conversationType, nowUtc);
        try
        {
            _logger.LogInformation(
                "Messaging direct conversation creation starting. ActorUserId={ActorUserId} TargetUserId={TargetUserId} ConversationId={ConversationId} ConversationType={ConversationType} DirectConversationKey={DirectConversationKey} RequestedParticipantCount={RequestedParticipantCount} DistinctParticipantCount={DistinctParticipantCount}",
                actor.UserId,
                targetUserId,
                conversation.Id,
                conversationType,
                directConversationKey,
                requestedParticipantCount,
                participants.Count);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var diagnosticId = Guid.NewGuid().ToString("N");
            var rootCause = (Exception)ex;

            while (rootCause.InnerException is not null)
                rootCause = rootCause.InnerException;

            var rootCauseMessage = rootCause.Message
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            var failedEntries = ex.Entries.Count == 0
                ? "None"
                : string.Join(
                    ", ",
                    ex.Entries.Select(entry =>
                        $"{entry.Entity.GetType().FullName}:{entry.State}"));

            _logger.LogError(
                ex,
                "Messaging conversation creation failed. DiagnosticId={DiagnosticId} ActorUserId={ActorUserId} TargetUserId={TargetUserId} ConversationId={ConversationId} ConversationType={ConversationType} DirectConversationKey={DirectConversationKey} RootExceptionType={RootExceptionType} RootExceptionMessage={RootExceptionMessage} FailedEntries={FailedEntries} OccurredUtc={OccurredUtc}",
                diagnosticId,
                actor.UserId,
                targetUserId,
                conversation.Id,
                conversationType,
                directConversationKey,
                rootCause.GetType().FullName,
                rootCauseMessage,
                failedEntries,
                DateTime.UtcNow);

            if (IsVerifiedDirectConversationKeyConflict(ex))
            {
                _db.ChangeTracker.Clear();
                var concurrent = await FindDirectConversationAsync(
                    conversationType,
                    directConversationKey,
                    cancellationToken);
                if (concurrent is not null)
                {
                    _logger.LogInformation(
                        "Messaging direct conversation creation resolved a verified uniqueness race. DiagnosticId={DiagnosticId} ActorUserId={ActorUserId} TargetUserId={TargetUserId} ConversationId={ConversationId} ConversationType={ConversationType} DirectConversationKey={DirectConversationKey}",
                        diagnosticId,
                        actor.UserId,
                        targetUserId,
                        concurrent.Id,
                        conversationType,
                        directConversationKey);
                    return await ContinueExistingConversationAsync(actor, concurrent.Id, initialMessage, clientMessageId, cancellationToken);
                }
            }

            return MessagingConversationResult.Failure(
                "MESSAGING_CONVERSATION_SAVE_FAILED",
                $"We could not open this conversation. Please try again. If the issue continues, provide Diagnostic ID: {diagnosticId}.");
        }

        return await GetConversationAsync(actor, conversation.Id, cancellationToken);
    }

    public async Task<MessagingMessageResult> SendMessageAsync(
        SendMessagingMessageCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var body = NormalizeOptional(command.Body);
        var clientMessageId = NormalizeOptional(command.ClientMessageId);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingMessageResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.ConversationId == Guid.Empty || string.IsNullOrWhiteSpace(body) ||
            !Fits(body, MaximumMessageBodyLength) || !Fits(clientMessageId, MaximumClientMessageIdLength))
        {
            return MessagingMessageResult.Failure("MESSAGING_MESSAGE_INVALID", "The message is invalid.");
        }

        var moderation = _moderation.Evaluate(body, "MessagingMessage");
        if (!moderation.IsAllowed)
        {
            AddAudit(actor.UserId, "ContentBlocked", command.ConversationId, null, null, moderation.ReasonCode, DateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return MessagingMessageResult.Failure("MESSAGING_CONTENT_BLOCKED", RespectfulCommunicationMessage);
        }

        var conversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .FirstOrDefaultAsync(x => x.Id == command.ConversationId, cancellationToken);
        if (conversation is null)
            return MessagingMessageResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");
        if (conversation.IsClosed)
            return MessagingMessageResult.Failure("MESSAGING_CONVERSATION_CLOSED", "Closed conversations cannot receive new messages.");
        if (conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
            !await ConversationHasActiveClientMembershipAsync(conversation.Id, cancellationToken))
        {
            return MessagingMessageResult.Failure(
                "MESSAGING_MEMBERSHIP_INACTIVE",
                "This client membership is inactive. The conversation history remains available, but new messages cannot be sent until membership is restored.");
        }

        InternalMessage? replyTarget = null;
        if (command.ReplyToMessageId.HasValue)
        {
            replyTarget = await _db.InternalMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    message =>
                        message.Id == command.ReplyToMessageId.Value &&
                        message.ConversationId == conversation.Id,
                    cancellationToken);

            if (replyTarget is null)
            {
                return MessagingMessageResult.Failure(
                    "MESSAGING_REPLY_TARGET_INVALID",
                    "The message you are replying to is no longer available in this conversation.");
            }
        }

        if (!string.IsNullOrWhiteSpace(clientMessageId))
        {
            var duplicate = await _db.InternalMessages
                .AsNoTracking()
                .Where(x => x.ClientMessageId == clientMessageId)
                .Select(x => new
                {
                    x.Id,
                    x.ConversationId,
                    x.SenderUserId,
                    x.SenderType,
                    x.Body,
                    x.SentUtc,
                    x.EditedUtc,
                    x.IsDeleted,
                    x.ReplyToMessageId,
                    Reply = x.ReplyToMessage == null
                        ? null
                        : new MessagingReplyPreview(
                            x.ReplyToMessage.Id,
                            x.ReplyToMessage.SenderUserId,
                            x.ReplyToMessage.SenderType,
                            x.ReplyToMessage.Body,
                            x.ReplyToMessage.IsDeleted)
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                if (duplicate.ConversationId == conversation.Id &&
                    IsSameParticipant(
                        duplicate.SenderUserId,
                        duplicate.SenderType,
                        actor.UserId,
                        actor.ParticipantType))
                {
                    return new MessagingMessageResult(
                        true,
                        null,
                        null,
                        new MessagingMessageSummary(
                            duplicate.Id,
                            duplicate.ConversationId,
                            duplicate.SenderUserId,
                            duplicate.SenderType,
                            duplicate.Body,
                            duplicate.SentUtc,
                            duplicate.EditedUtc,
                            duplicate.IsDeleted,
                            Array.Empty<MessagingAttachmentSummary>(),
                            duplicate.ReplyToMessageId,
                            duplicate.Reply),
                        duplicate.ConversationId);
                }

                return MessagingMessageResult.Failure("MESSAGING_CLIENT_MESSAGE_CONFLICT", "The client message identifier has already been used.");
            }
        }

        var nowUtc = DateTime.UtcNow;
        var message = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = actor.UserId,
            SenderType = actor.ParticipantType,
            Body = body,
            SentUtc = nowUtc,
            ClientMessageId = clientMessageId,
            ReplyToMessageId = command.ReplyToMessageId
        };
        _db.InternalMessages.Add(message);
        conversation.LastMessageUtc = nowUtc;
        conversation.UpdatedUtc = nowUtc;
        AddAudit(actor.UserId, "MessageSent", conversation.Id, message.Id, null, null, nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Messaging message save failed. ActorUserId={ActorUserId} ConversationId={ConversationId}", actor.UserId, conversation.Id);
            return MessagingMessageResult.Failure("MESSAGING_MESSAGE_SAVE_FAILED", "The message could not be saved.");
        }

        return new MessagingMessageResult(
            true,
            null,
            null,
            new MessagingMessageSummary(
                message.Id,
                message.ConversationId,
                message.SenderUserId,
                message.SenderType,
                message.Body,
                message.SentUtc,
                message.EditedUtc,
                message.IsDeleted,
                Array.Empty<MessagingAttachmentSummary>(),
                message.ReplyToMessageId,
                replyTarget is null
                    ? null
                    : new MessagingReplyPreview(
                        replyTarget.Id,
                        replyTarget.SenderUserId,
                        replyTarget.SenderType,
                        replyTarget.Body,
                        replyTarget.IsDeleted)),
            conversation.Id);
    }

    public async Task<MessagingOperationResult> MarkConversationReadAsync(
        MessagingConversationActionCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == command.ConversationId, cancellationToken);
        if (conversation is null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        var participant = await _db.MessageConversationParticipants
            .FirstOrDefaultAsync(x =>
                x.ConversationId == command.ConversationId &&
                x.IsActive &&
                x.UserId == actor.UserId &&
                x.ParticipantType == actor.ParticipantType,
                cancellationToken);
        if (participant is null)
            return MessagingOperationResult.Failure("MESSAGING_PARTICIPANT_NOT_FOUND", "The conversation participant was not found.");

        var latestMessage = await _db.InternalMessages
            .AsNoTracking()
            .Where(x => x.ConversationId == command.ConversationId)
            .OrderByDescending(x => x.SentUtc)
            .Select(x => new { x.Id, x.SentUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (latestMessage is null)
            return MessagingOperationResult.Success();

        participant.LastReadUtc = latestMessage.SentUtc;
        participant.LastReadMessageId = latestMessage.Id;
        AddAudit(actor.UserId, "ConversationRead", command.ConversationId, latestMessage.Id, null, null, DateTime.UtcNow);
        return await SaveOperationAsync("ConversationRead", actor.UserId, command.ConversationId, cancellationToken);
    }

    public async Task<MessagingOperationResult> SetConversationMutedAsync(
        SetMessagingConversationMutedCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var participant = await FindAuthorizedParticipantAsync(actor, command.ConversationId, cancellationToken);
        if (participant is null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        participant.IsMuted = command.IsMuted;
        AddAudit(actor.UserId, command.IsMuted ? "ConversationMuted" : "ConversationUnmuted", command.ConversationId, null, null, null, DateTime.UtcNow);
        return await SaveOperationAsync("ConversationMuted", actor.UserId, command.ConversationId, cancellationToken);
    }

    public async Task<MessagingOperationResult> SetConversationClosedAsync(
        SetMessagingConversationClosedCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .FirstOrDefaultAsync(x => x.Id == command.ConversationId, cancellationToken);
        if (conversation is null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        var nowUtc = DateTime.UtcNow;
        conversation.IsClosed = command.IsClosed;
        conversation.ClosedUtc = command.IsClosed ? nowUtc : null;
        conversation.UpdatedUtc = nowUtc;
        AddAudit(actor.UserId, command.IsClosed ? "ConversationClosed" : "ConversationReopened", conversation.Id, null, null, null, nowUtc);
        return await SaveOperationAsync("ConversationClosed", actor.UserId, conversation.Id, cancellationToken);
    }

    public async Task<MessagingAttachmentResult> AddPendingAttachmentAsync(
        AddPendingMessagingAttachmentCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingAttachmentResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.AttachmentId == Guid.Empty || command.InternalMessageId == Guid.Empty || command.SizeBytes <= 0 ||
            !Fits(command.OriginalFileName, MaximumAttachmentNameLength) ||
            !Fits(command.StoredFileName, MaximumAttachmentNameLength) ||
            !Fits(command.ContentType, MaximumAttachmentContentTypeLength) ||
            !Fits(command.StoragePath, MaximumAttachmentStoragePathLength) ||
            string.IsNullOrWhiteSpace(command.OriginalFileName) ||
            string.IsNullOrWhiteSpace(command.StoredFileName) ||
            string.IsNullOrWhiteSpace(command.ContentType) ||
            string.IsNullOrWhiteSpace(command.StoragePath))
        {
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_INVALID", "The attachment is invalid.");
        }

        var moderation = _moderation.Evaluate(command.OriginalFileName, "MessagingAttachmentFilename");
        if (!moderation.IsAllowed)
            return MessagingAttachmentResult.Failure("MESSAGING_CONTENT_BLOCKED", RespectfulCommunicationMessage);

        var message = await _db.InternalMessages
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == command.InternalMessageId, cancellationToken);
        if (message is null || message.IsDeleted ||
            !string.Equals(message.SenderUserId, actor.UserId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(message.SenderType, actor.ParticipantType, StringComparison.OrdinalIgnoreCase))
        {
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_FORBIDDEN", "Attachments can only be added to your own messages.");
        }

        var authorized = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .AnyAsync(x => x.Id == message.ConversationId, cancellationToken);
        if (!authorized)
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_FORBIDDEN", "Attachments are not permitted for this message.");

        if (await _db.MessageAttachments.AnyAsync(x => x.Id == command.AttachmentId, cancellationToken))
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_CONFLICT", "The attachment already exists.");

        var nowUtc = DateTime.UtcNow;
        var attachment = new MessageAttachment
        {
            Id = command.AttachmentId,
            InternalMessageId = message.Id,
            OriginalFileName = Path.GetFileName(command.OriginalFileName.Trim()),
            StoredFileName = Path.GetFileName(command.StoredFileName.Trim()),
            ContentType = command.ContentType.Trim(),
            SizeBytes = command.SizeBytes,
            StoragePath = command.StoragePath.Trim(),
            ScanStatus = MessagingAttachmentScanStatuses.Pending,
            CreatedUtc = nowUtc
        };
        _db.MessageAttachments.Add(attachment);
        AddAudit(actor.UserId, "AttachmentAdded", message.ConversationId, message.Id, null, attachment.OriginalFileName, nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Messaging attachment save failed. ActorUserId={ActorUserId} MessageId={MessageId}", actor.UserId, message.Id);
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_SAVE_FAILED", "The attachment could not be saved.");
        }

        return new MessagingAttachmentResult(
            true,
            null,
            null,
            ToAttachmentSummary(attachment),
            message.ConversationId);
    }

    public async Task<MessagingAttachmentAccessResult> GetAttachmentForDownloadAsync(
        MessagingAttachmentDownloadCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingAttachmentAccessResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var attachment = await _db.MessageAttachments
            .Include(x => x.InternalMessage)
            .FirstOrDefaultAsync(x => x.Id == command.AttachmentId, cancellationToken);
        if (attachment is null)
            return MessagingAttachmentAccessResult.Failure("MESSAGING_ATTACHMENT_NOT_FOUND", "The requested attachment was not found.");
        if (!string.Equals(attachment.ScanStatus, MessagingAttachmentScanStatuses.Clean, StringComparison.OrdinalIgnoreCase))
            return MessagingAttachmentAccessResult.Failure("MESSAGING_ATTACHMENT_NOT_READY", "This attachment is not available until scanning is complete.");

        var authorized = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .AnyAsync(x => x.Id == attachment.InternalMessage.ConversationId, cancellationToken);
        if (!authorized)
            return MessagingAttachmentAccessResult.Failure("MESSAGING_ATTACHMENT_FORBIDDEN", "The requested attachment is not available.");

        AddAudit(actor.UserId, "AttachmentDownloaded", attachment.InternalMessage.ConversationId, attachment.InternalMessageId, null, attachment.OriginalFileName, DateTime.UtcNow);
        var operation = await SaveOperationAsync("AttachmentDownloaded", actor.UserId, attachment.InternalMessage.ConversationId, cancellationToken);
        if (!operation.Succeeded)
            return MessagingAttachmentAccessResult.Failure(operation.ErrorCode!, operation.ErrorMessage!);

        return new MessagingAttachmentAccessResult(
            true,
            null,
            null,
            new MessagingAttachmentDownloadDescriptor(
                attachment.Id,
                attachment.OriginalFileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.StoragePath));
    }

    public async Task<MessagingOperationResult> UpdateAttachmentScanStatusAsync(
        UpdateMessagingAttachmentScanStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        var actorUserId = NormalizeRequired(command.ActorUserId);
        var status = NormalizeRequired(command.ScanStatus);
        if (string.IsNullOrWhiteSpace(actorUserId) || command.AttachmentId == Guid.Empty ||
            !Fits(actorUserId, 450) || !Fits(command.Detail, MaximumAuditDetailLength) ||
            !IsSupportedScanStatus(status))
        {
            return MessagingOperationResult.Failure("MESSAGING_ATTACHMENT_SCAN_INVALID", "The attachment scan update is invalid.");
        }

        var attachment = await _db.MessageAttachments
            .Include(x => x.InternalMessage)
            .FirstOrDefaultAsync(x => x.Id == command.AttachmentId, cancellationToken);
        if (attachment is null)
            return MessagingOperationResult.Failure("MESSAGING_ATTACHMENT_NOT_FOUND", "The requested attachment was not found.");
        if (!IsAllowedScanTransition(attachment.ScanStatus, status))
            return MessagingOperationResult.Failure("MESSAGING_ATTACHMENT_SCAN_TRANSITION_INVALID", "The attachment scan status cannot be changed that way.");

        attachment.ScanStatus = status;
        AddAudit(
            actorUserId,
            "AttachmentScanStatusUpdated",
            attachment.InternalMessage.ConversationId,
            attachment.InternalMessageId,
            null,
            NormalizeOptional(command.Detail),
            DateTime.UtcNow);
        return await SaveOperationAsync("AttachmentScanStatusUpdated", actorUserId, attachment.InternalMessage.ConversationId, cancellationToken);
    }

    private async Task<IQueryable<MessageConversation>> AuthorizedConversationsQueryAsync(
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        var actorUserId = NormalizeRequired(actor.UserId);
        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        var participantConversations = _db.MessageConversations.Where(conversation =>
            conversation.Participants.Any(participant =>
                participant.IsActive &&
                participant.UserId.ToLower() == actorUserId &&
                participant.ParticipantType == actorParticipantType));

        var activeAgentUserIds = ActiveMessagingAgentProfilesQuery()
            .Select(profile => profile.AgentUserId.ToLower());
        var activeClientUserIds = ActiveMessagingClientProfilesQuery()
            .Select(profile => profile.ClientUserId.ToLower());

        var clientJourneyConversations = participantConversations.Where(conversation =>
            conversation.ConversationType == MessagingConversationTypes.ClientJourney &&
            conversation.Participants.Count(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client) == 2 &&
            conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                .All(participant => activeClientUserIds.Contains(participant.UserId.ToLower())) &&
            !_db.JourneyCircleBlocks.Any(block =>
                _db.ClientProfiles.Any(profile => profile.Id == block.BlockerClientProfileId &&
                    conversation.Participants.Any(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client && participant.UserId.ToLower() == profile.ClientUserId.ToLower())) &&
                _db.ClientProfiles.Any(profile => profile.Id == block.BlockedClientProfileId &&
                    conversation.Participants.Any(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client && participant.UserId.ToLower() == profile.ClientUserId.ToLower()))));

        if (actorParticipantType == MessagingParticipantTypes.Agent)
        {
            var authorizedClientIds = AuthorizedClientIdsForAgentQuery(actorUserId);
            var activeLeadUserIds = await ActiveLeadUserIdsAsync(cancellationToken);
            var agentDirectConversations = participantConversations.Where(conversation =>
                conversation.ConversationType == MessagingConversationTypes.AgentDirect &&
                conversation.Participants.All(participant =>
                    participant.ParticipantType == MessagingParticipantTypes.Agent &&
                    participant.IsActive &&
                    activeAgentUserIds.Contains(participant.UserId.ToLower())));
            var clientAgentConversations = participantConversations.Where(conversation =>
                conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Agent)
                    .All(agent => activeAgentUserIds.Contains(agent.UserId.ToLower())) &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                    .All(client => activeClientUserIds.Contains(client.UserId.ToLower())) &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                    .Any(client => authorizedClientIds.Contains(client.UserId.ToLower()) ||
                        activeLeadUserIds.Contains(client.UserId.ToLower())));
            return agentDirectConversations.Union(clientAgentConversations);
        }

        if (actorParticipantType == MessagingParticipantTypes.Client)
        {
            var clientAgentConversations = participantConversations.Where(conversation =>
                conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Agent)
                    .All(agent => activeAgentUserIds.Contains(agent.UserId.ToLower())) &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                    .All(client => activeClientUserIds.Contains(client.UserId.ToLower())));
            return clientAgentConversations.Union(clientJourneyConversations);
        }

        return participantConversations.Where(_ => false);
    }

    private async Task<MessageConversationParticipant?> FindAuthorizedParticipantAsync(
        MessagingActor actor,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var isAuthorized = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .AnyAsync(x => x.Id == conversationId, cancellationToken);
        if (!isAuthorized)
            return null;

        return await _db.MessageConversationParticipants.FirstOrDefaultAsync(x =>
            x.ConversationId == conversationId && x.IsActive &&
            x.UserId.ToLower() == actor.UserId && x.ParticipantType == actor.ParticipantType,
            cancellationToken);
    }

    private async Task<MessageConversation?> FindDirectConversationAsync(
        string conversationType,
        string directConversationKey,
        CancellationToken cancellationToken)
    {
        return await _db.MessageConversations
            .Where(x => x.ConversationType == conversationType)
            .Where(x => x.DirectConversationKey == directConversationKey)
            .OrderByDescending(x => x.LastMessageUtc ?? x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<MessagingConversationResult> ContinueExistingConversationAsync(
        MessagingActor actor,
        Guid conversationId,
        string? initialMessage,
        string? clientMessageId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            var sendResult = await SendMessageAsync(
                new SendMessagingMessageCommand(actor, conversationId, initialMessage, clientMessageId),
                cancellationToken);
            if (!sendResult.Succeeded)
                return MessagingConversationResult.Failure(sendResult.ErrorCode!, sendResult.ErrorMessage!);
        }

        return await GetConversationAsync(actor, conversationId, cancellationToken);
    }

    private async Task<bool> IsValidActorAsync(MessagingActor actor, CancellationToken cancellationToken)
    {
        var normalizedActor = NormalizeActor(actor);
        if (string.IsNullOrWhiteSpace(normalizedActor.UserId) ||
            !Fits(normalizedActor.UserId, 450) ||
            !IsParticipantType(normalizedActor.ParticipantType))
        {
            return false;
        }

        if (normalizedActor.ParticipantType == MessagingParticipantTypes.Agent)
        {
            var isAssistant = await _db.AgentAssistants
                .AsNoTracking()
                .AnyAsync(x => x.AssistantUserId != null && x.AssistantUserId.ToLower() == normalizedActor.UserId, cancellationToken);
            if (isAssistant)
                return false;
        }

        return normalizedActor.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await _db.AgentProfiles.AsNoTracking().AnyAsync(
                x => x.IsActive && x.AgentUserId.ToLower() == normalizedActor.UserId,
                cancellationToken),
            MessagingParticipantTypes.Client => await ActiveMessagingClientProfilesQuery().AnyAsync(
                x => x.ClientUserId.ToLower() == normalizedActor.UserId ||
                     (x.ExternalIdentityObjectId != null && x.ExternalIdentityObjectId.ToLower() == normalizedActor.UserId),
                cancellationToken),
            _ => false
        };
    }

    private async Task<List<MessagingRecipientSummary>> ListAuthorizedRecipientsAsync(
        MessagingActor actor,
        string? recipientScope,
        CancellationToken cancellationToken)
    {
        var candidates = actor.ParticipantType == MessagingParticipantTypes.Agent
            ? await ListAgentRecipientsAsync(actor.UserId, recipientScope, cancellationToken)
            : await ListClientRecipientsAsync(actor.UserId, recipientScope, cancellationToken);
        var recipients = await ResolveRecipientIdentitiesAsync(
            CollapseAuthorizedRecipients(actor, candidates),
            cancellationToken);
        return await AttachExistingConversationIdsAsync(actor, recipients, cancellationToken);
    }

    private static bool MatchesContactSearch(MessagingRecipientSummary recipient, string search)
    {
        var normalizedSearch = NormalizeSearchText(search);
        if (string.IsNullOrWhiteSpace(normalizedSearch))
            return true;

        var searchable = NormalizeSearchText($"{recipient.DisplayName} {recipient.Email}");
        return normalizedSearch
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(token => searchable.Contains(token, StringComparison.Ordinal));
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<List<MessagingRecipientSummary>> ListAgentRecipientsAsync(
        string agentUserId,
        string? recipientScope,
        CancellationToken cancellationToken)
    {
        var recipients = new List<MessagingRecipientSummary>();
        if (recipientScope is not MessagingRecipientScopes.Clients and not MessagingRecipientScopes.Leads)
        {
            var agentRows = await ActiveMessagingAgentProfilesQuery()
                .Where(x => x.AgentUserId.ToLower() != agentUserId)
                .Select(x => new RecipientAgentRow(x.AgentUserId, x.FullName, x.AgentUpn))
                .ToListAsync(cancellationToken);

            recipients.AddRange(agentRows.Select(x => new MessagingRecipientSummary(
                x.UserId,
                MessagingParticipantTypes.Agent,
                FirstNonEmpty(x.FullName, x.Email, "Agent"),
                x.Email,
                "Agent")));
        }

        if (recipientScope is not MessagingRecipientScopes.Agents)
        {
            var linkedClientIds = await AuthorizedClientIdsForAgentQuery(agentUserId)
                .ToListAsync(cancellationToken);
            var clientRows = await ActiveMessagingClientProfilesQuery()
                .Select(x => new RecipientClientRow(x.ClientUserId, x.FirstName, x.LastName, x.Email, x.CrmNotes, x.CrmStatus))
                .ToListAsync(cancellationToken);

            recipients.AddRange(clientRows
                .Where(x => recipientScope switch
                {
                    MessagingRecipientScopes.Clients =>
                        linkedClientIds.Contains(x.UserId.ToLower()) &&
                        ClientRecordClassification.IsClientOrBusinessClient(x.UserId, x.CrmNotes, x.CrmStatus),
                    MessagingRecipientScopes.Leads => ClientRecordClassification.IsLead(x.UserId, x.CrmNotes, x.CrmStatus),
                    _ =>
                        ClientRecordClassification.IsLead(x.UserId, x.CrmNotes, x.CrmStatus) ||
                        (linkedClientIds.Contains(x.UserId.ToLower()) &&
                         ClientRecordClassification.IsClientOrBusinessClient(x.UserId, x.CrmNotes, x.CrmStatus))
                })
                .Select(x => new MessagingRecipientSummary(
                    x.UserId,
                    MessagingParticipantTypes.Client,
                    FirstNonEmpty($"{x.FirstName} {x.LastName}".Trim(), x.Email, "Client"),
                    x.Email,
                    ClientRecordClassification.IsLead(x.UserId, x.CrmNotes, x.CrmStatus) ? "Lead" : "Client")));
        }

        return recipients;
    }

    private async Task<List<MessagingRecipientSummary>> ListClientRecipientsAsync(
        string clientUserId,
        string? recipientScope,
        CancellationToken cancellationToken)
    {
        var recipients = new List<MessagingRecipientSummary>();
        if (recipientScope is not MessagingRecipientScopes.Clients)
        {
            var agentRows = await ActiveMessagingAgentProfilesQuery()
                .Select(x => new RecipientAgentRow(x.AgentUserId, x.FullName, x.AgentUpn))
                .ToListAsync(cancellationToken);

            recipients.AddRange(agentRows.Select(x => new MessagingRecipientSummary(
                x.UserId,
                MessagingParticipantTypes.Agent,
                FirstNonEmpty(x.FullName, x.Email, "Agent"),
                x.Email,
                "Agent")));
        }

        if (recipientScope is not MessagingRecipientScopes.Agents)
        {
            var clientKey = NormalizeUserId(clientUserId);
            var actorProfileIds = await ActiveMessagingClientProfilesQuery()
                .Where(profile => profile.ClientUserId.ToLower() == clientKey ||
                    (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == clientKey))
                .Select(profile => profile.Id)
                .ToListAsync(cancellationToken);
            var blockedProfileIds = actorProfileIds.Count == 0
                ? new HashSet<Guid>()
                : (await _db.JourneyCircleBlocks.AsNoTracking()
                    .Where(block => actorProfileIds.Contains(block.BlockerClientProfileId) || actorProfileIds.Contains(block.BlockedClientProfileId))
                    .Select(block => actorProfileIds.Contains(block.BlockerClientProfileId)
                        ? block.BlockedClientProfileId
                        : block.BlockerClientProfileId)
                    .ToListAsync(cancellationToken))
                    .ToHashSet();
            var clientRows = await ActiveMessagingClientProfilesQuery()
                .Where(profile => !blockedProfileIds.Contains(profile.Id))
                .Select(profile => new RecipientClientRow(profile.ClientUserId, profile.FirstName, profile.LastName, profile.Email, profile.CrmNotes, profile.CrmStatus))
                .ToListAsync(cancellationToken);

            recipients.AddRange(clientRows
                .Where(row => ClientRecordClassification.IsClientOrBusinessClient(row.UserId, row.CrmNotes, row.CrmStatus))
                .Select(row => new MessagingRecipientSummary(
                    row.UserId,
                    MessagingParticipantTypes.Client,
                    FirstNonEmpty($"{row.FirstName} {row.LastName}".Trim(), row.Email, "Client"),
                    row.Email,
                    "Client")));
        }

        return recipients;
    }

    private static List<MessagingRecipientSummary> CollapseAuthorizedRecipients(
        MessagingActor actor,
        IEnumerable<MessagingRecipientSummary> candidates)
    {
        return candidates
            .Select(recipient => recipient with
            {
                UserId = NormalizeUserId(recipient.UserId),
                ParticipantType = NormalizeRequired(recipient.ParticipantType),
                DisplayName = FirstNonEmpty(recipient.DisplayName, recipient.Email, "Participant")
            })
            .Where(recipient =>
                !string.IsNullOrWhiteSpace(recipient.UserId) &&
                IsParticipantType(recipient.ParticipantType) &&
                !IsSameParticipant(
                    recipient.UserId,
                    recipient.ParticipantType,
                    actor.UserId,
                    actor.ParticipantType))
            .GroupBy(recipient => (recipient.UserId, recipient.ParticipantType))
            .Select(group => group
                .OrderBy(recipient => recipient.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(recipient => recipient.ParticipantType)
            .ThenBy(recipient => recipient.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<MessagingRecipientSummary>> ResolveRecipientIdentitiesAsync(
        IReadOnlyCollection<MessagingRecipientSummary> recipients,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
            return Array.Empty<MessagingRecipientSummary>().ToList();

        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            recipients.Select(recipient => new MessagingParticipantReference(
                recipient.UserId,
                recipient.ParticipantType)),
            cancellationToken);

        return recipients
            .Where(recipient => identities.ContainsKey((recipient.UserId, recipient.ParticipantType)))
            .Select(recipient =>
            {
                var identity = identities[(recipient.UserId, recipient.ParticipantType)];
                return recipient with
                {
                    UserId = identity.UserId,
                    ParticipantType = identity.ParticipantType,
                    DisplayName = identity.DisplayName,
                    Email = identity.Email
                };
            })
            .ToList();
    }

    private async Task<List<MessagingRecipientSummary>> AttachExistingConversationIdsAsync(
        MessagingActor actor,
        IReadOnlyList<MessagingRecipientSummary> recipients,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
            return Array.Empty<MessagingRecipientSummary>().ToList();

        var recipientKeys = recipients
            .Select(recipient => recipient.UserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conversations = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(participant =>
                participant.IsActive &&
                recipientKeys.Contains(participant.UserId.ToLower()))
            .Where(participant => participant.Conversation.Participants.Count(conversationParticipant =>
                conversationParticipant.IsActive) == 2)
            .Where(participant => participant.Conversation.Participants.Any(conversationParticipant =>
                conversationParticipant.IsActive &&
                conversationParticipant.UserId.ToLower() == actor.UserId &&
                conversationParticipant.ParticipantType == actor.ParticipantType))
            .OrderByDescending(participant => participant.Conversation.LastMessageUtc ?? participant.Conversation.CreatedUtc)
            .Select(participant => new ExistingConversationRow(
                participant.ConversationId,
                participant.UserId,
                participant.ParticipantType))
            .ToListAsync(cancellationToken);

        var existingByRecipient = new Dictionary<(string UserId, string ParticipantType), Guid>();
        foreach (var conversation in conversations)
        {
            existingByRecipient.TryAdd(
                (NormalizeUserId(conversation.UserId), NormalizeRequired(conversation.ParticipantType)),
                conversation.Id);
        }

        return recipients
            .Select(recipient => existingByRecipient.TryGetValue(
                    (recipient.UserId, recipient.ParticipantType),
                    out var conversationId)
                ? recipient with { ExistingConversationId = conversationId }
                : recipient)
            .ToList();
    }

    private IQueryable<AgentClient> PrimaryClientAgentLinks(string clientUserId, string agentUserId)
    {
        var clientKey = NormalizeUserId(clientUserId);
        var agentKey = NormalizeUserId(agentUserId);
        return _db.AgentClients.AsNoTracking().Where(link =>
            link.ClientUserId.ToLower() == clientKey &&
            link.AgentUserId.ToLower() == agentKey);
    }

    private IQueryable<AgentClient> PrimaryClientAgentLinksForAgent(string agentUserId)
    {
        var agentKey = NormalizeUserId(agentUserId);
        return _db.AgentClients.AsNoTracking().Where(link =>
            link.AgentUserId.ToLower() == agentKey);
    }

    private IQueryable<ClientAgentMessagingGrant> ActiveMessagingGrants(string clientUserId, string agentUserId)
    {
        var clientKey = NormalizeUserId(clientUserId);
        var agentKey = NormalizeUserId(agentUserId);
        return _db.ClientAgentMessagingGrants.AsNoTracking().Where(grant =>
            grant.IsActive && grant.ClientUserId.ToLower() == clientKey && grant.AgentUserId.ToLower() == agentKey);
    }

    private IQueryable<ClientAgentMessagingGrant> ActiveMessagingGrantsForAgent(string agentUserId)
    {
        var agentKey = NormalizeUserId(agentUserId);
        return _db.ClientAgentMessagingGrants.AsNoTracking().Where(grant => grant.IsActive && grant.AgentUserId.ToLower() == agentKey);
    }

    private IQueryable<string> AuthorizedClientIdsForAgentQuery(string agentUserId) =>
        PrimaryClientAgentLinksForAgent(agentUserId)
            .Select(link => link.ClientUserId.ToLower())
            .Union(ActiveMessagingGrantsForAgent(agentUserId)
                .Select(grant => grant.ClientUserId.ToLower()))
            .Distinct();

    private IQueryable<AgentProfile> ActiveMessagingAgentProfilesQuery() =>
        _db.AgentProfiles.AsNoTracking()
            .Where(profile => profile.IsActive)
            .Where(profile => !_db.AgentAssistants.Any(assistant =>
                assistant.AssistantUserId != null &&
                assistant.AssistantUserId.ToLower() == profile.AgentUserId.ToLower()));

    private IQueryable<ClientProfile> ActiveMessagingClientProfilesQuery() =>
        _db.ClientProfiles.AsNoTracking()
            .Where(profile => profile.CrmStatus == null ||
                !new[] { "dormant", "inactive", "deleted", "blocked", "suspended", "cancelled", "canceled", "paused" }
                    .Contains(profile.CrmStatus.ToLower()))
            .Where(profile => !_db.ClientSubscriptions.Any(subscription => subscription.ClientProfileId == profile.Id) ||
                _db.ClientSubscriptions.Any(subscription =>
                    subscription.ClientProfileId == profile.Id &&
                    (subscription.Status == ClientSubscriptionStatus.Active ||
                     subscription.Status == ClientSubscriptionStatus.GracePeriod)));

    private async Task<HashSet<string>> ActiveLeadUserIdsAsync(CancellationToken cancellationToken)
    {
        var clientRows = await ActiveMessagingClientProfilesQuery()
            .Select(profile => new RecipientClientRow(
                profile.ClientUserId,
                profile.FirstName,
                profile.LastName,
                profile.Email,
                profile.CrmNotes,
                profile.CrmStatus))
            .ToListAsync(cancellationToken);

        return clientRows
            .Where(profile => ClientRecordClassification.IsLead(profile.UserId, profile.CrmNotes, profile.CrmStatus))
            .Select(profile => NormalizeUserId(profile.UserId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IQueryable<string> ActiveClientMembershipUserIdsQuery() =>
        ActiveMessagingClientProfilesQuery()
            .Select(profile => profile.ClientUserId.ToLower());

    private async Task<bool> ConversationHasActiveClientMembershipAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var clientUserIds = _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(participant =>
                participant.ConversationId == conversationId &&
                participant.IsActive &&
                participant.ParticipantType == MessagingParticipantTypes.Client)
            .Select(participant => participant.UserId.ToLower());

        return await ActiveClientMembershipUserIdsQuery()
            .AnyAsync(clientUserId => clientUserIds.Contains(clientUserId), cancellationToken);
    }

    private async Task<Dictionary<(string UserId, string ParticipantType), string>> LoadDisplayNamesAsync(
        IReadOnlyCollection<ParticipantRow> participants,
        CancellationToken cancellationToken)
    {
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            participants.Select(participant => new MessagingParticipantReference(
                participant.UserId,
                participant.ParticipantType)),
            cancellationToken);
        return identities.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DisplayName);
    }

    private async Task<MessagingOperationResult> SaveOperationAsync(
        string operation,
        string actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return MessagingOperationResult.Success();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Messaging operation failed. Operation={Operation} ActorUserId={ActorUserId} ConversationId={ConversationId}", operation, actorUserId, conversationId);
            return MessagingOperationResult.Failure("MESSAGING_OPERATION_SAVE_FAILED", "The messaging change could not be saved.");
        }
    }

    private void AddAudit(
        string actorUserId,
        string action,
        Guid? conversationId,
        Guid? messageId,
        string? targetUserId,
        string? detail,
        DateTime createdUtc)
    {
        _db.MessagingAuditEntries.Add(new MessagingAuditEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = action,
            ConversationId = conversationId,
            InternalMessageId = messageId,
            TargetUserId = NormalizeOptional(targetUserId),
            Detail = Truncate(NormalizeOptional(detail), MaximumAuditDetailLength),
            CreatedUtc = createdUtc
        });
    }

    private static MessagingActor NormalizeActor(MessagingActor actor) => new(
        NormalizeUserId(actor.UserId),
        NormalizeRequired(actor.ParticipantType));

    private static string? GetConversationType(string actorParticipantType, string targetParticipantType)
    {
        if (actorParticipantType == MessagingParticipantTypes.Agent && targetParticipantType == MessagingParticipantTypes.Agent)
            return MessagingConversationTypes.AgentDirect;
        if ((actorParticipantType == MessagingParticipantTypes.Agent && targetParticipantType == MessagingParticipantTypes.Client) ||
            (actorParticipantType == MessagingParticipantTypes.Client && targetParticipantType == MessagingParticipantTypes.Agent))
        {
            return MessagingConversationTypes.ClientAgent;
        }

        if (actorParticipantType == MessagingParticipantTypes.Client && targetParticipantType == MessagingParticipantTypes.Client)
            return MessagingConversationTypes.ClientJourney;

        return null;
    }

    private static string BuildDirectConversationKey(
        string conversationType,
        string firstUserId,
        string firstParticipantType,
        string secondUserId,
        string secondParticipantType)
    {
        var participantKeys = new[]
        {
            BuildDirectParticipantKey(firstUserId, firstParticipantType),
            BuildDirectParticipantKey(secondUserId, secondParticipantType)
        };

        Array.Sort(participantKeys, StringComparer.Ordinal);
        return $"{conversationType}|{participantKeys[0]}|{participantKeys[1]}";
    }

    private static bool TryBuildDirectParticipants(
        MessagingActor actor,
        string targetUserId,
        string targetParticipantType,
        out IReadOnlyList<DirectConversationParticipant> participants)
    {
        var requested = new[]
        {
            new DirectConversationParticipant(NormalizeUserId(actor.UserId), NormalizeRequired(actor.ParticipantType)),
            new DirectConversationParticipant(NormalizeUserId(targetUserId), NormalizeRequired(targetParticipantType))
        };

        if (requested.Any(participant =>
                string.IsNullOrWhiteSpace(participant.UserId) ||
                !IsParticipantType(participant.ParticipantType)))
        {
            participants = Array.Empty<DirectConversationParticipant>();
            return false;
        }

        var distinctParticipants = requested
            .GroupBy(participant => (participant.UserId, participant.ParticipantType))
            .Select(group => group.First())
            .ToArray();

        participants = distinctParticipants;
        return distinctParticipants.Length == requested.Length && distinctParticipants.Length == 2;
    }

    private static bool IsVerifiedDirectConversationKeyConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqlException sqlException || sqlException.Number is not (2601 or 2627))
                continue;

            if (sqlException.Errors.Cast<SqlError>().Any(error =>
                    error.Message.Contains("IX_MessageConversations_DirectConversationKey", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsParticipantType(string value) =>
        value == MessagingParticipantTypes.Agent || value == MessagingParticipantTypes.Client;

    private static bool TryNormalizeRecipientScope(
        MessagingActor actor,
        string? recipientScope,
        out string? normalizedScope)
    {
        normalizedScope = NormalizeOptional(recipientScope)?.ToLowerInvariant() switch
        {
            null => null,
            "agents" => MessagingRecipientScopes.Agents,
            "clients" => MessagingRecipientScopes.Clients,
            "leads" => MessagingRecipientScopes.Leads,
            _ => string.Empty
        };

        return actor.ParticipantType == MessagingParticipantTypes.Agent
            ? normalizedScope is null or MessagingRecipientScopes.Agents or MessagingRecipientScopes.Clients or MessagingRecipientScopes.Leads
            : normalizedScope is null or MessagingRecipientScopes.Agents or MessagingRecipientScopes.Clients;
    }

    private static bool IsSupportedScanStatus(string value) =>
        value == MessagingAttachmentScanStatuses.Scanning ||
        value == MessagingAttachmentScanStatuses.Clean ||
        value == MessagingAttachmentScanStatuses.Rejected;

    private static bool IsAllowedScanTransition(string current, string requested) =>
        (string.Equals(current, MessagingAttachmentScanStatuses.Pending, StringComparison.OrdinalIgnoreCase) &&
         requested == MessagingAttachmentScanStatuses.Scanning) ||
        (string.Equals(current, MessagingAttachmentScanStatuses.Scanning, StringComparison.OrdinalIgnoreCase) &&
         (requested == MessagingAttachmentScanStatuses.Clean || requested == MessagingAttachmentScanStatuses.Rejected));

    private static MessagingParticipantSummary ToParticipantSummary(
        ParticipantRow participant,
        IReadOnlyDictionary<(string UserId, string ParticipantType), string> displayNames)
    {
        return new MessagingParticipantSummary(
            participant.UserId,
            participant.ParticipantType,
            displayNames.TryGetValue((NormalizeUserId(participant.UserId), participant.ParticipantType), out var displayName)
                ? displayName
                : participant.ParticipantType);
    }

    private static MessagingMessageSummary ToMessageSummary(
        MessageDetailRow message,
        IReadOnlyCollection<AttachmentRow> attachments)
    {
        return new MessagingMessageSummary(
            message.Id,
            message.ConversationId,
            message.SenderUserId,
            message.SenderType,
            message.Body,
            message.SentUtc,
            message.EditedUtc,
            message.IsDeleted,
            attachments.Where(x => x.InternalMessageId == message.Id).Select(ToAttachmentSummary).ToList(),
            message.ReplyToMessageId,
            message.Reply is null
                ? null
                : new MessagingReplyPreview(
                    message.Reply.Id,
                    message.Reply.SenderUserId,
                    message.Reply.SenderType,
                    message.Reply.IsDeleted
                        ? "Message unavailable"
                        : message.Reply.Body,
                    message.Reply.IsDeleted));
    }

    private static MessagingAttachmentSummary ToAttachmentSummary(AttachmentRow attachment) => new(
        attachment.Id,
        attachment.OriginalFileName,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.ScanStatus,
        attachment.CreatedUtc,
        string.Equals(attachment.ScanStatus, MessagingAttachmentScanStatuses.Clean, StringComparison.OrdinalIgnoreCase));

    private static MessagingAttachmentSummary ToAttachmentSummary(MessageAttachment attachment) => new(
        attachment.Id,
        attachment.OriginalFileName,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.ScanStatus,
        attachment.CreatedUtc,
        string.Equals(attachment.ScanStatus, MessagingAttachmentScanStatuses.Clean, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static bool IsSameParticipant(
        string? leftUserId,
        string? leftParticipantType,
        string? rightUserId,
        string? rightParticipantType) =>
        string.Equals(NormalizeUserId(leftUserId), NormalizeUserId(rightUserId), StringComparison.Ordinal) &&
        string.Equals(NormalizeRequired(leftParticipantType), NormalizeRequired(rightParticipantType), StringComparison.Ordinal);

    private static string BuildDirectParticipantKey(string? userId, string? participantType)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedParticipantType = NormalizeRequired(participantType);
        return $"{normalizedParticipantType.Length}:{normalizedParticipantType}{normalizedUserId.Length}:{normalizedUserId}";
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeUserId(string? value) => NormalizeRequired(value).ToLowerInvariant();

    private static bool Fits(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private static string Preview(string body) => body.Length <= 160 ? body : $"{body[..157]}...";

    private const string RespectfulCommunicationMessage = "Message not sent. Legend Legacy Protection requires respectful communication. Please remove vulgar, abusive, threatening, hateful, or inappropriate language before sending.";

    private sealed record ConversationRow(
        Guid Id,
        string ConversationType,
        string? Subject,
        DateTime? LastMessageUtc,
        bool IsClosed);

    private sealed record DirectConversationParticipant(
        string UserId,
        string ParticipantType);

    private sealed record ExistingConversationRow(
        Guid Id,
        string UserId,
        string ParticipantType);

    private sealed record ConversationDetailRow(
        Guid Id,
        string ConversationType,
        string? Subject,
        DateTime CreatedUtc,
        DateTime? LastMessageUtc,
        bool IsClosed);

    private sealed record ParticipantRow(
        Guid ConversationId,
        string UserId,
        string ParticipantType,
        DateTime? LastReadUtc,
        bool IsMuted);

    private sealed record MessageListRow(
        Guid Id,
        Guid ConversationId,
        string SenderUserId,
        string SenderType,
        string Body,
        DateTime SentUtc,
        bool IsDeleted);

    private sealed record MessageDetailRow(
        Guid Id,
        Guid ConversationId,
        string SenderUserId,
        string SenderType,
        string Body,
        DateTime SentUtc,
        DateTime? EditedUtc,
        bool IsDeleted,
        Guid? ReplyToMessageId,
        ReplyDetailRow? Reply);

    private sealed record ReplyDetailRow(
        Guid Id,
        string SenderUserId,
        string SenderType,
        string Body,
        bool IsDeleted);

    private sealed record AttachmentRow(
        Guid Id,
        Guid InternalMessageId,
        string OriginalFileName,
        string ContentType,
        long SizeBytes,
        string ScanStatus,
        DateTime CreatedUtc);

    private sealed record RecipientAgentRow(string UserId, string? FullName, string? Email);

    private sealed record RecipientClientRow(string UserId, string? FirstName, string? LastName, string? Email, string? CrmNotes, string? CrmStatus);
}
