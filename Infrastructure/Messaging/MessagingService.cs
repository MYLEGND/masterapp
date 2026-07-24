using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
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

    public MessagingService(MasterAppDbContext db, ILogger<MessagingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MessagingConversationListResult> ListConversationsAsync(
        MessagingActor actor,
        MessagingConversationListQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationListResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var take = Math.Clamp(query.Take, 1, 100);
        var search = NormalizeOptional(query.Search);
        if (!Fits(search, MaximumConversationSubjectLength))
            return MessagingConversationListResult.Failure("MESSAGING_SEARCH_INVALID", "The conversation search text is too long.");

        var conversationsQuery = AuthorizedConversationsQuery(actor);
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
                x.Body,
                x.SentUtc,
                x.IsDeleted))
            .ToListAsync(cancellationToken);

        var displayNames = await LoadDisplayNamesAsync(participants, cancellationToken);
        var actorUserId = NormalizeRequired(actor.UserId);
        var result = new List<MessagingConversationSummary>(conversations.Count);
        foreach (var conversation in conversations)
        {
            var conversationParticipants = participants
                .Where(x => x.ConversationId == conversation.Id)
                .ToList();
            var currentParticipant = conversationParticipants.FirstOrDefault(x =>
                string.Equals(x.UserId, actorUserId, StringComparison.OrdinalIgnoreCase));
            if (currentParticipant is null)
                continue;

            var counterparty = conversationParticipants.FirstOrDefault(x =>
                !string.Equals(x.UserId, actorUserId, StringComparison.OrdinalIgnoreCase));
            if (counterparty is null)
                continue;

            var conversationMessages = messages
                .Where(x => x.ConversationId == conversation.Id)
                .OrderByDescending(x => x.SentUtc)
                .ToList();
            var unreadCount = conversationMessages.Count(x =>
                !x.IsDeleted &&
                !string.Equals(x.SenderUserId, actorUserId, StringComparison.OrdinalIgnoreCase) &&
                (!currentParticipant.LastReadUtc.HasValue || x.SentUtc > currentParticipant.LastReadUtc.Value));
            var latest = conversationMessages.FirstOrDefault(x => !x.IsDeleted);

            result.Add(new MessagingConversationSummary(
                conversation.Id,
                conversation.ConversationType,
                conversation.Subject,
                conversation.LastMessageUtc,
                conversation.IsClosed,
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
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await AuthorizedConversationsQuery(actor)
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
                x.IsDeleted))
            .ToListAsync(cancellationToken);
        var displayNames = await LoadDisplayNamesAsync(participants, cancellationToken);
        var currentParticipant = participants.FirstOrDefault(x =>
            string.Equals(x.UserId, NormalizeRequired(actor.UserId), StringComparison.OrdinalIgnoreCase));

        var detail = new MessagingConversationDetail(
            conversation.Id,
            conversation.ConversationType,
            conversation.Subject,
            conversation.CreatedUtc,
            conversation.LastMessageUtc,
            conversation.IsClosed,
            currentParticipant?.IsMuted == true,
            participants.Select(x => ToParticipantSummary(x, displayNames)).ToList(),
            messages.Select(message => ToMessageSummary(message, attachments)).ToList());

        return new MessagingConversationResult(true, null, null, detail);
    }

    public async Task<MessagingRecipientListResult> ListRecipientsAsync(
        MessagingActor actor,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingRecipientListResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        search = NormalizeOptional(search);
        if (!Fits(search, MaximumConversationSubjectLength))
            return MessagingRecipientListResult.Failure("MESSAGING_SEARCH_INVALID", "The recipient search text is too long.");

        var results = await ListAuthorizedRecipientsAsync(actor, cancellationToken);
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

        if (string.Equals(actor.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actor.ParticipantType, normalizedParticipantType, StringComparison.Ordinal))
        {
            var ownParticipant = await GetParticipantSummaryAsync(actor.UserId, actor.ParticipantType, cancellationToken);
            return ownParticipant is null
                ? MessagingRecipientResult.Failure("MESSAGING_RECIPIENT_NOT_FOUND", "The requested participant is not available.")
                : new MessagingRecipientResult(true, null, null, ownParticipant);
        }

        var participant = (await ListAuthorizedRecipientsAsync(actor, cancellationToken))
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
            !Fits(clientMessageId, MaximumClientMessageIdLength) ||
            string.Equals(actor.UserId, targetUserId, StringComparison.OrdinalIgnoreCase))
        {
            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_INVALID", "The requested conversation is invalid.");
        }

        var conversationType = GetConversationType(actor.ParticipantType, targetParticipantType);
        if (conversationType is null || !await IsPermittedPairAsync(actor, targetUserId, targetParticipantType, cancellationToken))
            return MessagingConversationResult.Failure("MESSAGING_RECIPIENT_FORBIDDEN", "Messaging is not permitted for the requested recipient.");

        var directConversationKey = BuildDirectConversationKey(conversationType, actor.UserId, targetUserId);

        var existing = await FindDirectConversationAsync(
            actor.UserId,
            targetUserId,
            conversationType,
            directConversationKey,
            cancellationToken);
        if (existing is not null)
            return await ContinueExistingConversationAsync(actor, existing.Id, initialMessage, clientMessageId, cancellationToken);

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
        _db.MessageConversationParticipants.AddRange(
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = actor.UserId,
                ParticipantType = actor.ParticipantType,
                IsActive = true,
                JoinedUtc = nowUtc
            },
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = targetUserId,
                ParticipantType = targetParticipantType,
                IsActive = true,
                JoinedUtc = nowUtc
            });

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
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Messaging conversation creation failed. ActorUserId={ActorUserId} TargetUserId={TargetUserId}", actor.UserId, targetUserId);
            _db.ChangeTracker.Clear();
            var concurrent = await FindDirectConversationAsync(
                actor.UserId,
                targetUserId,
                conversationType,
                directConversationKey,
                cancellationToken);
            if (concurrent is not null)
                return await ContinueExistingConversationAsync(actor, concurrent.Id, initialMessage, clientMessageId, cancellationToken);

            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_SAVE_FAILED", "The conversation could not be saved.");
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

        var conversation = await AuthorizedConversationsQuery(actor)
            .FirstOrDefaultAsync(x => x.Id == command.ConversationId, cancellationToken);
        if (conversation is null)
            return MessagingMessageResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");
        if (conversation.IsClosed)
            return MessagingMessageResult.Failure("MESSAGING_CONVERSATION_CLOSED", "Closed conversations cannot receive new messages.");

        if (!string.IsNullOrWhiteSpace(clientMessageId))
        {
            var duplicate = await _db.InternalMessages
                .AsNoTracking()
                .Where(x => x.ClientMessageId == clientMessageId)
                .Select(x => new { x.Id, x.ConversationId, x.SenderUserId, x.SenderType, x.Body, x.SentUtc, x.EditedUtc, x.IsDeleted })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                if (duplicate.ConversationId == conversation.Id &&
                    string.Equals(duplicate.SenderUserId, actor.UserId, StringComparison.OrdinalIgnoreCase))
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
                            Array.Empty<MessagingAttachmentSummary>()),
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
            ClientMessageId = clientMessageId
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
                Array.Empty<MessagingAttachmentSummary>()),
            conversation.Id);
    }

    public async Task<MessagingOperationResult> MarkConversationReadAsync(
        MessagingConversationActionCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await AuthorizedConversationsQuery(actor)
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

        var conversation = await AuthorizedConversationsQuery(actor)
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

        var message = await _db.InternalMessages
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == command.InternalMessageId, cancellationToken);
        if (message is null || message.IsDeleted ||
            !string.Equals(message.SenderUserId, actor.UserId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(message.SenderType, actor.ParticipantType, StringComparison.OrdinalIgnoreCase))
        {
            return MessagingAttachmentResult.Failure("MESSAGING_ATTACHMENT_FORBIDDEN", "Attachments can only be added to your own messages.");
        }

        var authorized = await AuthorizedConversationsQuery(actor)
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

        var authorized = await AuthorizedConversationsQuery(actor)
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

    private IQueryable<MessageConversation> AuthorizedConversationsQuery(MessagingActor actor)
    {
        var actorUserId = NormalizeRequired(actor.UserId);
        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        return _db.MessageConversations.Where(conversation =>
            conversation.Participants.Any(participant =>
                participant.IsActive &&
                participant.UserId.ToLower() == actorUserId &&
                participant.ParticipantType == actorParticipantType) &&
            (
                (conversation.ConversationType == MessagingConversationTypes.AgentDirect &&
                 conversation.Participants.All(participant =>
                     participant.ParticipantType == MessagingParticipantTypes.Agent &&
                     participant.IsActive &&
                     _db.AgentProfiles.Any(profile =>
                         profile.IsActive && profile.AgentUserId.ToLower() == participant.UserId.ToLower()))) ||
                (conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
                 conversation.Participants.Where(participant =>
                         participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                     .Any(client => conversation.Participants.Where(participant =>
                             participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Agent)
                         .Any(agent =>
                             _db.AgentClients.Any(link =>
                                 link.ClientUserId.ToLower() == client.UserId.ToLower() &&
                                 link.AgentUserId.ToLower() == agent.UserId.ToLower()) ||
                             _db.ClientAgentMessagingGrants.Any(grant =>
                                 grant.IsActive &&
                                 grant.ClientUserId.ToLower() == client.UserId.ToLower() &&
                                 grant.AgentUserId.ToLower() == agent.UserId.ToLower()))))
            ));
    }

    private async Task<MessageConversationParticipant?> FindAuthorizedParticipantAsync(
        MessagingActor actor,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var isAuthorized = await AuthorizedConversationsQuery(actor)
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
        string actorUserId,
        string targetUserId,
        string conversationType,
        string directConversationKey,
        CancellationToken cancellationToken)
    {
        return await _db.MessageConversations
            .Where(x => x.ConversationType == conversationType)
            .Where(x => x.DirectConversationKey == directConversationKey ||
                        (x.DirectConversationKey == null &&
                         x.Participants.Count(participant => participant.IsActive) == 2 &&
                         x.Participants.Any(participant => participant.IsActive && participant.UserId.ToLower() == actorUserId) &&
                         x.Participants.Any(participant => participant.IsActive && participant.UserId.ToLower() == targetUserId)))
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

        var isAssistant = await _db.AgentAssistants
            .AsNoTracking()
            .AnyAsync(x => x.AssistantUserId != null && x.AssistantUserId.ToLower() == normalizedActor.UserId, cancellationToken);
        if (isAssistant)
            return false;

        return normalizedActor.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await _db.AgentProfiles.AsNoTracking().AnyAsync(
                x => x.IsActive && x.AgentUserId.ToLower() == normalizedActor.UserId,
                cancellationToken),
            MessagingParticipantTypes.Client => await _db.ClientProfiles.AsNoTracking().AnyAsync(
                x => x.ClientUserId.ToLower() == normalizedActor.UserId ||
                     (x.ExternalIdentityObjectId != null && x.ExternalIdentityObjectId.ToLower() == normalizedActor.UserId),
                cancellationToken),
            _ => false
        };
    }

    private async Task<bool> IsPermittedPairAsync(
        MessagingActor actor,
        string targetUserId,
        string targetParticipantType,
        CancellationToken cancellationToken)
    {
        if (actor.ParticipantType == MessagingParticipantTypes.Agent &&
            targetParticipantType == MessagingParticipantTypes.Agent)
        {
            return await _db.AgentProfiles.AsNoTracking().AnyAsync(
                x => x.IsActive && x.AgentUserId.ToLower() == targetUserId,
                cancellationToken);
        }

        if (actor.ParticipantType == MessagingParticipantTypes.Client &&
            targetParticipantType == MessagingParticipantTypes.Agent)
        {
            return await HasClientAgentMessagingPermissionAsync(actor.UserId, targetUserId, cancellationToken);
        }

        if (actor.ParticipantType == MessagingParticipantTypes.Agent &&
            targetParticipantType == MessagingParticipantTypes.Client)
        {
            return await HasClientAgentMessagingPermissionAsync(targetUserId, actor.UserId, cancellationToken);
        }

        return false;
    }

    private Task<List<MessagingRecipientSummary>> ListAuthorizedRecipientsAsync(
        MessagingActor actor,
        CancellationToken cancellationToken) =>
        actor.ParticipantType == MessagingParticipantTypes.Agent
            ? ListAgentRecipientsAsync(actor.UserId, cancellationToken)
            : ListClientRecipientsAsync(actor.UserId, cancellationToken);

    private async Task<MessagingRecipientSummary?> GetParticipantSummaryAsync(
        string userId,
        string participantType,
        CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeUserId(userId);
        if (participantType == MessagingParticipantTypes.Agent)
        {
            var agent = await _db.AgentProfiles
                .AsNoTracking()
                .Where(x => x.IsActive && x.AgentUserId.ToLower() == normalizedUserId)
                .Select(x => new RecipientAgentRow(x.AgentUserId, x.FullName, x.AgentUpn))
                .FirstOrDefaultAsync(cancellationToken);
            return agent is null
                ? null
                : new MessagingRecipientSummary(
                    agent.UserId,
                    MessagingParticipantTypes.Agent,
                    FirstNonEmpty(agent.FullName, agent.Email, "Agent"),
                    agent.Email);
        }

        if (participantType == MessagingParticipantTypes.Client)
        {
            var client = await _db.ClientProfiles
                .AsNoTracking()
                .Where(x => x.ClientUserId.ToLower() == normalizedUserId ||
                            (x.ExternalIdentityObjectId != null && x.ExternalIdentityObjectId.ToLower() == normalizedUserId))
                .Select(x => new RecipientClientRow(x.ClientUserId, x.FirstName, x.LastName, x.Email))
                .FirstOrDefaultAsync(cancellationToken);
            return client is null
                ? null
                : new MessagingRecipientSummary(
                    client.UserId,
                    MessagingParticipantTypes.Client,
                    FirstNonEmpty($"{client.FirstName} {client.LastName}".Trim(), client.Email, "Client"),
                    client.Email);
        }

        return null;
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
        CancellationToken cancellationToken)
    {
        var agentRows = await _db.AgentProfiles.AsNoTracking()
            .Where(x => x.IsActive && x.AgentUserId.ToLower() != agentUserId)
            .Where(x => !_db.AgentAssistants.Any(assistant =>
                assistant.AssistantUserId != null &&
                assistant.AssistantUserId.ToLower() == x.AgentUserId.ToLower()))
            .Select(x => new RecipientAgentRow(x.AgentUserId, x.FullName, x.AgentUpn))
            .ToListAsync(cancellationToken);

        var linkedClientIds = await _db.AgentClients.AsNoTracking()
            .Where(x => x.AgentUserId.ToLower() == agentUserId)
            .Select(x => x.ClientUserId.ToLower())
            .Union(_db.ClientAgentMessagingGrants.AsNoTracking()
                .Where(x => x.IsActive && x.AgentUserId.ToLower() == agentUserId)
                .Select(x => x.ClientUserId.ToLower()))
            .Distinct()
            .ToListAsync(cancellationToken);
        var clientRows = await _db.ClientProfiles.AsNoTracking()
            .Where(x => linkedClientIds.Contains(x.ClientUserId.ToLower()))
            .Select(x => new RecipientClientRow(x.ClientUserId, x.FirstName, x.LastName, x.Email))
            .ToListAsync(cancellationToken);

        var agents = agentRows.Select(x => new MessagingRecipientSummary(
            x.UserId,
            MessagingParticipantTypes.Agent,
            FirstNonEmpty(x.FullName, x.Email, "Agent"),
            x.Email));
        var clients = clientRows.Select(x => new MessagingRecipientSummary(
            x.UserId,
            MessagingParticipantTypes.Client,
            FirstNonEmpty($"{x.FirstName} {x.LastName}".Trim(), x.Email, "Client"),
            x.Email));
        return agents.Concat(clients)
            .OrderBy(x => x.ParticipantType)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<MessagingRecipientSummary>> ListClientRecipientsAsync(
        string clientUserId,
        CancellationToken cancellationToken)
    {
        var agentIds = await _db.AgentClients.AsNoTracking()
            .Where(x => x.ClientUserId.ToLower() == clientUserId)
            .Select(x => x.AgentUserId.ToLower())
            .Union(_db.ClientAgentMessagingGrants.AsNoTracking()
                .Where(x => x.IsActive && x.ClientUserId.ToLower() == clientUserId)
                .Select(x => x.AgentUserId.ToLower()))
            .Distinct()
            .ToListAsync(cancellationToken);

        var agentRows = await _db.AgentProfiles.AsNoTracking()
            .Where(x => x.IsActive && agentIds.Contains(x.AgentUserId.ToLower()))
            .Where(x => !_db.AgentAssistants.Any(assistant =>
                assistant.AssistantUserId != null &&
                assistant.AssistantUserId.ToLower() == x.AgentUserId.ToLower()))
            .Select(x => new RecipientAgentRow(x.AgentUserId, x.FullName, x.AgentUpn))
            .ToListAsync(cancellationToken);

        return agentRows.Select(x => new MessagingRecipientSummary(
                x.UserId,
                MessagingParticipantTypes.Agent,
                FirstNonEmpty(x.FullName, x.Email, "Agent"),
                x.Email))
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> HasClientAgentMessagingPermissionAsync(
        string clientUserId,
        string agentUserId,
        CancellationToken cancellationToken)
    {
        var linked = await _db.AgentClients.AsNoTracking().AnyAsync(
            link => link.ClientUserId.ToLower() == clientUserId && link.AgentUserId.ToLower() == agentUserId,
            cancellationToken);
        if (linked)
            return true;

        return await _db.ClientAgentMessagingGrants.AsNoTracking().AnyAsync(
            grant => grant.IsActive &&
                     grant.ClientUserId.ToLower() == clientUserId &&
                     grant.AgentUserId.ToLower() == agentUserId,
            cancellationToken);
    }

    private async Task<Dictionary<(string UserId, string ParticipantType), string>> LoadDisplayNamesAsync(
        IReadOnlyCollection<ParticipantRow> participants,
        CancellationToken cancellationToken)
    {
        var agentIds = participants
            .Where(x => x.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(x => x.UserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var clientIds = participants
            .Where(x => x.ParticipantType == MessagingParticipantTypes.Client)
            .Select(x => x.UserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var agentNames = await _db.AgentProfiles.AsNoTracking()
            .Where(x => agentIds.Contains(x.AgentUserId.ToLower()))
            .Select(x => new { x.AgentUserId, x.FullName, x.AgentUpn })
            .ToListAsync(cancellationToken);
        var clientNames = await _db.ClientProfiles.AsNoTracking()
            .Where(x => clientIds.Contains(x.ClientUserId.ToLower()) ||
                        (x.ExternalIdentityObjectId != null && clientIds.Contains(x.ExternalIdentityObjectId.ToLower())))
            .Select(x => new { x.ClientUserId, x.ExternalIdentityObjectId, x.FirstName, x.LastName, x.Email })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<(string UserId, string ParticipantType), string>();
        foreach (var agent in agentNames)
        {
            result[(agent.AgentUserId, MessagingParticipantTypes.Agent)] = FirstNonEmpty(agent.FullName, agent.AgentUpn, "Agent");
        }

        foreach (var client in clientNames)
        {
            var displayName = FirstNonEmpty($"{client.FirstName} {client.LastName}".Trim(), client.Email, "Client");
            result[(client.ClientUserId, MessagingParticipantTypes.Client)] = displayName;
            if (!string.IsNullOrWhiteSpace(client.ExternalIdentityObjectId))
                result[(client.ExternalIdentityObjectId, MessagingParticipantTypes.Client)] = displayName;
        }

        return result;
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

        return null;
    }

    private static string BuildDirectConversationKey(string conversationType, string firstUserId, string secondUserId)
    {
        var participants = new[] { NormalizeUserId(firstUserId), NormalizeUserId(secondUserId) };
        Array.Sort(participants, StringComparer.Ordinal);
        return $"{conversationType}|{participants[0]}|{participants[1]}";
    }

    private static bool IsParticipantType(string value) =>
        value == MessagingParticipantTypes.Agent || value == MessagingParticipantTypes.Client;

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
            displayNames.TryGetValue((participant.UserId, participant.ParticipantType), out var displayName)
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
            attachments.Where(x => x.InternalMessageId == message.Id).Select(ToAttachmentSummary).ToList());
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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeUserId(string? value) => NormalizeRequired(value).ToLowerInvariant();

    private static bool Fits(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private static string Preview(string body) => body.Length <= 160 ? body : $"{body[..157]}...";

    private sealed record ConversationRow(
        Guid Id,
        string ConversationType,
        string? Subject,
        DateTime? LastMessageUtc,
        bool IsClosed);

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

    private sealed record RecipientClientRow(string UserId, string? FirstName, string? LastName, string? Email);
}
