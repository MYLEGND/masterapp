using System.Data;
using System.Globalization;
using System.Text;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    public async Task<MessagingReactionResult> SetMessageReactionAsync(MessagingActor actor,
        Guid conversationId, Guid messageId, string? emoji, CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingReactionResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available.");
        if (conversationId == Guid.Empty || messageId == Guid.Empty ||
            (emoji != null && !IsSupportedReactionEmoji(emoji)))
            return MessagingReactionResult.Failure("MESSAGING_REACTION_INVALID", "Choose one supported emoji reaction.");

        var writeAttempted = false;
        MessagingReactionResult result;
        try
        {
            result = await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                // Capture only this attempt's additions. A retry must not reuse
                // a reaction/audit accepted by EF but rolled back by the database,
                // nor clear unrelated work from this scoped context.
                var previouslyTracked = _db.ChangeTracker.Entries().Select(x => x.Entity).ToHashSet();
                try
                {
                    await using var transaction = _db.Database.IsRelational()
                        ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                        : null;
                    var member = await FindAuthorizedParticipantAsync(actor, conversationId, cancellationToken);
                    if (member == null)
                        return MessagingReactionResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "The conversation is unavailable.");
                    var message = await _db.InternalMessages.AsNoTracking()
                        .Where(x => x.Id == messageId && x.ConversationId == conversationId && !x.IsDeleted)
                        .Select(x => new { x.Conversation.IsClosed, x.Conversation.ConversationType })
                        .SingleOrDefaultAsync(cancellationToken);
                    if (message == null)
                        return MessagingReactionResult.Failure("MESSAGING_MESSAGE_NOT_FOUND", "The message is unavailable.");
                    if (message.IsClosed)
                        return MessagingReactionResult.Failure("MESSAGING_CONVERSATION_CLOSED", "Closed conversations cannot receive reactions.");
                    if (!await CanSendWithinCommunitySafetyAsync(actor, conversationId, cancellationToken))
                        return MessagingReactionResult.Failure("MESSAGING_BLOCKED_BY_COMMUNITY_SAFETY", "A community block prevents reactions in this conversation.");
                    if (message.ConversationType == MessagingConversationTypes.ClientAgent &&
                        !await ConversationHasActiveClientMembershipAsync(conversationId, cancellationToken))
                        return MessagingReactionResult.Failure("MESSAGING_MEMBERSHIP_INACTIVE", "This membership is inactive.");

                    var profileId = await ReactionActorProfileIdAsync(actor, cancellationToken);
                    if (profileId == null)
                        return MessagingReactionResult.Failure("MESSAGING_ACTOR_INVALID", "The messaging identity is unavailable.");
                    var existing = await _db.MessageReactions.SingleOrDefaultAsync(x =>
                        x.InternalMessageId == messageId && x.ActorProfileId == profileId.Value &&
                        x.ParticipantType == actor.ParticipantType, cancellationToken);
                    var changed = existing?.Emoji != emoji;
                    if (changed)
                    {
                        if (emoji == null)
                            _db.MessageReactions.Remove(existing!);
                        else if (existing == null)
                            _db.MessageReactions.Add(new MessageReaction
                            {
                                InternalMessageId = messageId, ActorProfileId = profileId.Value,
                                ParticipantType = actor.ParticipantType, Emoji = emoji, UpdatedUtc = DateTime.UtcNow
                            });
                        else
                        {
                            existing.Emoji = emoji;
                            existing.UpdatedUtc = DateTime.UtcNow;
                        }
                        AddAudit(actor.UserId, "MessageReactionChanged", conversationId, messageId, null, null, DateTime.UtcNow);
                        writeAttempted = true;
                        await _db.SaveChangesAsync(cancellationToken);
                    }

                    // Read the canonical acknowledgment inside the transaction.
                    // There is no database read that can fail after commit.
                    var summaries = await ReactionSummariesAsync(actor, [messageId], cancellationToken, profileId);
                    var acknowledgment = new MessagingReactionResult(true, null, null,
                        new MessagingReactionState(messageId, summaries.GetValueOrDefault(messageId) ?? []));
                    if (transaction != null) await transaction.CommitAsync(cancellationToken);
                    return acknowledgment;
                }
                catch
                {
                    foreach (var entry in _db.ChangeTracker.Entries().ToArray())
                        if (!previouslyTracked.Contains(entry.Entity) ||
                            entry.Entity is MessageReaction reaction && reaction.InternalMessageId == messageId)
                            entry.State = EntityState.Detached;
                    throw;
                }
            });
        }
        catch (DbUpdateException)
        {
            _logger.LogWarning("A concurrent or failed message reaction write could not be committed.");
            return MessagingReactionResult.Failure("MESSAGING_REACTION_SAVE_FAILED", "The reaction could not be saved. Please retry.");
        }
        // An uncertain commit may be retried as a no-op. Still refresh after the
        // strategy confirms the requested state; never audit that no-op again.
        if (result.Succeeded && writeAttempted)
        {
            try { await PublishConversationRefreshAsync(conversationId, cancellationToken); }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Reaction committed; conversation refresh was cancelled.");
            }
        }
        return result;
    }

    private async Task<Guid?> ReactionActorProfileIdAsync(MessagingActor actor, CancellationToken ct)
    {
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(actor.UserId, actor.ParticipantType)], ct);
        var identity = identities.Values.FirstOrDefault(x =>
            string.Equals(x.ParticipantType, actor.ParticipantType, StringComparison.Ordinal));
        return identity?.ProfileId is Guid id && id != Guid.Empty ? id : null;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<MessagingReactionSummary>>> ReactionSummariesAsync(
        MessagingActor actor, IReadOnlyCollection<Guid> messageIds, CancellationToken ct, Guid? actorProfileId = null)
    {
        var result = new Dictionary<Guid, IReadOnlyList<MessagingReactionSummary>>();
        if (messageIds.Count == 0) return result;
        actorProfileId ??= await ReactionActorProfileIdAsync(actor, ct);
        // Only the authorized message page is queried. Aggregate in SQL rather
        // than materializing one row per reaction or querying each bubble.
        foreach (var batch in messageIds.Chunk(80))
        {
            var rows = await _db.MessageReactions.AsNoTracking()
                .Where(x => batch.Contains(x.InternalMessageId) && !x.InternalMessage.IsDeleted)
                .GroupBy(x => new { x.InternalMessageId, x.Emoji })
                .Select(group => new
                {
                    group.Key.InternalMessageId, group.Key.Emoji, Count = group.Count(),
                    Mine = group.Count(x => x.ActorProfileId == actorProfileId && x.ParticipantType == actor.ParticipantType)
                }).ToListAsync(ct);
            foreach (var group in rows.GroupBy(x => x.InternalMessageId))
                result[group.Key] = group.OrderBy(x => x.Emoji, StringComparer.Ordinal)
                    .Select(x => new MessagingReactionSummary(x.Emoji, x.Count, x.Mine > 0)).ToArray();
        }
        return result;
    }

    // One bounded grapheme from the supported emoji symbol repertoire, with
    // valid join/modifier structure. Ordinary text and markup are never reactions.
    internal static bool IsSupportedReactionEmoji(string value)
    {
        if (value.Length is 0 or > 64 || StringInfo.ParseCombiningCharacters(value).Length != 1) return false;
        var runes = value.EnumerateRunes().Select(x => x.Value).ToArray();
        if (runes.Length == 2 && runes.All(x => x is >= 0x1F1E6 and <= 0x1F1FF)) return true;
        if (runes.Length is 2 or 3 && (runes[0] is >= 0x30 and <= 0x39 or 0x23 or 0x2A) &&
            runes[^1] == 0x20E3 && (runes.Length == 2 || runes[1] == 0xFE0F)) return true;
        var needBase = true;
        var canModify = false;
        var canSelectPresentation = false;
        foreach (var rune in runes)
        {
            if (needBase)
            {
                if (!(rune is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
                    0x203C or 0x2049 or 0x2122 or 0x2139 or >= 0x2194 and <= 0x2199 or
                    0x21A9 or 0x21AA or 0x231A or 0x231B or 0x2328 or 0x23CF or
                    >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA or 0x24C2 or
                    0x25AA or 0x25AB or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE or
                    0x2934 or 0x2935 or >= 0x2B05 and <= 0x2B07 or 0x2B1B or 0x2B1C or
                    0x2B50 or 0x2B55 or 0x3030 or 0x303D or 0x3297 or 0x3299) ||
                    rune is >= 0x1F3FB and <= 0x1F3FF) return false;
                needBase = false;
                canModify = true;
                canSelectPresentation = true;
            }
            else if (rune == 0x200D) { needBase = true; canModify = false; }
            else if (rune == 0xFE0F && canSelectPresentation) { canSelectPresentation = false; }
            else if (rune is >= 0x1F3FB and <= 0x1F3FF && canModify) { canModify = false; canSelectPresentation = false; }
            else return false;
        }
        return !needBase;
    }
}
