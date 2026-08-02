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
    private const int MaximumResolutionNoteLength = 1_000;
    private const int MaximumGroupImageBytes = 512 * 1_024;
    private const int MaximumPinnedConversations = 6;

    private readonly MasterAppDbContext _db;
    private readonly ILogger<MessagingService> _logger;
    private readonly ICommunityTextModerationService _moderation;
    private readonly IMessagingProfileImageResolver _participantIdentities;
    private readonly IControlledResourceAccessService _controlledResources;
    private readonly ITranslationService _translation;

    public MessagingService(
        MasterAppDbContext db,
        ILogger<MessagingService> logger,
        ICommunityTextModerationService moderation,
        IMessagingProfileImageResolver participantIdentities,
        IControlledResourceAccessService controlledResources,
        ITranslationService translation)
    {
        _db = db;
        _logger = logger;
        _moderation = moderation;
        _participantIdentities = participantIdentities;
        _controlledResources = controlledResources;
        _translation = translation;
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

        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var conversationsQuery = await AuthorizedConversationsQueryAsync(actor, cancellationToken);

        // Removing a conversation is actor-scoped. It remains available to the
        // other members, and returns to this inbox if a newer message arrives.
        conversationsQuery = conversationsQuery.Where(conversation =>
            conversation.Participants.Any(participant =>
                participant.IsActive &&
                actorUserIds.Contains(participant.UserId.ToLower()) &&
                participant.ParticipantType == actorParticipantType &&
                (participant.HiddenUtc == null ||
                 conversation.Messages.Any(message =>
                     !message.IsDeleted && message.SentUtc > participant.HiddenUtc))));

        conversationsQuery = conversationsQuery.Where(conversation =>
            conversation.Messages.Any(message => !message.IsDeleted));

        if (!query.IncludeClosed)
            conversationsQuery = conversationsQuery.Where(x => !x.IsClosed);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchPattern = $"%{search}%";
            conversationsQuery = conversationsQuery.Where(x =>
                (x.Subject != null && EF.Functions.Like(x.Subject, searchPattern)) ||
                x.Messages.Any(message => !message.IsDeleted && EF.Functions.Like(message.Body, searchPattern)));
        }

        // The actor's active participant row is the source of truth for inbox
        // controls such as pinning. Joining it directly keeps the existing
        // authorization query intact and produces a simple, portable SQL ORDER BY.
        // Ordering a projected record containing a correlated FirstOrDefault was
        // not translatable by the production SQL Server provider.
        var conversationRows = await (
                from conversation in conversationsQuery
                join participant in _db.MessageConversationParticipants.AsNoTracking()
                    on conversation.Id equals participant.ConversationId
                where participant.IsActive &&
                      actorUserIds.Contains(participant.UserId.ToLower()) &&
                      participant.ParticipantType == actorParticipantType
                select new
                {
                    conversation.Id,
                    conversation.ConversationType,
                    conversation.Subject,
                    conversation.LastMessageUtc,
                    conversation.IsClosed,
                    conversation.Purpose,
                    conversation.GroupImageContent,
                    conversation.GroupImageContentType,
                    participant.PinnedUtc
                })
            .OrderByDescending(x => x.PinnedUtc.HasValue)
            .ThenByDescending(x => x.PinnedUtc)
            .ThenByDescending(x => x.LastMessageUtc ?? DateTime.MinValue)
            .Take(take)
            .ToListAsync(cancellationToken);

        var conversations = conversationRows
            .Select(x => new ConversationRow(
                x.Id,
                x.ConversationType,
                x.Subject,
                x.LastMessageUtc,
                x.IsClosed,
                x.Purpose,
                x.GroupImageContent,
                x.GroupImageContentType,
                x.PinnedUtc))
            .ToList();

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
                x.IsMuted,
                x.PinnedUtc,
                x.HiddenUtc))
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
        var result = new List<MessagingConversationSummary>(conversations.Count);
        foreach (var conversation in conversations)
        {
            var conversationParticipants = participants
                .Where(x => x.ConversationId == conversation.Id)
                .ToList();
            var currentParticipant = conversationParticipants.FirstOrDefault(x =>
                IsCurrentActor(x.UserId, x.ParticipantType, actorUserIds, actorParticipantType));
            if (currentParticipant is null)
                continue;

            var counterparty = conversationParticipants.FirstOrDefault(x =>
                !IsCurrentActor(x.UserId, x.ParticipantType, actorUserIds, actorParticipantType));
            if (counterparty is null)
                continue;

            var conversationMessages = messages
                .Where(x => x.ConversationId == conversation.Id)
                .OrderByDescending(x => x.SentUtc)
                .ToList();
            var unreadCount = conversationMessages.Count(x =>
                !x.IsDeleted &&
                !IsCurrentActor(x.SenderUserId, x.SenderType, actorUserIds, actorParticipantType) &&
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
                latest is null ? null : Preview(latest.Body),
                conversation.Purpose,
                ToGroupImage(conversation.GroupImageContent, conversation.GroupImageContentType),
                currentParticipant.PinnedUtc.HasValue,
                currentParticipant.IsMuted));
        }

        // Legacy data can contain more than one direct conversation for the
        // same typed counterparty. The direct-conversation key prevents new
        // duplicates, while this projection keeps every client authoritative
        // and stable until legacy rows are repaired.
        var canonicalConversations = result
            .GroupBy(conversation => conversation.ConversationType == MessagingConversationTypes.Group
                ? $"group:{conversation.Id:D}"
                : $"person:{NormalizeUserId(conversation.Counterparty.UserId)}:{NormalizeRequired(conversation.Counterparty.ParticipantType)}")
            .Select(group =>
            {
                var canonical = group
                    .OrderByDescending(conversation => conversation.IsPinned)
                    .ThenByDescending(conversation => conversation.LastMessageUtc ?? DateTime.MinValue)
                    .ThenByDescending(conversation => conversation.Id)
                    .First();

                return canonical with
                {
                    UnreadCount = group.Sum(conversation => conversation.UnreadCount),
                    IsPinned = group.Any(conversation => conversation.IsPinned)
                };
            })
            .OrderByDescending(conversation => conversation.IsPinned)
            .ThenByDescending(conversation => conversation.LastMessageUtc ?? DateTime.MinValue)
            .ThenByDescending(conversation => conversation.Id)
            .ToArray();

        return new MessagingConversationListResult(
            true,
            null,
            null,
            canonicalConversations);
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
                x.IsClosed,
                x.OwnerUserId,
                x.OwnerParticipantType,
                x.Purpose,
                x.GroupImageContent,
                x.GroupImageContentType))
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
            return MessagingConversationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        var participants = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.IsActive)
            .Select(x => new ParticipantRow(
                x.ConversationId,
                x.UserId,
                x.ParticipantType,
                x.LastReadUtc,
                x.IsMuted,
                x.PinnedUtc,
                x.HiddenUtc))
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
                x.OriginalLanguage,
                x.SentUtc,
                x.EditedUtc,
                x.IsDeleted,
                x.ReplyToMessageId,
                x.VerificationReviewRequestId,
                x.ReplyToMessage == null
                    ? null
                    : new ReplyDetailRow(
                        x.ReplyToMessage.Id,
                        x.ReplyToMessage.SenderUserId,
                        x.ReplyToMessage.SenderType,
                        x.ReplyToMessage.Body,
                        x.ReplyToMessage.IsDeleted)))
            .ToListAsync(cancellationToken);
        var messageParticipants = messages
            .Select(message => new MessagingParticipantReference(message.SenderUserId, message.SenderType))
            .Concat(messages
                .Where(message => message.Reply is not null)
                .Select(message => new MessagingParticipantReference(
                    message.Reply!.SenderUserId,
                    message.Reply.SenderType)))
            .ToArray();
        var displayNames = await LoadDisplayNamesAsync(
            participants,
            messageParticipants,
            cancellationToken);
        var reviewRequestIds = messages
            .Where(message => message.VerificationReviewRequestId.HasValue)
            .Select(message => message.VerificationReviewRequestId!.Value)
            .Distinct()
            .ToArray();
        var canResolveReview = conversation.Purpose == MessagingConversationPurposes.VerificationReview &&
            await IsFounderVerificationManagerAsync(actor, cancellationToken);
        var reviews = reviewRequestIds.Length == 0
            ? new Dictionary<Guid, MessagingVerificationReview>()
            : (await _db.VerificationReviewRequests
                .AsNoTracking()
                .Where(request => reviewRequestIds.Contains(request.Id))
                .Select(request => new MessagingVerificationReview(
                    request.Id,
                    request.RequesterUserId,
                    request.RequesterParticipantType,
                    request.Status,
                    request.RequestedUtc,
                    canResolveReview && request.Status == VerificationReviewStatuses.Pending,
                    request.ResourceType))
                .ToListAsync(cancellationToken))
                .ToDictionary(request => request.Id);
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var currentParticipant = participants.FirstOrDefault(x =>
            IsCurrentActor(x.UserId, x.ParticipantType, actorUserIds, actor.ParticipantType));
        var isArchivedMembership =
            conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
            !await ConversationHasActiveClientMembershipAsync(conversation.Id, cancellationToken);

        var messageSummaries = messages
            .Select(message => ToMessageSummary(message, attachments, reviews))
            .ToList();
        messageSummaries = await ApplyTranslationPresentationAsync(
            actor,
            messageSummaries,
            messages,
            cancellationToken);

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
            messageSummaries,
            conversation.ConversationType == MessagingConversationTypes.Group &&
            IsSameParticipant(
                conversation.OwnerUserId ?? string.Empty,
                conversation.OwnerParticipantType ?? string.Empty,
                actor.UserId,
                actor.ParticipantType),
            conversation.Purpose,
            ToGroupImage(conversation.GroupImageContent, conversation.GroupImageContentType));

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

        var existing = await FindOrClaimDirectConversationAsync(
            conversationType,
            directConversationKey,
            actor,
            targetUserId,
            targetParticipantType,
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

    public async Task<MessagingConversationResult> CreateGroupAsync(
        CreateMessagingGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var subject = NormalizeOptional(command.Subject);
        var initialMessage = NormalizeOptional(command.InitialMessageBody);
        var clientMessageId = NormalizeOptional(command.ClientMessageId);
        var requestedParticipants = command.Participants ?? Array.Empty<MessagingParticipantReference>();

        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (string.IsNullOrWhiteSpace(subject) ||
            !Fits(subject, MaximumConversationSubjectLength) ||
            !Fits(initialMessage, MaximumMessageBodyLength) ||
            !Fits(clientMessageId, MaximumClientMessageIdLength) ||
            requestedParticipants.Count < 2 || requestedParticipants.Count > 24)
        {
            return MessagingConversationResult.Failure("MESSAGING_GROUP_INVALID", "Choose a group name and at least two connections.");
        }

        var participants = requestedParticipants
            .Select(participant => new MessagingParticipantReference(
                NormalizeUserId(participant.UserId),
                NormalizeRequired(participant.ParticipantType)))
            .ToArray();
        if (participants.Any(participant =>
                string.IsNullOrWhiteSpace(participant.UserId) ||
                !Fits(participant.UserId, 450) ||
                !IsParticipantType(participant.ParticipantType)) ||
            participants.Any(participant => IsSameParticipant(
                participant.UserId,
                participant.ParticipantType,
                actor.UserId,
                actor.ParticipantType)) ||
            participants.Distinct().Count() != participants.Length)
        {
            return MessagingConversationResult.Failure("MESSAGING_GROUP_INVALID", "The group members are invalid.");
        }

        foreach (var participant in participants)
        {
            var authorized = await GetAuthorizedParticipantAsync(
                actor,
                participant.UserId,
                participant.ParticipantType,
                cancellationToken);
            if (!authorized.Succeeded)
            {
                return MessagingConversationResult.Failure(
                    "MESSAGING_RECIPIENT_FORBIDDEN",
                    "A selected member is no longer one of your available connections.");
            }
        }

        return await CreateGroupConversationCoreAsync(
            actor,
            participants,
            subject,
            initialMessage,
            clientMessageId,
            purpose: null,
            owner: new MessagingParticipantReference(actor.UserId, actor.ParticipantType),
            groupImage: command.GroupImage,
            cancellationToken: cancellationToken);
    }

    public async Task<MessagingVerificationRequestResult> StartVerificationRequestAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var result = await StartControlledResourceRequestAsync(
            new StartControlledResourceRequestCommand(actor, ControlledResourceTypes.VerificationBadge),
            cancellationToken);
        return result.Succeeded
            ? new MessagingVerificationRequestResult(true, null, null, result.Request)
            : MessagingVerificationRequestResult.Failure(
                result.ErrorCode ?? "MESSAGING_VERIFICATION_UNAVAILABLE",
                result.ErrorMessage ?? "Verification review is temporarily unavailable.");
    }

    public async Task<MessagingControlledResourceRequestResult> StartControlledResourceRequestAsync(
        StartControlledResourceRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var resourceType = NormalizeRequired(command.ResourceType);
        if (!ControlledResourceTypes.IsSupported(resourceType))
            return MessagingControlledResourceRequestResult.Failure("MESSAGING_RESOURCE_INVALID", "This Legend resource is not available.");
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingControlledResourceRequestResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var access = await _controlledResources.GetAccessAsync(actor, resourceType, cancellationToken);
        if (access.State == ControlledResourceAccessStates.Granted)
            return MessagingControlledResourceRequestResult.Failure(
                "MESSAGING_RESOURCE_ALREADY_GRANTED",
                $"{ControlledResourceDisplayName(resourceType)} is already enabled for this profile.");

        var existing = await FindPendingControlledResourceRequestAsync(actor, resourceType, cancellationToken);
        if (existing is not null)
            return new MessagingControlledResourceRequestResult(true, null, null, ToReview(existing));

        var reviewConversation = await GetOrCreateControlledResourceReviewConversationAsync(actor, cancellationToken);
        if (reviewConversation is null)
            return MessagingControlledResourceRequestResult.Failure(
                "MESSAGING_RESOURCE_REVIEW_UNAVAILABLE",
                "The private Legend review team is temporarily unavailable.");

        var nowUtc = DateTime.UtcNow;
        var request = new VerificationReviewRequest
        {
            Id = Guid.NewGuid(),
            ReviewConversationId = reviewConversation.Id,
            RequesterUserId = actor.UserId,
            RequesterParticipantType = actor.ParticipantType,
            ResourceType = resourceType,
            Status = VerificationReviewStatuses.Pending,
            RequestedUtc = nowUtc
        };
        _db.VerificationReviewRequests.Add(request);
        _db.InternalMessages.Add(new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = reviewConversation.Id,
            SenderUserId = actor.UserId,
            SenderType = actor.ParticipantType,
            Body = $"Requested {ControlledResourceDisplayName(resourceType)}.",
            SentUtc = nowUtc,
            VerificationReviewRequestId = request.Id
        });
        reviewConversation.LastMessageUtc = nowUtc;
        reviewConversation.UpdatedUtc = nowUtc;
        AddAudit(actor.UserId, "ControlledResourceRequested", reviewConversation.Id, null, actor.UserId, $"{resourceType}:{request.Id:D}", nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogWarning(exception, "Controlled resource request save conflicted. ActorUserId={ActorUserId} ResourceType={ResourceType}", actor.UserId, resourceType);
            var concurrent = await FindPendingControlledResourceRequestAsync(actor, resourceType, cancellationToken);
            if (concurrent is not null)
                return new MessagingControlledResourceRequestResult(true, null, null, ToReview(concurrent));
            return MessagingControlledResourceRequestResult.Failure(
                "MESSAGING_RESOURCE_SAVE_FAILED",
                "Legend could not submit your request. Please try again.");
        }

        return new MessagingControlledResourceRequestResult(true, null, null, ToReview(request));
    }

    public async Task<MessagingCommunicationLanguageListResult> ListCommunicationLanguagesAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return MessagingCommunicationLanguageListResult.Failure(
                "MESSAGING_ACTOR_INVALID",
                "Messaging is not available for this user.");
        }

        var access = await _controlledResources.GetAccessAsync(
            actor,
            ControlledResourceTypes.LanguageTranslation,
            cancellationToken);
        if (access.State != ControlledResourceAccessStates.Granted)
        {
            return MessagingCommunicationLanguageListResult.Failure(
                "MESSAGING_RESOURCE_ACCESS_REQUIRED",
                "Language Translation Access must be granted before choosing a language.");
        }

        return new MessagingCommunicationLanguageListResult(
            true,
            null,
            null,
            CommunicationLanguages.Supported);
    }

    public async Task<MessagingActivityNotificationListResult> ListActivityNotificationsAsync(
        MessagingActor actor,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return MessagingActivityNotificationListResult.Failure(
                "MESSAGING_ACTOR_INVALID",
                "Activity is not available for this user.");
        }

        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var notifications = await _db.MobileActivityNotifications
            .AsNoTracking()
            .Where(notification =>
                actorUserIds.Contains(notification.RecipientUserId.ToLower()) &&
                notification.RecipientParticipantType == actorParticipantType)
            .OrderByDescending(notification => notification.OccurredUtc)
            .ThenByDescending(notification => notification.Id)
            .Take(Math.Clamp(take, 1, 100))
            .Select(notification => new MessagingActivityNotification(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Detail,
                notification.OccurredUtc,
                notification.ControlledResourceRequestId))
            .ToListAsync(cancellationToken);

        return new MessagingActivityNotificationListResult(
            true,
            null,
            null,
            notifications);
    }

    public async Task<MessagingOperationResult> AddGroupParticipantAsync(
        AddMessagingGroupParticipantCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var userId = NormalizeUserId(command.UserId);
        var participantType = NormalizeRequired(command.ParticipantType);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.ConversationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(userId) ||
            !Fits(userId, 450) ||
            !IsParticipantType(participantType) ||
            IsSameParticipant(userId, participantType, actor.UserId, actor.ParticipantType))
        {
            return MessagingOperationResult.Failure("MESSAGING_GROUP_MEMBER_INVALID", "Choose an available connection.");
        }

        var conversation = await _db.MessageConversations
            .Include(candidate => candidate.Participants)
            .FirstOrDefaultAsync(candidate => candidate.Id == command.ConversationId, cancellationToken);
        if (conversation is null ||
            conversation.ConversationType != MessagingConversationTypes.Group ||
            !IsSameParticipant(
                conversation.OwnerUserId ?? string.Empty,
                conversation.OwnerParticipantType ?? string.Empty,
                actor.UserId,
                actor.ParticipantType))
        {
            return MessagingOperationResult.Failure("MESSAGING_GROUP_OWNER_REQUIRED", "Only the group owner can add members.");
        }

        var authorizedConversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == command.ConversationId, cancellationToken);
        if (!authorizedConversation)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        var isVerificationReview = conversation.Purpose == MessagingConversationPurposes.VerificationReview;
        if (isVerificationReview)
        {
            if (!await IsFounderVerificationManagerAsync(actor, cancellationToken) ||
                participantType != MessagingParticipantTypes.Agent ||
                !await IsActiveAgentAsync(userId, cancellationToken))
            {
                return MessagingOperationResult.Failure(
                    "MESSAGING_VERIFICATION_REVIEWER_FORBIDDEN",
                    "Only Zac Owen can add active Legend agents to the verification review group.");
            }
        }
        else
        {
            var authorizedRecipient = await GetAuthorizedParticipantAsync(
                actor,
                userId,
                participantType,
                cancellationToken);
            if (!authorizedRecipient.Succeeded)
            {
                return MessagingOperationResult.Failure(
                    "MESSAGING_RECIPIENT_FORBIDDEN",
                    "That member is no longer one of your available connections.");
            }
        }

        var nowUtc = DateTime.UtcNow;
        var member = conversation.Participants.FirstOrDefault(participant =>
            IsSameParticipant(participant.UserId, participant.ParticipantType, userId, participantType));
        if (member is not null && member.IsActive)
            return MessagingOperationResult.Success();

        if (member is null)
        {
            _db.MessageConversationParticipants.Add(new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = userId,
                ParticipantType = participantType,
                IsActive = true,
                JoinedUtc = nowUtc
            });
        }
        else
        {
            member.IsActive = true;
            member.JoinedUtc = nowUtc;
            member.LeftUtc = null;
        }

        conversation.UpdatedUtc = nowUtc;
        AddAudit(actor.UserId, "GroupMemberAdded", conversation.Id, null, userId, participantType, nowUtc);
        return await SaveOperationAsync("GroupMemberAdded", actor.UserId, conversation.Id, cancellationToken);
    }

    public async Task<MessagingOperationResult> UpdateGroupProfileAsync(
        UpdateMessagingGroupProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var subject = NormalizeOptional(command.Subject);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.ConversationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) ||
            !Fits(subject, MaximumConversationSubjectLength) || !IsValidGroupImage(command.GroupImage))
        {
            return MessagingOperationResult.Failure("MESSAGING_GROUP_PROFILE_INVALID", "Choose a valid group name and image.");
        }

        var conversation = await _db.MessageConversations.FirstOrDefaultAsync(
            candidate => candidate.Id == command.ConversationId,
            cancellationToken);
        if (conversation is null || conversation.ConversationType != MessagingConversationTypes.Group ||
            !IsSameParticipant(
                conversation.OwnerUserId,
                conversation.OwnerParticipantType,
                actor.UserId,
                actor.ParticipantType))
        {
            return MessagingOperationResult.Failure("MESSAGING_GROUP_OWNER_REQUIRED", "Only the group owner can edit this group.");
        }

        conversation.Subject = subject;
        if (command.GroupImage is not null)
        {
            conversation.GroupImageContent = command.GroupImage.Content;
            conversation.GroupImageContentType = command.GroupImage.ContentType;
        }
        conversation.UpdatedUtc = DateTime.UtcNow;
        AddAudit(actor.UserId, "GroupProfileUpdated", conversation.Id, null, null, null, conversation.UpdatedUtc);
        return await SaveOperationAsync("GroupProfileUpdated", actor.UserId, conversation.Id, cancellationToken);
    }

    public async Task<MessagingOperationResult> ResolveVerificationReviewRequestAsync(
        ResolveVerificationReviewRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        return await ResolveControlledResourceRequestAsync(
            new ResolveControlledResourceRequestCommand(
                command.Actor,
                command.RequestId,
                command.Approve,
                command.ResolutionNote),
            cancellationToken);
    }

    public async Task<MessagingOperationResult> ResolveControlledResourceRequestAsync(
        ResolveControlledResourceRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.RequestId == Guid.Empty || !await _controlledResources.IsFounderManagerAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_REVIEWER_FORBIDDEN", "Only the Founder can resolve this resource request.");

        var request = await _db.VerificationReviewRequests.FirstOrDefaultAsync(
            candidate => candidate.Id == command.RequestId,
            cancellationToken);
        if (request is null || request.Status != VerificationReviewStatuses.Pending || !ControlledResourceTypes.IsSupported(request.ResourceType))
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_REQUEST_UNAVAILABLE", "This resource request is no longer pending.");

        var reviewConversation = await _db.MessageConversations.FirstOrDefaultAsync(
            conversation => conversation.Id == request.ReviewConversationId &&
                conversation.Purpose == MessagingConversationPurposes.ControlledResourceReview,
            cancellationToken);
        if (reviewConversation is null)
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_REQUEST_UNAVAILABLE", "The resource review is no longer available.");

        if (command.Approve && !await SetResourceGrantCoreAsync(
                actor,
                request.ResourceType,
                request.RequesterUserId,
                request.RequesterParticipantType,
                isGranted: true,
                cancellationToken))
        {
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_PROFILE_UNAVAILABLE", "The requesting profile is no longer available.");
        }

        var nowUtc = DateTime.UtcNow;
        var resolutionNote = Truncate(
            NormalizeOptional(command.ResolutionNote),
            MaximumResolutionNoteLength);
        request.Status = command.Approve ? VerificationReviewStatuses.Approved : VerificationReviewStatuses.Declined;
        request.ResolvedUtc = nowUtc;
        request.ResolvedByUserId = actor.UserId;
        request.ResolutionNote = resolutionNote;
        _db.MobileActivityNotifications.Add(CreateControlledResourceOutcomeNotification(
            request,
            command.Approve,
            resolutionNote,
            nowUtc));
        _db.InternalMessages.Add(new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = reviewConversation.Id,
            SenderUserId = actor.UserId,
            SenderType = actor.ParticipantType,
            Body = command.Approve
                ? $"{ControlledResourceDisplayName(request.ResourceType)} approved."
                : $"{ControlledResourceDisplayName(request.ResourceType)} declined.",
            SentUtc = nowUtc,
            VerificationReviewRequestId = request.Id
        });
        reviewConversation.LastMessageUtc = nowUtc;
        reviewConversation.UpdatedUtc = nowUtc;
        AddAudit(actor.UserId, command.Approve ? "ControlledResourceApproved" : "ControlledResourceDeclined", reviewConversation.Id, null, request.RequesterUserId, $"{request.ResourceType}:{request.Id:D}", nowUtc);
        return await SaveOperationAsync("ControlledResourceRequestResolved", actor.UserId, reviewConversation.Id, cancellationToken);
    }

    public async Task<MessagingControlledResourceRecipientListResult> ListControlledResourceRecipientsAsync(
        MessagingActor actor,
        string resourceType,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        resourceType = NormalizeRequired(resourceType);
        if (!ControlledResourceTypes.IsSupported(resourceType))
            return MessagingControlledResourceRecipientListResult.Failure("MESSAGING_RESOURCE_INVALID", "This Legend resource is not available.");
        if (!await IsValidActorAsync(actor, cancellationToken) ||
            !await _controlledResources.IsFounderManagerAsync(actor, cancellationToken))
        {
            return MessagingControlledResourceRecipientListResult.Failure("MESSAGING_RESOURCE_REVIEWER_FORBIDDEN", "Only the Founder can manage this resource.");
        }

        search = NormalizeOptional(search);
        if (!Fits(search, MaximumConversationSubjectLength))
            return MessagingControlledResourceRecipientListResult.Failure("MESSAGING_SEARCH_INVALID", "The search text is too long.");

        var recipients = await ListFounderControlledResourceRecipientsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
            recipients = recipients.Where(recipient => MatchesContactSearch(recipient, search)).ToList();

        var managed = new List<MessagingControlledResourceRecipient>(Math.Min(recipients.Count, 100));
        foreach (var recipient in recipients.Take(100))
        {
            var access = await _controlledResources.GetAccessAsync(
                new MessagingActor(recipient.UserId, recipient.ParticipantType),
                resourceType,
                cancellationToken);
            managed.Add(new MessagingControlledResourceRecipient(recipient, resourceType, access.State));
        }

        return new MessagingControlledResourceRecipientListResult(true, null, null, managed);
    }

    public async Task<MessagingOperationResult> SetControlledResourceGrantAsync(
        SetControlledResourceGrantCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        var target = new MessagingActor(NormalizeUserId(command.TargetUserId), NormalizeRequired(command.TargetParticipantType));
        var resourceType = NormalizeRequired(command.ResourceType);
        if (!ControlledResourceTypes.IsSupported(resourceType))
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_INVALID", "This Legend resource is not available.");
        if (!await IsValidActorAsync(actor, cancellationToken) ||
            !await _controlledResources.IsFounderManagerAsync(actor, cancellationToken))
        {
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_REVIEWER_FORBIDDEN", "Only the Founder can manage this resource.");
        }
        if (!await IsValidActorAsync(target, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_RECIPIENT_UNAVAILABLE", "This person is no longer available.");

        var targetAccess = await _controlledResources.GetAccessAsync(target, resourceType, cancellationToken);
        if (!command.IsGranted && targetAccess.CanManage && resourceType == ControlledResourceTypes.LanguageTranslation)
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_FOUNDER_PERSISTENT", "Founder Language Translation Access remains active.");

        if (!await SetResourceGrantCoreAsync(actor, resourceType, target.UserId, target.ParticipantType, command.IsGranted, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_RECIPIENT_UNAVAILABLE", "This person is no longer available.");

        var pending = await _db.VerificationReviewRequests
            .Where(request =>
                request.ResourceType == resourceType &&
                request.Status == VerificationReviewStatuses.Pending &&
                request.RequesterUserId.ToLower() == target.UserId &&
                request.RequesterParticipantType == target.ParticipantType)
            .ToListAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var reviewConversationIds = pending
            .Select(request => request.ReviewConversationId)
            .Distinct()
            .ToArray();
        var reviewConversations = reviewConversationIds.Length == 0
            ? new Dictionary<Guid, MessageConversation>()
            : await _db.MessageConversations
                .Where(conversation => reviewConversationIds.Contains(conversation.Id))
                .ToDictionaryAsync(conversation => conversation.Id, cancellationToken);
        foreach (var request in pending)
        {
            request.Status = command.IsGranted ? VerificationReviewStatuses.Approved : VerificationReviewStatuses.Declined;
            request.ResolvedUtc = nowUtc;
            request.ResolvedByUserId = actor.UserId;
            _db.MobileActivityNotifications.Add(CreateControlledResourceOutcomeNotification(
                request,
                command.IsGranted,
                resolutionNote: null,
                nowUtc));
            if (reviewConversations.TryGetValue(request.ReviewConversationId, out var reviewConversation))
            {
                _db.InternalMessages.Add(new InternalMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = reviewConversation.Id,
                    SenderUserId = actor.UserId,
                    SenderType = actor.ParticipantType,
                    Body = command.IsGranted
                        ? $"{ControlledResourceDisplayName(resourceType)} granted directly by the Founder."
                        : $"{ControlledResourceDisplayName(resourceType)} declined directly by the Founder.",
                    SentUtc = nowUtc,
                    VerificationReviewRequestId = request.Id
                });
                reviewConversation.LastMessageUtc = nowUtc;
                reviewConversation.UpdatedUtc = nowUtc;
            }
        }

        AddAudit(actor.UserId,
            command.IsGranted ? "ControlledResourceGranted" : "ControlledResourceRevoked",
            null,
            null,
            target.UserId,
            resourceType,
            nowUtc);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogWarning(exception, "Controlled resource grant save failed. ResourceType={ResourceType} TargetUserId={TargetUserId}", resourceType, target.UserId);
            return MessagingOperationResult.Failure("MESSAGING_RESOURCE_SAVE_FAILED", "Legend could not update this resource. Please try again.");
        }

        return MessagingOperationResult.Success();
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
        var hiddenParticipants = await _db.MessageConversationParticipants
            .Where(participant => participant.ConversationId == conversation.Id &&
                                  participant.IsActive &&
                                  participant.HiddenUtc != null)
            .ToListAsync(cancellationToken);
        foreach (var participant in hiddenParticipants)
            participant.HiddenUtc = null;
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

        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var participant = await _db.MessageConversationParticipants
            .FirstOrDefaultAsync(x =>
                x.ConversationId == command.ConversationId &&
                x.IsActive &&
                actorUserIds.Contains(x.UserId.ToLower()) &&
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

    public async Task<MessagingOperationResult> SetConversationPinnedAsync(
        SetMessagingConversationPinnedCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var participant = await FindAuthorizedParticipantAsync(actor, command.ConversationId, cancellationToken);
        if (participant is null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        if (command.IsPinned && !participant.PinnedUtc.HasValue)
        {
            var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
            var pinnedCount = await _db.MessageConversationParticipants
                .CountAsync(candidate =>
                    candidate.IsActive &&
                    actorUserIds.Contains(candidate.UserId.ToLower()) &&
                    candidate.ParticipantType == actor.ParticipantType &&
                    candidate.PinnedUtc != null,
                    cancellationToken);
            if (pinnedCount >= MaximumPinnedConversations)
            {
                return MessagingOperationResult.Failure(
                    "MESSAGING_PIN_LIMIT_REACHED",
                    $"You can pin up to {MaximumPinnedConversations} conversations.");
            }
        }

        participant.PinnedUtc = command.IsPinned ? DateTime.UtcNow : null;
        AddAudit(
            actor.UserId,
            command.IsPinned ? "ConversationPinned" : "ConversationUnpinned",
            command.ConversationId,
            null,
            null,
            null,
            DateTime.UtcNow);
        return await SaveOperationAsync("ConversationPinned", actor.UserId, command.ConversationId, cancellationToken);
    }

    public async Task<MessagingOperationResult> RemoveConversationForActorAsync(
        RemoveMessagingConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var participant = await FindAuthorizedParticipantAsync(actor, command.ConversationId, cancellationToken);
        if (participant is null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");

        participant.PinnedUtc = null;
        participant.HiddenUtc = DateTime.UtcNow;
        AddAudit(actor.UserId, "ConversationRemovedFromInbox", command.ConversationId, null, null, null, participant.HiddenUtc.Value);
        return await SaveOperationAsync("ConversationRemovedFromInbox", actor.UserId, command.ConversationId, cancellationToken);
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

    public async Task<MessagingOperationResult> DeleteMessageAsync(
        DeleteMessagingMessageCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");
        if (command.MessageId == Guid.Empty || command.ConversationId == Guid.Empty)
            return MessagingOperationResult.Failure("MESSAGING_MESSAGE_INVALID", "The requested message is invalid.");

        var message = await _db.InternalMessages
            .Include(candidate => candidate.Conversation)
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == command.MessageId &&
                candidate.ConversationId == command.ConversationId,
                cancellationToken);
        if (message is null || message.IsDeleted)
            return MessagingOperationResult.Failure("MESSAGING_MESSAGE_NOT_FOUND", "The requested message is not available.");

        var participant = await FindAuthorizedParticipantAsync(actor, command.ConversationId, cancellationToken);
        if (participant is null ||
            !IsSameParticipant(message.SenderUserId, message.SenderType, actor.UserId, actor.ParticipantType))
        {
            return MessagingOperationResult.Failure("MESSAGING_MESSAGE_FORBIDDEN", "Only your own messages can be unsent.");
        }

        message.Body = string.Empty;
        message.IsDeleted = true;
        message.DeletedUtc = DateTime.UtcNow;
        message.EditedUtc = message.DeletedUtc;
        AddAudit(actor.UserId, "MessageUnsent", command.ConversationId, message.Id, null, null, message.DeletedUtc.Value);
        return await SaveOperationAsync("MessageUnsent", actor.UserId, command.ConversationId, cancellationToken);
    }

    public async Task<MessagingConversationCallOptionsResult> GetConversationCallOptionsAsync(
        MessagingActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingConversationCallOptionsResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available for this user.");

        var conversation = await (await AuthorizedConversationsQueryAsync(actor, cancellationToken))
            .AsNoTracking()
            .Where(candidate => candidate.Id == conversationId)
            .Select(candidate => new { candidate.Id, candidate.ConversationType })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
            return MessagingConversationCallOptionsResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The requested conversation was not found.");
        if (conversation.ConversationType == MessagingConversationTypes.Group)
            return MessagingConversationCallOptionsResult.Failure("MESSAGING_CALL_NOT_AVAILABLE", "Calls are available for direct conversations only.");

        var participants = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(candidate => candidate.ConversationId == conversationId && candidate.IsActive)
            .Select(candidate => new MessagingParticipantReference(candidate.UserId, candidate.ParticipantType))
            .ToListAsync(cancellationToken);
        var counterparty = participants.SingleOrDefault(participant =>
            !IsSameParticipant(participant.UserId, participant.ParticipantType, actor.UserId, actor.ParticipantType));
        if (participants.Count != 2 || counterparty is null)
        {
            return MessagingConversationCallOptionsResult.Failure(
                "MESSAGING_CALL_NOT_AVAILABLE",
                "Calls are available only when this conversation has one other active participant.");
        }

        var identities = await _participantIdentities.ResolveIdentitiesAsync([counterparty], cancellationToken);
        if (!identities.TryGetValue((counterparty.UserId, counterparty.ParticipantType), out var identity))
            return MessagingConversationCallOptionsResult.Failure("MESSAGING_CALL_NOT_AVAILABLE", "This participant is no longer available for calling.");

        var phone = NormalizePhoneForNativeCall(identity.Phone);
        var faceTimeAddress = NormalizeFaceTimeAddress(identity.Email) ?? phone;
        if (phone is null && faceTimeAddress is null)
            return MessagingConversationCallOptionsResult.Failure("MESSAGING_CALL_NOT_AVAILABLE", "This participant has not shared a call address.");

        return new MessagingConversationCallOptionsResult(
            true,
            null,
            null,
            new MessagingConversationCallOptions(
                conversationId,
                identity.DisplayName,
                phone,
                faceTimeAddress));
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
        if (attachment.InternalMessage.IsDeleted)
            return MessagingAttachmentAccessResult.Failure("MESSAGING_ATTACHMENT_NOT_FOUND", "The requested attachment was removed with its message.");
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
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var participantConversations = _db.MessageConversations.Where(conversation =>
            conversation.Participants.Any(participant =>
                participant.IsActive &&
                actorUserIds.Contains(participant.UserId.ToLower()) &&
                participant.ParticipantType == actorParticipantType));

        var activeAgentUserIds = ActiveMessagingAgentProfilesQuery()
            .Select(profile => profile.AgentUserId.ToLower());
        var activeClientUserIds = ActiveClientMembershipUserIdsQuery();

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

        // Group membership is the authorization boundary once the group has
        // been created. Creation and later additions independently require the
        // owner's recipient authority; this query only permits active typed
        // profiles to read or send within the resulting group.
        var groupConversations = participantConversations.Where(conversation =>
            conversation.ConversationType == MessagingConversationTypes.Group &&
            conversation.Participants.Where(participant =>
                    participant.IsActive &&
                    participant.ParticipantType == MessagingParticipantTypes.Agent)
                .All(participant => activeAgentUserIds.Contains(participant.UserId.ToLower())) &&
            conversation.Participants.Where(participant =>
                    participant.IsActive &&
                    participant.ParticipantType == MessagingParticipantTypes.Client)
                .All(participant => activeClientUserIds.Contains(participant.UserId.ToLower())));

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
            return agentDirectConversations
                .Union(clientAgentConversations)
                .Union(groupConversations);
        }

        if (actorParticipantType == MessagingParticipantTypes.Client)
        {
            var clientAgentConversations = participantConversations.Where(conversation =>
                conversation.ConversationType == MessagingConversationTypes.ClientAgent &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Agent)
                    .All(agent => activeAgentUserIds.Contains(agent.UserId.ToLower())) &&
                conversation.Participants.Where(participant => participant.IsActive && participant.ParticipantType == MessagingParticipantTypes.Client)
                    .All(client => activeClientUserIds.Contains(client.UserId.ToLower())));
            return clientAgentConversations
                .Union(clientJourneyConversations)
                .Union(groupConversations);
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

        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        return await _db.MessageConversationParticipants.FirstOrDefaultAsync(x =>
            x.ConversationId == conversationId && x.IsActive &&
            actorUserIds.Contains(x.UserId.ToLower()) && x.ParticipantType == actor.ParticipantType,
            cancellationToken);
    }

    private async Task<MessageConversation?> FindOrClaimDirectConversationAsync(
        string conversationType,
        string directConversationKey,
        MessagingActor actor,
        string targetUserId,
        string targetParticipantType,
        CancellationToken cancellationToken)
    {
        var keyedConversation = await FindDirectConversationAsync(
            conversationType,
            directConversationKey,
            cancellationToken);
        if (keyedConversation is not null)
            return keyedConversation;

        var actorParticipantType = NormalizeRequired(actor.ParticipantType);
        targetUserId = NormalizeUserId(targetUserId);
        targetParticipantType = NormalizeRequired(targetParticipantType);
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var targetUserIds = await ParticipantUserIdFormsAsync(
            new MessagingActor(targetUserId, targetParticipantType),
            cancellationToken);

        // The original direct-key migration intentionally left existing rows
        // nullable. Without this participant-identity fallback, opening a
        // legacy direct thread creates a second keyed conversation.
        var legacyConversation = await _db.MessageConversations
            .Where(conversation => conversation.ConversationType == conversationType)
            .Where(conversation => conversation.DirectConversationKey == null)
            .Where(conversation =>
                conversation.Participants.Count(participant => participant.IsActive) == 2 &&
                conversation.Participants.Any(participant =>
                    participant.IsActive &&
                    actorUserIds.Contains(participant.UserId.ToLower()) &&
                    participant.ParticipantType == actorParticipantType) &&
                conversation.Participants.Any(participant =>
                    participant.IsActive &&
                    targetUserIds.Contains(participant.UserId.ToLower()) &&
                    participant.ParticipantType == targetParticipantType))
            .OrderByDescending(conversation => conversation.LastMessageUtc ?? conversation.CreatedUtc)
            .ThenByDescending(conversation => conversation.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (legacyConversation is null)
            return null;

        legacyConversation.DirectConversationKey = directConversationKey;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Messaging legacy direct conversation claimed. ConversationId={ConversationId} ConversationType={ConversationType} DirectConversationKey={DirectConversationKey}",
                legacyConversation.Id,
                conversationType,
                directConversationKey);
            return legacyConversation;
        }
        catch (DbUpdateException ex) when (IsVerifiedDirectConversationKeyConflict(ex))
        {
            _db.ChangeTracker.Clear();
            return await FindDirectConversationAsync(
                conversationType,
                directConversationKey,
                cancellationToken);
        }
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

    /// <summary>
    /// Resolves the persisted identities for one logical actor before any
    /// recipient, activity, or conversation query is evaluated. Azure-backed
    /// clients may have both their legacy ClientUserId and external object ID
    /// in existing rows; treating either as a different person drops valid
    /// delivery and inbox state.
    /// </summary>
    private async Task<string[]> ParticipantUserIdFormsAsync(
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        if (actor.ParticipantType != MessagingParticipantTypes.Client)
            return [actor.UserId];

        var profile = await ActiveMessagingClientProfilesQuery()
            .Where(candidate => candidate.ClientUserId.ToLower() == actor.UserId ||
                                (candidate.ExternalIdentityObjectId != null &&
                                 candidate.ExternalIdentityObjectId.ToLower() == actor.UserId))
            .Select(candidate => new
            {
                candidate.ClientUserId,
                candidate.ExternalIdentityObjectId
            })
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null
            ? [actor.UserId]
            : LogicalParticipantIdentity.ClientUserIdForms(
                profile.ClientUserId,
                profile.ExternalIdentityObjectId);
    }

    private static bool IsCurrentActor(
        string userId,
        string participantType,
        IReadOnlyCollection<string> actorUserIds,
        string actorParticipantType) =>
        participantType == actorParticipantType &&
        actorUserIds.Contains(NormalizeUserId(userId), StringComparer.Ordinal);

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

    private static IEnumerable<MessagingRecipientSummary> CanonicalAgentRecipients(
        IEnumerable<RecipientAgentRow> rows,
        string? excludedAgentUserId = null)
    {
        return rows
            .GroupBy(row => AgentProfileIdentity.DirectoryKey(
                row.NormalizedEmail,
                row.Email,
                row.UserId), StringComparer.Ordinal)
            .Where(group => !group.Any(row =>
                !string.IsNullOrWhiteSpace(excludedAgentUserId) &&
                string.Equals(row.UserId, excludedAgentUserId, StringComparison.OrdinalIgnoreCase)))
            .Select(group => group
                .OrderByDescending(row => AgentProfileIdentity.DirectoryCompleteness(
                    row.NormalizedEmail,
                    row.FullName,
                    row.Title,
                    row.ShortBio))
                .ThenByDescending(row => row.UpdatedUtc)
                .ThenBy(row => row.CreatedUtc)
                .ThenBy(row => row.UserId, StringComparer.Ordinal)
                .First())
            .Select(row => new MessagingRecipientSummary(
                row.UserId,
                MessagingParticipantTypes.Agent,
                FirstNonEmpty(row.FullName, row.Email, "Agent"),
                row.Email,
                "Agent"));
    }

    private static bool MatchesContactSearch(MessagingRecipientSummary recipient, string search)
    {
        var normalizedSearch = NormalizeSearchText(search);
        if (string.IsNullOrWhiteSpace(normalizedSearch))
            return true;

        var searchable = NormalizeSearchText(
            $"{recipient.DisplayName} {recipient.Email} {recipient.Username}");
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
                .Select(x => new RecipientAgentRow(
                    x.AgentUserId,
                    x.NormalizedEmail,
                    x.FullName,
                    x.AgentUpn,
                    x.Title,
                    x.ShortBio,
                    x.CreatedUtc,
                    x.UpdatedUtc))
                .ToListAsync(cancellationToken);

            recipients.AddRange(CanonicalAgentRecipients(agentRows, agentUserId));
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
                .Select(x => new RecipientAgentRow(
                    x.AgentUserId,
                    x.NormalizedEmail,
                    x.FullName,
                    x.AgentUpn,
                    x.Title,
                    x.ShortBio,
                    x.CreatedUtc,
                    x.UpdatedUtc))
                .ToListAsync(cancellationToken);

            recipients.AddRange(CanonicalAgentRecipients(agentRows));
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

        var profileKeys = identities.Values
            .Select(identity => (identity.ProfileId, identity.ParticipantType))
            .ToHashSet();
        var profileIds = profileKeys.Select(key => key.ProfileId).ToArray();
        var usernames = profileIds.Length == 0
            ? new Dictionary<(Guid ProfileId, string ParticipantType), string?>()
            : (await _db.MobileProfileSettings
                .AsNoTracking()
                .Where(setting => profileIds.Contains(setting.ProfileId))
                .Select(setting => new
                {
                    setting.ProfileId,
                    setting.ParticipantType,
                    setting.NormalizedUsername
                })
                .ToListAsync(cancellationToken))
                .Where(setting => profileKeys.Contains((setting.ProfileId, setting.ParticipantType)))
                .ToDictionary(
                    setting => (setting.ProfileId, setting.ParticipantType),
                    setting => setting.NormalizedUsername);

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
                    Email = identity.Email,
                    Username = usernames.GetValueOrDefault(
                        (identity.ProfileId, identity.ParticipantType))
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
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var conversations = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(participant =>
                participant.IsActive &&
                recipientKeys.Contains(participant.UserId.ToLower()))
            .Where(participant => participant.Conversation.Participants.Count(conversationParticipant =>
                conversationParticipant.IsActive) == 2)
            .Where(participant => participant.Conversation.Participants.Any(conversationParticipant =>
                conversationParticipant.IsActive &&
                actorUserIds.Contains(conversationParticipant.UserId.ToLower()) &&
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

    private IQueryable<string> AuthorizedClientIdsForAgentQuery(string agentUserId)
    {
        var canonicalClientIds = AuthorizedCanonicalClientIdsForAgentQuery(agentUserId);
        var authorizedProfiles = ActiveMessagingClientProfilesQuery()
            .Where(profile => canonicalClientIds.Contains(profile.ClientUserId.ToLower()));

        return authorizedProfiles
            .Select(profile => profile.ClientUserId.ToLower())
            .Union(authorizedProfiles
                .Where(profile => profile.ExternalIdentityObjectId != null)
                .Select(profile => profile.ExternalIdentityObjectId!.ToLower()))
            .Distinct();
    }

    private IQueryable<string> AuthorizedCanonicalClientIdsForAgentQuery(string agentUserId) =>
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
            .Select(profile => profile.ClientUserId.ToLower())
            .Union(ActiveMessagingClientProfilesQuery()
                .Where(profile => profile.ExternalIdentityObjectId != null)
                .Select(profile => profile.ExternalIdentityObjectId!.ToLower()))
            .Distinct();

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

    private async Task<MessagingConversationResult> CreateGroupConversationCoreAsync(
        MessagingActor actor,
        IReadOnlyList<MessagingParticipantReference> targets,
        string subject,
        string? initialMessage,
        string? clientMessageId,
        string? purpose,
        MessagingParticipantReference owner,
        MessagingGroupImage? groupImage,
        CancellationToken cancellationToken)
    {
        if (!IsValidGroupImage(groupImage))
        {
            return MessagingConversationResult.Failure(
                "MESSAGING_GROUP_PROFILE_INVALID",
                "The group image must be a supported image smaller than 512 KB.");
        }

        var moderation = _moderation.Evaluate(initialMessage, "MessagingGroupStart");
        if (!moderation.IsAllowed)
        {
            AddAudit(actor.UserId, "ContentBlocked", null, null, null, moderation.ReasonCode, DateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return MessagingConversationResult.Failure("MESSAGING_CONTENT_BLOCKED", RespectfulCommunicationMessage);
        }

        var nowUtc = DateTime.UtcNow;
        var conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = MessagingConversationTypes.Group,
            Subject = subject,
            Purpose = purpose,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            CreatedByUserId = actor.UserId,
            OwnerUserId = owner.UserId,
            OwnerParticipantType = owner.ParticipantType,
            GroupImageContent = groupImage?.Content,
            GroupImageContentType = groupImage?.ContentType
        };
        _db.MessageConversations.Add(conversation);

        var participants = new[]
            {
                new MessagingParticipantReference(actor.UserId, actor.ParticipantType)
            }
            .Concat(targets)
            .Distinct()
            .ToArray();
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

        AddAudit(actor.UserId, "GroupConversationCreated", conversation.Id, null, null, purpose, nowUtc);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Messaging group creation failed. ActorUserId={ActorUserId} ConversationId={ConversationId} Purpose={Purpose}",
                actor.UserId,
                conversation.Id,
                purpose);
            return MessagingConversationResult.Failure(
                "MESSAGING_GROUP_SAVE_FAILED",
                "We could not create this group. Please try again.");
        }

        return await GetConversationAsync(actor, conversation.Id, cancellationToken);
    }

    private async Task<Dictionary<(string UserId, string ParticipantType), string>> LoadDisplayNamesAsync(
        IReadOnlyCollection<ParticipantRow> participants,
        IReadOnlyCollection<MessagingParticipantReference>? additionalParticipants,
        CancellationToken cancellationToken)
    {
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            participants.Select(participant => new MessagingParticipantReference(
                participant.UserId,
                participant.ParticipantType))
                .Concat(additionalParticipants ?? Array.Empty<MessagingParticipantReference>()),
            cancellationToken);
        return identities.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DisplayName);
    }

    private Task<Dictionary<(string UserId, string ParticipantType), string>> LoadDisplayNamesAsync(
        IReadOnlyCollection<ParticipantRow> participants,
        CancellationToken cancellationToken) =>
        LoadDisplayNamesAsync(participants, null, cancellationToken);

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

    private Task<bool> IsFounderVerificationManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken) =>
        _controlledResources.IsFounderManagerAsync(actor, cancellationToken);

    private async Task<VerificationReviewRequest?> FindPendingControlledResourceRequestAsync(
        MessagingActor actor,
        string resourceType,
        CancellationToken cancellationToken) =>
        await _db.VerificationReviewRequests
            .AsNoTracking()
            .Where(request =>
                request.RequesterUserId.ToLower() == actor.UserId &&
                request.RequesterParticipantType == actor.ParticipantType &&
                request.ResourceType == resourceType &&
                request.Status == VerificationReviewStatuses.Pending)
            .OrderByDescending(request => request.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<MessageConversation?> GetOrCreateControlledResourceReviewConversationAsync(
        MessagingActor requester,
        CancellationToken cancellationToken)
    {
        var profiles = await _db.AgentProfiles
            .AsNoTracking()
            .Where(profile => profile.IsActive)
            .Select(profile => new
            {
                profile.AgentUserId,
                profile.AgentUpn,
                profile.NormalizedEmail,
                profile.FullName,
                profile.Title,
                profile.ShortBio,
                profile.UpdatedUtc
            })
            .ToListAsync(cancellationToken);

        var staff = new List<MessagingParticipantReference>(2);
        foreach (var email in new[] { LegendVerifiedIdentity.FounderEmail, LegendVerifiedIdentity.LegendEmail })
        {
            var profile = profiles
                .Where(candidate => string.Equals(
                    NormalizeUserId(candidate.NormalizedEmail ?? candidate.AgentUpn),
                    email,
                    StringComparison.Ordinal))
                .OrderByDescending(candidate => AgentProfileIdentity.DirectoryCompleteness(
                    candidate.NormalizedEmail,
                    candidate.FullName,
                    candidate.Title,
                    candidate.ShortBio))
                .ThenByDescending(candidate => candidate.UpdatedUtc)
                .FirstOrDefault();
            if (profile is null || string.IsNullOrWhiteSpace(profile.AgentUserId))
                return null;

            staff.Add(new MessagingParticipantReference(
                NormalizeUserId(profile.AgentUserId),
                MessagingParticipantTypes.Agent));
        }

        if (staff.Distinct().Count() != 2 || staff.Any(member =>
                IsSameParticipant(member.UserId, member.ParticipantType, requester.UserId, requester.ParticipantType)))
        {
            return null;
        }

        var founder = staff[0];
        var conversation = await _db.MessageConversations
            .Include(candidate => candidate.Participants)
            .Where(candidate =>
                candidate.ConversationType == MessagingConversationTypes.Group &&
                candidate.Purpose == MessagingConversationPurposes.ControlledResourceReview &&
                candidate.OwnerUserId!.ToLower() == founder.UserId &&
                candidate.OwnerParticipantType == MessagingParticipantTypes.Agent)
            .OrderBy(candidate => candidate.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is not null)
            return conversation;

        var nowUtc = DateTime.UtcNow;
        conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = MessagingConversationTypes.Group,
            Subject = "Legend resource review",
            Purpose = MessagingConversationPurposes.ControlledResourceReview,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            CreatedByUserId = founder.UserId,
            OwnerUserId = founder.UserId,
            OwnerParticipantType = MessagingParticipantTypes.Agent
        };
        _db.MessageConversations.Add(conversation);
        _db.MessageConversationParticipants.AddRange(staff.Select(member =>
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = member.UserId,
                ParticipantType = member.ParticipantType,
                IsActive = true,
                JoinedUtc = nowUtc
            }));
        AddAudit(founder.UserId, "ControlledResourceReviewGroupCreated", conversation.Id, null, null, null, nowUtc);
        return conversation;
    }

    private async Task<List<MessagingRecipientSummary>> ListFounderControlledResourceRecipientsAsync(
        CancellationToken cancellationToken)
    {
        var agentRows = await ActiveMessagingAgentProfilesQuery()
            .Select(profile => new RecipientAgentRow(
                profile.AgentUserId,
                profile.NormalizedEmail,
                profile.FullName,
                profile.AgentUpn,
                profile.Title,
                profile.ShortBio,
                profile.CreatedUtc,
                profile.UpdatedUtc))
            .ToListAsync(cancellationToken);
        var clientRows = await ActiveMessagingClientProfilesQuery()
            .Select(profile => new RecipientClientRow(
                profile.ClientUserId,
                profile.FirstName,
                profile.LastName,
                profile.Email,
                profile.CrmNotes,
                profile.CrmStatus))
            .ToListAsync(cancellationToken);

        var candidates = CanonicalAgentRecipients(agentRows).Concat(clientRows.Select(row =>
            new MessagingRecipientSummary(
                row.UserId,
                MessagingParticipantTypes.Client,
                FirstNonEmpty($"{row.FirstName} {row.LastName}".Trim(), row.Email, "Client"),
                row.Email,
                ClientRecordClassification.IsLead(row.UserId, row.CrmNotes, row.CrmStatus) ? "Lead" : "Client")))
            .GroupBy(recipient => (NormalizeUserId(recipient.UserId), recipient.ParticipantType))
            .Select(group => group.First())
            .OrderBy(recipient => recipient.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await ResolveRecipientIdentitiesAsync(candidates, cancellationToken);
    }

    private async Task<bool> SetResourceGrantCoreAsync(
        MessagingActor founder,
        string resourceType,
        string targetUserId,
        string targetParticipantType,
        bool isGranted,
        CancellationToken cancellationToken)
    {
        if (resourceType == ControlledResourceTypes.VerificationBadge)
        {
            return targetParticipantType switch
            {
                MessagingParticipantTypes.Agent => await SetAgentVerificationAsync(targetUserId, isGranted, cancellationToken),
                MessagingParticipantTypes.Client => await SetClientVerificationAsync(targetUserId, isGranted, cancellationToken),
                _ => false
            };
        }

        if (resourceType != ControlledResourceTypes.LanguageTranslation)
            return false;

        var grant = await _db.ControlledResourceGrants.SingleOrDefaultAsync(candidate =>
            candidate.UserId.ToLower() == targetUserId &&
            candidate.ParticipantType == targetParticipantType &&
            candidate.ResourceType == resourceType,
            cancellationToken);
        var nowUtc = DateTime.UtcNow;
        if (grant is null)
        {
            if (!isGranted)
                return true;

            _db.ControlledResourceGrants.Add(new ControlledResourceGrant
            {
                Id = Guid.NewGuid(),
                UserId = targetUserId,
                ParticipantType = targetParticipantType,
                ResourceType = resourceType,
                IsActive = true,
                GrantedUtc = nowUtc,
                GrantedByUserId = founder.UserId
            });
            return true;
        }

        if (grant.IsActive == isGranted)
            return true;

        grant.IsActive = isGranted;
        if (isGranted)
        {
            grant.GrantedUtc = nowUtc;
            grant.GrantedByUserId = founder.UserId;
            grant.RevokedUtc = null;
            grant.RevokedByUserId = null;
        }
        else
        {
            grant.RevokedUtc = nowUtc;
            grant.RevokedByUserId = founder.UserId;
        }
        return true;
    }

    private static MessagingVerificationReview ToReview(VerificationReviewRequest request) => new(
        request.Id,
        request.RequesterUserId,
        request.RequesterParticipantType,
        request.Status,
        request.RequestedUtc,
        ResourceType: request.ResourceType);

    private static string ControlledResourceDisplayName(string resourceType) => resourceType switch
    {
        ControlledResourceTypes.VerificationBadge => "Legend verification",
        ControlledResourceTypes.LanguageTranslation => "Language Translation Access",
        _ => "Legend resource"
    };

    /// <summary>
    /// Decision copy is owned by the server so every entry point (Founder
    /// review, direct grant, and future staff tooling) produces the same
    /// recipient outcome without opening a conversation.
    /// </summary>
    private static MobileActivityNotification CreateControlledResourceOutcomeNotification(
        VerificationReviewRequest request,
        bool approved,
        string? resolutionNote,
        DateTime occurredUtc)
    {
        var resourceName = ControlledResourceDisplayName(request.ResourceType);
        var defaultDetail = (request.ResourceType, approved) switch
        {
            (ControlledResourceTypes.VerificationBadge, true) =>
                "Your verification request was approved. Your verified badge is now active.",
            (ControlledResourceTypes.VerificationBadge, false) =>
                "Your verification request was not approved. You can update your profile and submit a new request when ready.",
            (ControlledResourceTypes.LanguageTranslation, true) =>
                "Language Translation Access was approved. You can now select your preferred communication language in Profile settings.",
            (ControlledResourceTypes.LanguageTranslation, false) =>
                "Language Translation Access was not approved. You can submit a new request when ready.",
            (_, true) => $"{resourceName} was approved.",
            _ => $"{resourceName} was not approved."
        };

        return new MobileActivityNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = request.RequesterUserId,
            RecipientParticipantType = request.RequesterParticipantType,
            Kind = approved ? "ControlledResourceApproved" : "ControlledResourceDeclined",
            Title = approved ? $"{resourceName} approved" : $"{resourceName} declined",
            Detail = resolutionNote ?? defaultDetail,
            ControlledResourceRequestId = request.Id,
            OccurredUtc = occurredUtc
        };
    }

    private Task<bool> IsActiveAgentAsync(
        string userId,
        CancellationToken cancellationToken) =>
        _db.AgentProfiles.AsNoTracking().AnyAsync(profile =>
            profile.IsActive && profile.AgentUserId.ToLower() == userId,
            cancellationToken);

    private async Task<bool> SetAgentVerificationAsync(
        string userId,
        bool isVerified,
        CancellationToken cancellationToken)
    {
        var profile = await _db.AgentProfiles.FirstOrDefaultAsync(candidate =>
            candidate.IsActive && candidate.AgentUserId.ToLower() == userId,
            cancellationToken);
        if (profile is null)
            return false;
        profile.IsVerified = isVerified;
        profile.UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    private async Task<bool> SetClientVerificationAsync(
        string userId,
        bool isVerified,
        CancellationToken cancellationToken)
    {
        var profile = await _db.ClientProfiles.FirstOrDefaultAsync(candidate =>
            candidate.ClientUserId.ToLower() == userId ||
            (candidate.ExternalIdentityObjectId != null && candidate.ExternalIdentityObjectId.ToLower() == userId),
            cancellationToken);
        if (profile is null)
            return false;
        profile.IsVerified = isVerified;
        profile.UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    private static bool IsValidGroupImage(MessagingGroupImage? image) =>
        image is null ||
        (image.Content.Length is > 0 and <= MaximumGroupImageBytes &&
         (string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(image.ContentType, "image/png", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(image.ContentType, "image/heic", StringComparison.OrdinalIgnoreCase)));

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
        IReadOnlyCollection<AttachmentRow> attachments,
        IReadOnlyDictionary<Guid, MessagingVerificationReview>? reviews = null)
    {
        return new MessagingMessageSummary(
            message.Id,
            message.ConversationId,
            message.SenderUserId,
            message.SenderType,
            message.IsDeleted ? "Message unsent" : message.Body,
            message.SentUtc,
            message.EditedUtc,
            message.IsDeleted,
            message.IsDeleted
                ? Array.Empty<MessagingAttachmentSummary>()
                : attachments.Where(x => x.InternalMessageId == message.Id).Select(ToAttachmentSummary).ToList(),
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
                    message.Reply.IsDeleted),
            message.VerificationReviewRequestId.HasValue &&
            reviews?.TryGetValue(message.VerificationReviewRequestId.Value, out var review) == true
                ? review
                : null);
    }

    private static MessagingGroupImage? ToGroupImage(byte[]? content, string? contentType) =>
        content is { Length: > 0 } && !string.IsNullOrWhiteSpace(contentType)
            ? new MessagingGroupImage(content, contentType)
            : null;

    private async Task<List<MessagingMessageSummary>> ApplyTranslationPresentationAsync(
        MessagingActor actor,
        IReadOnlyList<MessagingMessageSummary> summaries,
        IReadOnlyList<MessageDetailRow> sourceMessages,
        CancellationToken cancellationToken)
    {
        var targetLanguage = await _controlledResources.GetPreferredLanguageAsync(actor, cancellationToken);
        if (targetLanguage is null || summaries.Count == 0)
            return summaries.ToList();

        var sources = sourceMessages.ToDictionary(message => message.Id);
        var presented = new List<MessagingMessageSummary>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (!sources.TryGetValue(summary.Id, out var source) ||
                summary.IsDeleted ||
                summary.VerificationReview is not null ||
                IsSameParticipant(summary.SenderUserId, summary.SenderType, actor.UserId, actor.ParticipantType))
            {
                presented.Add(summary);
                continue;
            }

            var senderLanguage = await _controlledResources.GetPreferredLanguageAsync(
                new MessagingActor(source.SenderUserId, source.SenderType),
                cancellationToken);
            if (string.Equals(senderLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(CommunicationLanguages.NormalizeOrNull(source.OriginalLanguage), targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                presented.Add(summary);
                continue;
            }

            var translation = await GetOrCreateMessageTranslationAsync(source, targetLanguage, cancellationToken);
            presented.Add(translation is null
                ? summary
                : summary with
                {
                    Body = translation.TranslatedText,
                    OriginalBody = source.Body,
                    Translation = new MessagingTranslationPresentation(
                        translation.OriginalLanguage,
                        targetLanguage,
                        translation.Provider)
                });
        }

        return presented;
    }

    private async Task<CachedMessageTranslation?> GetOrCreateMessageTranslationAsync(
        MessageDetailRow message,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var cached = await _db.MessageTranslations
            .AsNoTracking()
            .Where(translation =>
                translation.InternalMessageId == message.Id &&
                translation.TargetLanguage == targetLanguage)
            .Select(translation => new CachedMessageTranslation(
                translation.TranslatedText,
                message.OriginalLanguage ?? string.Empty,
                translation.Provider))
            .SingleOrDefaultAsync(cancellationToken);
        if (cached is not null)
            return cached.OriginalLanguage.Length == 0
                ? null
                : cached;

        var sourceLanguage = CommunicationLanguages.NormalizeOrNull(message.OriginalLanguage);
        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            return null;

        TranslationProviderResult providerResult;
        try
        {
            providerResult = await _translation.TranslateAsync(
                message.Body,
                targetLanguage,
                sourceLanguage,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message translation provider failed. MessageId={MessageId} TargetLanguage={TargetLanguage}", message.Id, targetLanguage);
            return null;
        }

        var detectedLanguage = CommunicationLanguages.NormalizeOrNull(providerResult.DetectedLanguage) ?? sourceLanguage;
        if (!providerResult.Succeeded ||
            string.IsNullOrWhiteSpace(providerResult.TranslatedText) ||
            detectedLanguage is null ||
            string.Equals(detectedLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(message.OriginalLanguage, detectedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            var messageEntity = _db.InternalMessages.Local
                .FirstOrDefault(candidate => candidate.Id == message.Id);
            if (messageEntity is null)
            {
                messageEntity = new InternalMessage { Id = message.Id };
                _db.Attach(messageEntity);
            }
            messageEntity.OriginalLanguage = detectedLanguage;
            _db.Entry(messageEntity).Property(entity => entity.OriginalLanguage).IsModified = true;
        }

        var created = new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = message.Id,
            TargetLanguage = targetLanguage,
            TranslatedText = providerResult.TranslatedText.Trim(),
            Provider = providerResult.Provider,
            CreatedUtc = DateTime.UtcNow
        };
        _db.MessageTranslations.Add(created);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.Entry(created).State = EntityState.Detached;
            var concurrent = await _db.MessageTranslations
                .AsNoTracking()
                .Where(translation =>
                    translation.InternalMessageId == message.Id &&
                    translation.TargetLanguage == targetLanguage)
                .Select(translation => new CachedMessageTranslation(
                    translation.TranslatedText,
                    detectedLanguage,
                    translation.Provider))
                .SingleOrDefaultAsync(cancellationToken);
            if (concurrent is null)
                return null;
            return concurrent;
        }

        return new CachedMessageTranslation(created.TranslatedText, detectedLanguage, created.Provider);
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

    private static string? NormalizePhoneForNativeCall(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is < 7 or > 20)
            return null;

        return trimmed.StartsWith('+') ? $"+{digits}" : digits;
    }

    private static string? NormalizeFaceTimeAddress(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is { Length: <= 320 } &&
               normalized.Contains('@') &&
               !normalized.Any(char.IsWhiteSpace)
            ? normalized
            : null;
    }

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
        bool IsClosed,
        string? Purpose,
        byte[]? GroupImageContent,
        string? GroupImageContentType,
        DateTime? PinnedUtc);

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
        bool IsClosed,
        string? OwnerUserId,
        string? OwnerParticipantType,
        string? Purpose,
        byte[]? GroupImageContent,
        string? GroupImageContentType);

    private sealed record ParticipantRow(
        Guid ConversationId,
        string UserId,
        string ParticipantType,
        DateTime? LastReadUtc,
        bool IsMuted,
        DateTime? PinnedUtc,
        DateTime? HiddenUtc);

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
        string? OriginalLanguage,
        DateTime SentUtc,
        DateTime? EditedUtc,
        bool IsDeleted,
        Guid? ReplyToMessageId,
        Guid? VerificationReviewRequestId,
        ReplyDetailRow? Reply);

    private sealed record CachedMessageTranslation(
        string TranslatedText,
        string OriginalLanguage,
        string Provider);

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

    private sealed record RecipientAgentRow(
        string UserId,
        string? NormalizedEmail,
        string? FullName,
        string? Email,
        string? Title,
        string? ShortBio,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    private sealed record RecipientClientRow(string UserId, string? FirstName, string? LastName, string? Email, string? CrmNotes, string? CrmStatus);
}
