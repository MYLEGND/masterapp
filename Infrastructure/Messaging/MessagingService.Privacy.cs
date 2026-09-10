using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    private async Task<MessagingReadReceiptSettings> ReadReceiptSettingsAsync(
        MessagingActor actor, Guid conversationId, CancellationToken ct)
    {
        var members = await _db.MessageConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.IsActive).ToListAsync(ct);
        var identities = await _participantIdentities.ResolveIdentitiesAsync(members
            .Select(p => new MessagingParticipantReference(p.UserId, p.ParticipantType)).ToArray(), ct);
        var profileIds = identities.Values.Select(p => p.ProfileId).Distinct().ToArray();
        var preferences = await _db.MobileProfileSettings.AsNoTracking()
            .Where(p => profileIds.Contains(p.ProfileId)).ToListAsync(ct);
        bool Enabled(MessageConversationParticipant member)
        {
            var identity = identities.Values.FirstOrDefault(p => IsSameParticipant(
                p.UserId, p.ParticipantType, member.UserId, member.ParticipantType));
            return identity != null && (preferences.FirstOrDefault(p =>
                p.ProfileId == identity.ProfileId && p.ParticipantType == member.ParticipantType)?.SendReadReceipts ?? true);
        }
        var actorIds = await ParticipantUserIdFormsAsync(actor, ct);
        var own = members.FirstOrDefault(p => IsCurrentActor(p.UserId, p.ParticipantType, actorIds, actor.ParticipantType));
        return new MessagingReadReceiptSettings(own != null && Enabled(own), own?.SuppressReadReceipts == false,
            members.Where(p => !IsCurrentActor(p.UserId, p.ParticipantType, actorIds, actor.ParticipantType) &&
                !p.SuppressReadReceipts && p.SharedReadThroughUtc.HasValue && Enabled(p))
                .Select(p => new MessagingReadReceipt(p.UserId, p.ParticipantType, p.SharedReadThroughUtc!.Value)).ToArray());
    }

    public async Task<MessagingOperationResult> SetReadReceiptsAsync(MessagingActor actor,
        Guid conversationId, bool enabled, bool globally, CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available.");
        var member = await FindAuthorizedParticipantAsync(actor, conversationId, cancellationToken);
        if (member == null)
            return MessagingOperationResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "Conversation unavailable.");
        if (globally)
        {
            var identities = await _participantIdentities.ResolveIdentitiesAsync(
                [new MessagingParticipantReference(member.UserId, member.ParticipantType)], cancellationToken);
            var identity = identities.Values.SingleOrDefault();
            if (identity == null) return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Account unavailable.");
            var settings = await _db.MobileProfileSettings.SingleOrDefaultAsync(p =>
                p.ProfileId == identity.ProfileId && p.ParticipantType == actor.ParticipantType, cancellationToken);
            if (settings == null)
            {
                settings = new MobileProfileSettings { ProfileId = identity.ProfileId, ParticipantType = actor.ParticipantType };
                _db.MobileProfileSettings.Add(settings);
            }
            settings.SendReadReceipts = enabled;
            settings.UpdatedUtc = DateTime.UtcNow;
        }
        else member.SuppressReadReceipts = !enabled;
        AddAudit(actor.UserId, "ReadReceiptsChanged", conversationId, null, null, globally ? "global" : "conversation", DateTime.UtcNow);
        var result = await SaveOperationAsync("ReadReceiptsChanged", actor.UserId, conversationId, cancellationToken);
        if (result.Succeeded && globally)
        {
            var actorIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
            var ids = await _db.MessageConversationParticipants.AsNoTracking().Where(p => p.IsActive &&
                actorIds.Contains(p.UserId.ToLower()) && p.ParticipantType == actor.ParticipantType && p.ConversationId != conversationId)
                .Select(p => p.ConversationId).Distinct().ToListAsync(cancellationToken);
            foreach (var id in ids) await PublishConversationRefreshAsync(id, cancellationToken);
        }
        return result;
    }

    private async Task PublishConversationRefreshAsync(Guid conversationId, CancellationToken ct)
    {
        if (_realtime == null || conversationId == Guid.Empty) return;
        try
        {
            var recipients = await _db.MessageConversationParticipants.AsNoTracking()
                .Where(p => p.ConversationId == conversationId && p.IsActive)
                .Select(p => new MessagingRealtimeRecipient(p.UserId, p.ParticipantType)).ToArrayAsync(ct);
            await _realtime.PublishAsync(new MessagingRealtimeEvent("conversationUpdated", conversationId, null, DateTime.UtcNow, recipients), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Conversation refresh delivery failed after the change was saved.");
        }
    }
}
