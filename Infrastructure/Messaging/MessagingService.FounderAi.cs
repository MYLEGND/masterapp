using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Messaging;

// Founder AI transcripts use the existing messaging store and authorization
// owner. This partial adds no provider, inference path, background job or cache.
internal sealed partial class MessagingService
{
    private const int FounderAiMaximumBodyCharacters = 1_000_000;
    private const int FounderAiMaximumMetadataCharacters = 1_000_000;
    private const int FounderAiMaximumPageBytes = 32_000_000;
    private static readonly JsonSerializerOptions FounderAiJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record FounderAiMessageSize(Guid Id, int BodyLength, int MetadataLength);

    private sealed record FounderAiRequestReceipt(int Version, string Mode, string RequestFingerprint,
        DateTime ExecutionDeadlineUtc,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MessagingFounderAiCloudflareDelegation? CloudflareDelegation = null);

    private async Task<bool> IsFounderAiOwnerAsync(MessagingActor actor, CancellationToken ct) =>
        actor.ParticipantType == MessagingParticipantTypes.Agent &&
        await _controlledResources.IsCanonicalFounderManagerAsync(actor, ct) &&
        await _db.AgentProfiles.AsNoTracking().AnyAsync(profile => profile.IsActive &&
            profile.AgentUserId.ToLower() == actor.UserId, ct);

    private IQueryable<MessageConversation> FounderAiOwnedQuery(MessagingActor actor) =>
        _db.MessageConversations.Where(conversation =>
            conversation.ConversationType == MessagingConversationTypes.Assistant &&
            conversation.Purpose == MessagingConversationPurposes.FounderAI &&
            conversation.OwnerUserId == actor.UserId &&
            conversation.OwnerParticipantType == MessagingParticipantTypes.Agent &&
            conversation.CreatedByUserId == actor.UserId &&
            conversation.Participants.Count(participant => participant.IsActive) == 1 &&
            conversation.Participants.All(participant => participant.UserId == actor.UserId &&
                participant.ParticipantType == MessagingParticipantTypes.Agent));

    public async Task<MessagingConversationListResult> ListFounderAiConversationsAsync(
        MessagingActor actor, MessagingConversationListQuery query, CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsFounderAiOwnerAsync(actor, cancellationToken))
            return MessagingConversationListResult.Failure("FOUNDER_HISTORY_FORBIDDEN", ApplicationCopyText.Source("Conversation history is unavailable for this account."));
        var conversations = FounderAiOwnedQuery(actor).AsNoTracking();
        if (!query.IncludeClosed) conversations = conversations.Where(item => !item.IsClosed);
        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
            conversations = conversations.Where(item => item.Subject != null && item.Subject.Contains(search));
        var rows = await conversations.OrderByDescending(item => item.LastMessageUtc)
            .ThenByDescending(item => item.Id).Skip(Math.Clamp(query.Skip, 0, 10_000))
            .Take(Math.Clamp(query.Take, 1, 50))
            .Select(item => new { item.Id, item.Subject, item.LastMessageUtc, item.IsClosed })
            .ToListAsync(cancellationToken);
        return new(true, null, null, rows.Select(item => new MessagingConversationSummary(
            item.Id, MessagingConversationTypes.Assistant, item.Subject, FounderAiUtc(item.LastMessageUtc),
            item.IsClosed, false, 0, new("", "", "LEGEND"), null,
            MessagingConversationPurposes.FounderAI)).ToArray());
    }

    public async Task<MessagingConversationResult> GetFounderAiConversationPageAsync(
        MessagingActor actor, Guid conversationId, MessagingConversationMessagePageQuery query,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsFounderAiOwnerAsync(actor, cancellationToken)) return FounderAiHistoryNotFound();
        var conversation = await FounderAiOwnedQuery(actor).AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null) return FounderAiHistoryNotFound();
        var messages = _db.InternalMessages.AsNoTracking().Where(item =>
            item.ConversationId == conversationId && !item.IsDeleted);
        if (query.BeforeUtc is DateTime before)
            messages = ApplyConversationMessageCursor(messages, before, query.BeforeMessageId);
        else if (query.BeforeMessageId.HasValue)
            return MessagingConversationResult.Failure("FOUNDER_HISTORY_CURSOR_INVALID", ApplicationCopyText.Source("The conversation history cursor is invalid."));
        var take = Math.Clamp(query.Take, 1, 60);
        // Bound before loading bodies; a row-count limit alone permits tens of
        // megabytes because the established Founder input limit is larger.
        var ordered = messages.OrderByDescending(item => item.SentUtc).ThenByDescending(item => item.Id);
        var sqlServer = _db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
        var candidates = sqlServer
            ? await ordered.Select(item => new FounderAiMessageSize(item.Id,
                (EF.Functions.DataLength(item.Body) ?? 0) / 2,
                (EF.Functions.DataLength(item.AiTurnMetadataJson!) ?? 0) / 2))
                .Take(take + 1).ToListAsync(cancellationToken)
            : await ordered.Select(item => new FounderAiMessageSize(item.Id, item.Body.Length,
                item.AiTurnMetadataJson == null ? 0 : item.AiTurnMetadataJson.Length))
                .Take(take + 1).ToListAsync(cancellationToken);
        var selectedIds = new List<Guid>();
        long bytes = 0;
        foreach (var candidate in candidates.Take(take))
        {
            // JSON escaping can require six bytes per UTF-16 unit. Twelve
            // also covers SQLite length counting a surrogate pair as one scalar.
            // SQL Server DATALENGTH includes trailing spaces (LEN does not).
            var size = 12L * (candidate.BodyLength + candidate.MetadataLength) + 4096;
            if (candidate.BodyLength > FounderAiMaximumBodyCharacters ||
                candidate.MetadataLength > FounderAiMaximumMetadataCharacters ||
                size > FounderAiMaximumPageBytes)
                return MessagingConversationResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
            if (bytes + size > FounderAiMaximumPageBytes) break;
            bytes += size;
            selectedIds.Add(candidate.Id);
        }
        var selected = await messages.Where(item => selectedIds.Contains(item.Id)).ToListAsync(cancellationToken);
        if (selected.Count != selectedIds.Count || selected.Any(item =>
                (item.AuthorKind == MessagingAuthorKinds.Human &&
                    (item.SenderUserId != actor.UserId || item.SenderType != actor.ParticipantType)) ||
                item.Body.Length > FounderAiMaximumBodyCharacters ||
                (item.AiTurnMetadataJson?.Length ?? 0) > FounderAiMaximumMetadataCharacters) ||
            selected.Sum(item => 6L * (item.Body.Length + (item.AiTurnMetadataJson?.Length ?? 0)) + 4096) > FounderAiMaximumPageBytes)
            return MessagingConversationResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
        var byId = selected.ToDictionary(item => item.Id);
        var summaries = new List<MessagingMessageSummary>();
        foreach (var id in selectedIds.AsEnumerable().Reverse())
        {
            if (!TryProjectFounderAiMessage(byId[id], out var summary))
                return MessagingConversationResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
            summaries.Add(summary!);
        }
        // Original authored bodies are authoritative. Opening history never
        // invokes translation, promotes knowledge, or changes response language.
        return new(true, null, null, new MessagingConversationDetail(conversation.Id,
            conversation.ConversationType, conversation.Subject, FounderAiUtc(conversation.CreatedUtc),
            FounderAiUtc(conversation.LastMessageUtc), conversation.IsClosed, false, false,
            [new(actor.UserId, actor.ParticipantType, "Founder")], summaries,
            Purpose: MessagingConversationPurposes.FounderAI,
            HasOlderMessages: candidates.Count > selectedIds.Count));
    }

    public async Task<MessagingFounderAiOperationDelegation?> GetFounderAiOperationDelegationAsync(
        MessagingActor actor, Guid conversationId, Guid operationId, CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (conversationId == Guid.Empty || operationId == Guid.Empty ||
            !await IsFounderAiOwnerAsync(actor, cancellationToken)) return null;
        if (!await FounderAiOwnedQuery(actor).AsNoTracking().AnyAsync(item =>
                item.Id == conversationId && !item.IsClosed, cancellationToken)) return null;
        // Only the current pending operation may delegate. An older Human row
        // cannot revive after a terminal response or another admitted request.
        var user = await _db.InternalMessages.AsNoTracking().Where(item => item.ConversationId == conversationId)
            .OrderByDescending(item => item.SentUtc).ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (user is null || user.IsDeleted || user.SenderUserId != actor.UserId || user.SenderType != actor.ParticipantType ||
            user.ClientMessageId != FounderAiOperationKey(actor, conversationId, operationId, "user") ||
            !TryReadFounderAiRequest(user, out var receipt) || receipt!.Version != 2 ||
            receipt.ExecutionDeadlineUtc <= now || receipt.CloudflareDelegation is not { } delegation ||
            delegation.UserId != actor.UserId || delegation.ExpiresUtc <= now ||
            delegation.ExpiresUtc > receipt.ExecutionDeadlineUtc || delegation.ExpiresUtc > FounderAiUtc(user.SentUtc).AddSeconds(120) ||
            await FounderAiTerminalAsync(conversationId, user.Id, cancellationToken) is not null) return null;
        // Re-read current profile/Founder authority after resolving the receipt.
        // Session/version revocation remains the authenticated callback owner's
        // responsibility; possession of this metadata is not authentication.
        if (!await IsFounderAiOwnerAsync(actor, cancellationToken) ||
            receipt.ExecutionDeadlineUtc <= DateTime.UtcNow || delegation.ExpiresUtc <= DateTime.UtcNow) return null;
        return new(conversationId, operationId, receipt.RequestFingerprint, receipt.ExecutionDeadlineUtc,
            delegation with { Roles = delegation.Roles.ToArray() });
    }

    public async Task<MessagingFounderAiTurnResult> BeginFounderAiTurnAsync(
        MessagingFounderAiBeginTurnCommand command, CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsFounderAiOwnerAsync(actor, cancellationToken)) return FounderAiBeginNotFound();
        var now = DateTime.UtcNow;
        if (command.ConversationId == Guid.Empty || command.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > FounderAiMaximumBodyCharacters ||
            command.Mode is not ("legend" or "teacher") || !IsFounderAiFingerprint(command.RequestFingerprint) ||
            (command.CloudflareDelegation is { } scope &&
                (!ValidFounderAiDelegationScope(scope) || scope.UserId != actor.UserId)))
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_REQUEST_INVALID", ApplicationCopyText.Source("The conversation request is invalid."));
        var userKey = FounderAiOperationKey(actor, command.ConversationId, command.OperationId, "user");
        var conversation = await FounderAiOwnedQuery(actor).AsTracking()
            .SingleOrDefaultAsync(item => item.Id == command.ConversationId, cancellationToken);
        var added = new List<InternalMessage>();
        if (conversation is null)
        {
            if (await _db.MessageConversations.AsNoTracking().AnyAsync(item => item.Id == command.ConversationId, cancellationToken))
                return FounderAiBeginNotFound();
            if (command.ExpectedLastMessageId is not null)
                return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_STALE", ApplicationCopyText.Source("Conversation history changed. Reload it before sending."));
            if (!ValidFounderAiDeadline(command.ExecutionDeadlineUtc, now) || !ValidFounderAiDelegationAdmission(command, now))
                return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_DEADLINE_INVALID", ApplicationCopyText.Source("The conversation execution deadline is invalid."));
            conversation = new MessageConversation
            {
                Id = command.ConversationId, ConversationType = MessagingConversationTypes.Assistant,
                Purpose = MessagingConversationPurposes.FounderAI, CreatedByUserId = actor.UserId,
                OwnerUserId = actor.UserId, OwnerParticipantType = actor.ParticipantType,
                CreatedUtc = now, UpdatedUtc = now,
                Subject = command.Body.Length <= 80 ? command.Body : command.Body[..80],
                Participants = [new() { Id = Guid.NewGuid(), UserId = actor.UserId,
                    ParticipantType = actor.ParticipantType, IsActive = true, JoinedUtc = now }]
            };
            _db.MessageConversations.Add(conversation);
        }
        else if (conversation.IsClosed)
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_CLOSED", ApplicationCopyText.Source("This conversation is closed."));

        var prior = await _db.InternalMessages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ClientMessageId == userKey && item.ConversationId == command.ConversationId, cancellationToken);
        if (prior is not null)
        {
            if (prior.SenderUserId != actor.UserId || prior.SenderType != actor.ParticipantType ||
                !TryReadFounderAiRequest(prior, out var receipt) || prior.Body != command.Body ||
                receipt!.RequestFingerprint != command.RequestFingerprint || receipt.Mode != command.Mode ||
                !SameFounderAiReplayIdentity(receipt.CloudflareDelegation, command.CloudflareDelegation))
                return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_REPLAY_MISMATCH", ApplicationCopyText.Source("This request identifier was already used for different content or settings."));
            // A retry supplies a freshly computed admission deadline, but this
            // branch only returns the old receipt. It never replaces metadata
            // or grants execution using the new delegation/expiry.
            var terminal = await FounderAiTerminalAsync(conversation.Id, prior.Id, cancellationToken);
            if (terminal is not null) return FounderAiReplay(prior, terminal, command.OperationId);
            if (receipt.ExecutionDeadlineUtc > now)
                return new(true, null, null, "Pending", ProjectFounderAiUser(prior), OperationId: command.OperationId);
            added.Add(AddFounderAiUnknown(conversation, prior, receipt, now));
            return await SaveFounderAiBeginAsync(conversation, added, prior, "OutcomeUnknown", command.OperationId, cancellationToken);
        }

        var last = await _db.InternalMessages.AsNoTracking().Where(item => item.ConversationId == conversation.Id)
            .OrderByDescending(item => item.SentUtc).ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (last?.Id != command.ExpectedLastMessageId)
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_STALE", ApplicationCopyText.Source("Conversation history changed. Reload it before sending."));
        if (!ValidFounderAiDeadline(command.ExecutionDeadlineUtc, now) || !ValidFounderAiDelegationAdmission(command, now))
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_DEADLINE_INVALID", ApplicationCopyText.Source("The conversation execution deadline is invalid."));
        if (last?.AuthorKind == MessagingAuthorKinds.Human)
        {
            if (!TryReadFounderAiRequest(last, out var pending))
                return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
            if (pending!.ExecutionDeadlineUtc > now)
                return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_PENDING", ApplicationCopyText.Source("Another request in this conversation is still running."));
            added.Add(AddFounderAiUnknown(conversation, last, pending, now));
        }
        var sentUtc = NextFounderAiTimestamp(conversation.LastMessageUtc, now);
        var user = new InternalMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id,
            SenderUserId = actor.UserId, SenderType = actor.ParticipantType,
            AuthorKind = MessagingAuthorKinds.Human, Body = command.Body,
            SentUtc = sentUtc, ClientMessageId = userKey,
            AiTurnMetadataJson = JsonSerializer.Serialize(new FounderAiRequestReceipt(command.CloudflareDelegation is null ? 1 : 2, command.Mode,
                command.RequestFingerprint, command.ExecutionDeadlineUtc, command.CloudflareDelegation is { } delegation
                    ? delegation with { Roles = delegation.Roles.ToArray() } : null), FounderAiJson)
        };
        _db.InternalMessages.Add(user); added.Add(user);
        TouchFounderAiConversation(conversation, sentUtc);
        return await SaveFounderAiBeginAsync(conversation, added, user, "Started", command.OperationId, cancellationToken);
    }

    public async Task<MessagingMessageResult> CompleteFounderAiTurnAsync(
        MessagingFounderAiCompleteTurnCommand command, CancellationToken cancellationToken = default)
    {
        var actor = NormalizeActor(command.Actor);
        if (!await IsFounderAiOwnerAsync(actor, cancellationToken)) return FounderAiCompleteNotFound();
        if (command.Provenance is null || !ValidFounderAiResponse(command.Provenance) ||
            command.AuthorKind is not (MessagingAuthorKinds.Assistant or MessagingAuthorKinds.Service) ||
            (command.AuthorKind == MessagingAuthorKinds.Assistant && !command.Provenance.Succeeded) ||
            string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > FounderAiMaximumBodyCharacters)
            return MessagingMessageResult.Failure("FOUNDER_HISTORY_RESPONSE_INVALID", ApplicationCopyText.Source("The conversation response could not be verified."));
        var json = JsonSerializer.Serialize(command.Provenance, FounderAiJson);
        if (json.Length > FounderAiMaximumMetadataCharacters)
            return MessagingMessageResult.Failure("FOUNDER_HISTORY_RESPONSE_INVALID", ApplicationCopyText.Source("The conversation response could not be verified."));
        var conversation = await FounderAiOwnedQuery(actor).AsTracking()
            .SingleOrDefaultAsync(item => item.Id == command.ConversationId, cancellationToken);
        if (conversation is null) return FounderAiCompleteNotFound();
        var userKey = FounderAiOperationKey(actor, command.ConversationId, command.OperationId, "user");
        var user = await _db.InternalMessages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == command.UserMessageId && item.ConversationId == conversation.Id && item.ClientMessageId == userKey,
            cancellationToken);
        if (user is null || !TryReadFounderAiRequest(user, out var receipt) || receipt!.Mode != command.Provenance.Mode)
            return FounderAiCompleteNotFound();
        var terminal = await FounderAiTerminalAsync(conversation.Id, user.Id, cancellationToken);
        if (terminal is not null)
        {
            if (terminal.AuthorKind != command.AuthorKind || terminal.Body != command.Body || terminal.AiTurnMetadataJson != json)
                return MessagingMessageResult.Failure("FOUNDER_HISTORY_TERMINAL", ApplicationCopyText.Source("This request already has a durable outcome and cannot be replaced."));
            return TryProjectFounderAiMessage(terminal, out var replay)
                ? new(true, null, null, replay, conversation.Id)
                : MessagingMessageResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
        }
        var lastId = await _db.InternalMessages.AsNoTracking().Where(item => item.ConversationId == conversation.Id)
            .OrderByDescending(item => item.SentUtc).ThenByDescending(item => item.Id).Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastId != user.Id)
            return MessagingMessageResult.Failure("FOUNDER_HISTORY_STALE", ApplicationCopyText.Source("Conversation history changed before this response was saved."));
        var now = DateTime.UtcNow;
        var expired = receipt.ExecutionDeadlineUtc <= now;
        var response = expired ? AddFounderAiUnknown(conversation, user, receipt, now) : new InternalMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, AuthorKind = command.AuthorKind,
            SenderUserId = "", SenderType = "", Body = command.Body,
            SentUtc = NextFounderAiTimestamp(conversation.LastMessageUtc, now),
            ClientMessageId = FounderAiTerminalKey(user.ClientMessageId!), ReplyToMessageId = user.Id,
            AiTurnMetadataJson = json
        };
        if (!expired) _db.InternalMessages.Add(response);
        TouchFounderAiConversation(conversation, response.SentUtc);
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsFounderAiWriteConflict(exception))
        {
            DetachFounderAiMutation(conversation, [response]);
            return MessagingMessageResult.Failure("FOUNDER_HISTORY_CONFLICT", ApplicationCopyText.Source("Conversation history changed. Reload it to see the durable outcome."));
        }
        if (expired)
            return new(false, "FOUNDER_HISTORY_OUTCOME_UNKNOWN", ApplicationCopyText.Source("The request ended without a confirmed durable response. Check any consequential action before trying again."),
                TryProjectFounderAiMessage(response, out var unknown) ? unknown : null, conversation.Id);
        return TryProjectFounderAiMessage(response, out var summary)
            ? new(true, null, null, summary, conversation.Id)
            : MessagingMessageResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
    }

    private async Task<MessagingFounderAiTurnResult> SaveFounderAiBeginAsync(MessageConversation conversation,
        IReadOnlyList<InternalMessage> added, InternalMessage user, string state, Guid operationId, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (IsFounderAiWriteConflict(exception))
        {
            DetachFounderAiMutation(conversation, added);
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_CONFLICT", ApplicationCopyText.Source("Conversation history changed. Reload it before sending."));
        }
        if (state == "OutcomeUnknown") return FounderAiReplay(user, added[^1], operationId);
        return new(true, null, null, state, ProjectFounderAiUser(user), OperationId: operationId);
    }

    private InternalMessage AddFounderAiUnknown(MessageConversation conversation, InternalMessage user,
        FounderAiRequestReceipt receipt, DateTime now)
    {
        var terminal = new InternalMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, AuthorKind = MessagingAuthorKinds.Service,
            SenderUserId = "", SenderType = "", ReplyToMessageId = user.Id,
            ClientMessageId = FounderAiTerminalKey(user.ClientMessageId!),
            Body = ApplicationCopyText.Source("This request ended without a confirmed durable response. Its outcome is unknown. Check any consequential action before trying again."),
            SentUtc = NextFounderAiTimestamp(conversation.LastMessageUtc, now),
            AiTurnMetadataJson = JsonSerializer.Serialize(MessagingFounderAiResponseProvenance.OutcomeUnknown(receipt.Mode), FounderAiJson)
        };
        _db.InternalMessages.Add(terminal);
        TouchFounderAiConversation(conversation, terminal.SentUtc);
        return terminal;
    }

    // SQL datetime2 contains UTC ticks without DateTime.Kind. Specify the
    // existing meaning at the wire boundary; never convert those stored ticks
    // through the server or device's local timezone.
    private static DateTime FounderAiUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? FounderAiUtc(DateTime? value) => value.HasValue ? FounderAiUtc(value.Value) : null;

    private static DateTime NextFounderAiTimestamp(DateTime? previous, DateTime now) =>
        previous >= now ? previous.Value.AddTicks(1) : now;

    private void TouchFounderAiConversation(MessageConversation conversation, DateTime sentUtc)
    {
        conversation.UpdatedUtc = sentUtc;
        conversation.LastMessageUtc = sentUtc;
        if (!(_db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) ?? false))
            conversation.RowVersion = Guid.NewGuid().ToByteArray();
    }

    private void DetachFounderAiMutation(MessageConversation conversation, IReadOnlyList<InternalMessage> added)
    {
        foreach (var message in added) _db.Entry(message).State = EntityState.Detached;
        foreach (var participant in conversation.Participants.Where(item => _db.Entry(item).State == EntityState.Added))
            _db.Entry(participant).State = EntityState.Detached;
        _db.Entry(conversation).State = EntityState.Detached;
    }

    private Task<InternalMessage?> FounderAiTerminalAsync(Guid conversationId, Guid userMessageId, CancellationToken ct) =>
        _db.InternalMessages.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == conversationId && item.ReplyToMessageId == userMessageId &&
            (item.AuthorKind == MessagingAuthorKinds.Assistant || item.AuthorKind == MessagingAuthorKinds.Service), ct);

    private static bool IsFounderAiWriteConflict(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException ||
        exception.InnerException is SqlException { Number: 2601 or 2627 } ||
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static bool IsFounderAiFingerprint(string? value) => value?.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidFounderAiDeadline(DateTime deadline, DateTime now) =>
        deadline.Kind == DateTimeKind.Utc && deadline > now && deadline <= now.AddSeconds(1800);

    private static string FounderAiOperationKey(MessagingActor actor, Guid conversationId, Guid operationId, string role) =>
        "fai:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            actor.UserId + "\n" + actor.ParticipantType + "\n" + conversationId.ToString("D") + "\n" + operationId.ToString("D")))) + ":" + role;

    private static string FounderAiTerminalKey(string userKey) => userKey[..^4] + "result";

    private static bool TryReadFounderAiRequest(InternalMessage item, out FounderAiRequestReceipt? receipt)
    {
        receipt = null;
        if (item.AuthorKind != MessagingAuthorKinds.Human || string.IsNullOrWhiteSpace(item.AiTurnMetadataJson) ||
            item.AiTurnMetadataJson.Length > FounderAiMaximumMetadataCharacters) return false;
        try { receipt = JsonSerializer.Deserialize<FounderAiRequestReceipt>(item.AiTurnMetadataJson, FounderAiJson); }
        catch (JsonException) { return false; }
        return receipt is not null &&
            (receipt.Version == 1 && receipt.CloudflareDelegation is null ||
             receipt.Version == 2 && receipt.CloudflareDelegation is { } delegation && ValidFounderAiDelegationScope(delegation)) &&
            receipt.Mode is "legend" or "teacher" && receipt.ExecutionDeadlineUtc.Kind == DateTimeKind.Utc &&
            IsFounderAiFingerprint(receipt.RequestFingerprint);
    }

    private static bool ValidFounderAiDelegationScope(MessagingFounderAiCloudflareDelegation value) =>
        ValidFounderAiScopeIdentifier(value.AccountId, 256) && ValidFounderAiScopeIdentifier(value.TenantId, 128) &&
        ValidFounderAiScopeIdentifier(value.UserId, 128) && ValidFounderAiScopeIdentifier(value.SessionId, 256) &&
        ValidFounderAiScopeIdentifier(value.AuthorizationVersion, 128) && ValidFounderAiScopeIdentifier(value.Environment, 32) &&
        value.ExpiresUtc.Kind == DateTimeKind.Utc && value.Roles is { Count: > 0 and <= 16 } &&
        value.Roles.All(role => ValidFounderAiScopeIdentifier(role, 64)) &&
        value.Roles.Distinct(StringComparer.Ordinal).Count() == value.Roles.Count &&
        value.Roles.Contains("Founder", StringComparer.Ordinal);

    private static bool ValidFounderAiScopeIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool ValidFounderAiDelegationAdmission(MessagingFounderAiBeginTurnCommand command, DateTime now) =>
        command.CloudflareDelegation is not { } value ||
        value.ExpiresUtc > now && value.ExpiresUtc <= now.AddSeconds(120) && value.ExpiresUtc <= command.ExecutionDeadlineUtc;

    private static bool SameFounderAiReplayIdentity(MessagingFounderAiCloudflareDelegation? prior, MessagingFounderAiCloudflareDelegation? current) =>
        prior is null ? current is null : current is not null &&
        prior.AccountId == current.AccountId && prior.TenantId == current.TenantId && prior.UserId == current.UserId &&
        prior.SessionId == current.SessionId && prior.AuthorizationVersion == current.AuthorizationVersion &&
        prior.Environment == current.Environment &&
        prior.Roles.SequenceEqual(current.Roles, StringComparer.Ordinal);

    private static bool ValidFounderAiResponse(MessagingFounderAiResponseProvenance value) =>
        value.Version == 1 && value.Mode is "legend" or "teacher" &&
        new[] { value.Stage, value.Reason, value.ResponseAuthority, value.FoundationModel, value.FoundationHosting,
            value.EscalationDisposition, value.ResearchState, value.LearningState, value.ModelAssistanceState,
            value.ModelVersion, value.ModelProvenance, value.FailureKind, value.Reference, value.ModelAssistanceReason }
            .All(field => field is null || field.Length <= 1024) &&
        value.ProviderStatusCode is null or (>= 100 and <= 599) &&
        (!value.BodyIsError || value.Error is null) &&
        (value.Error is null || value.Error.Length <= 10_000) &&
        new[] { value.CompletedWork, value.RemainingWork, value.ReasoningTransitionPath }
            .All(list => list is null || (list.Count <= 128 && list.All(item => item is not null && item.Length <= 2048)));

    private static MessagingMessageSummary ProjectFounderAiUser(InternalMessage item) =>
        new(item.Id, item.ConversationId, item.SenderUserId, item.SenderType, item.Body,
            FounderAiUtc(item.SentUtc), FounderAiUtc(item.EditedUtc), item.IsDeleted, [], item.ReplyToMessageId)
        { AuthorKind = MessagingAuthorKinds.Human };

    private static bool TryProjectFounderAiMessage(InternalMessage item, out MessagingMessageSummary? summary)
    {
        summary = null;
        if (item.AuthorKind == MessagingAuthorKinds.Human)
        {
            if (!TryReadFounderAiRequest(item, out _)) return false;
            summary = ProjectFounderAiUser(item); return true;
        }
        if (item.AuthorKind is not (MessagingAuthorKinds.Assistant or MessagingAuthorKinds.Service) ||
            item.SenderUserId.Length != 0 || item.SenderType.Length != 0 ||
            string.IsNullOrWhiteSpace(item.AiTurnMetadataJson) || item.AiTurnMetadataJson.Length > FounderAiMaximumMetadataCharacters)
            return false;
        MessagingFounderAiResponseProvenance? provenance;
        try { provenance = JsonSerializer.Deserialize<MessagingFounderAiResponseProvenance>(item.AiTurnMetadataJson, FounderAiJson); }
        catch (JsonException) { return false; }
        if (provenance is null || !ValidFounderAiResponse(provenance)) return false;
        summary = new(item.Id, item.ConversationId, "", "", item.Body, FounderAiUtc(item.SentUtc), FounderAiUtc(item.EditedUtc),
            item.IsDeleted, [], item.ReplyToMessageId)
        { AuthorKind = item.AuthorKind, ResponseProvenance = provenance };
        return true;
    }

    private static MessagingFounderAiTurnResult FounderAiReplay(InternalMessage user, InternalMessage terminal, Guid operationId)
    {
        if (!TryProjectFounderAiMessage(terminal, out var summary))
            return MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_INVALID", ApplicationCopyText.Source("Conversation history could not be verified."));
        return new(true, null, null, summary!.ResponseProvenance?.Reason == "outcome_unknown" ? "OutcomeUnknown" : "Completed",
            ProjectFounderAiUser(user), summary, operationId);
    }

    private static MessagingConversationResult FounderAiHistoryNotFound() =>
        MessagingConversationResult.Failure("FOUNDER_HISTORY_NOT_FOUND", ApplicationCopyText.Source("The requested conversation was not found."));
    private static MessagingFounderAiTurnResult FounderAiBeginNotFound() =>
        MessagingFounderAiTurnResult.Failure("FOUNDER_HISTORY_NOT_FOUND", ApplicationCopyText.Source("The requested conversation was not found."));
    private static MessagingMessageResult FounderAiCompleteNotFound() =>
        MessagingMessageResult.Failure("FOUNDER_HISTORY_NOT_FOUND", ApplicationCopyText.Source("The requested conversation was not found."));
}
