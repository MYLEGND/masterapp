using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Shared.Messaging;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService : IMessagingPresenceAuthority
{
    private const int PresenceRefreshSeconds = 30;
    private const int PresenceLeaseSeconds = 90;

    public async Task TouchConnectionAsync(string userId, string participantType, string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 128) throw new ArgumentException("Invalid connection identity.");
        var actor = NormalizeActor(new(userId, participantType));
        if (!await IsValidActorAsync(actor, cancellationToken)) throw new UnauthorizedAccessException("Messaging account unavailable.");
        var identities = await _participantIdentities.ResolveIdentitiesAsync([new(actor.UserId, actor.ParticipantType)], cancellationToken);
        if (!identities.TryGetValue(MessagingParticipantIdentityKey.Create(actor.UserId, actor.ParticipantType), out var identity))
            throw new UnauthorizedAccessException("Messaging identity unavailable.");
        var now = DateTime.UtcNow;
        // Update and expiry cleanup are conditional database mutations. A collector
        // must never delete a lease another application host has just refreshed.
        var updated = await _db.MessagingConnectionLeases
            .Where(row => row.ConnectionId == connectionId && row.ProfileId == identity.ProfileId && row.ParticipantType == identity.ParticipantType)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresUtc, now.AddSeconds(PresenceLeaseSeconds)), cancellationToken);
        if (updated == 0)
        {
            if (await _db.MessagingConnectionLeases.AnyAsync(row => row.ConnectionId == connectionId, cancellationToken))
                throw new UnauthorizedAccessException("Connection account changed.");
            _db.MessagingConnectionLeases.Add(new()
            {
                ConnectionId = connectionId, ProfileId = identity.ProfileId,
                ParticipantType = identity.ParticipantType, ExpiresUtc = now.AddSeconds(PresenceLeaseSeconds)
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        await _db.MessagingConnectionLeases.Where(row => row.ExpiresUtc <= now)
            .OrderBy(row => row.ExpiresUtc).Take(100).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task RemoveConnectionAsync(string connectionId, CancellationToken cancellationToken) =>
        await _db.MessagingConnectionLeases.Where(row => row.ConnectionId == connectionId).ExecuteDeleteAsync(cancellationToken);

    public async Task<MessagingPresenceResult> ReadPresenceAsync(string userId, string participantType, MessagingPresenceRequest request, CancellationToken ct)
    {
        var actor = NormalizeActor(new(userId, participantType));
        if (!await IsValidActorAsync(actor, ct)) throw new UnauthorizedAccessException("Messaging account unavailable.");
        var requestedPeople = request.Participants ?? [];
        var requestedConversations = request.ConversationIds ?? [];
        if (requestedPeople.Length > 50 || requestedConversations.Length > 50) throw new ArgumentException("Request at most fifty visible messaging contacts and conversations.");
        var requestedReferences = requestedPeople
            .Where(person => !string.IsNullOrWhiteSpace(person.UserId) && person.UserId.Length <= 450 && IsParticipantType(person.ParticipantType))
            .Select(person => new MessagingParticipantReference(person.UserId, person.ParticipantType)).Distinct().ToArray();
        // Authorize the directory once, then match canonical typed profiles. A
        // legacy client alias cannot broaden the existing recipient authority.
        var recipients = requestedReferences.Length == 0 ? [] : await ListAuthorizedRecipientsAsync(actor, recipientScope: null, ct);
        var contactIdentities = await _participantIdentities.ResolveIdentitiesAsync(
            requestedReferences.Concat(recipients.Select(person => new MessagingParticipantReference(person.UserId, person.ParticipantType))), ct);
        var authorizedProfiles = recipients.Select(person => contactIdentities.GetValueOrDefault(MessagingParticipantIdentityKey.Create(person.UserId, person.ParticipantType)))
            .Where(identity => identity != null).Select(identity => (identity!.ProfileId, identity.ParticipantType)).ToHashSet();
        var allowedPeople = requestedReferences.Where(person =>
            contactIdentities.TryGetValue(MessagingParticipantIdentityKey.Create(person.UserId, person.ParticipantType), out var identity)
            && authorizedProfiles.Contains((identity.ProfileId, identity.ParticipantType))).ToArray();
        var allowedConversationIds = await (await AuthorizedConversationsQueryAsync(actor, ct)).AsNoTracking()
            .Where(row => requestedConversations.Contains(row.Id) && !row.IsClosed && row.Purpose != MessagingConversationPurposes.FounderAI)
            .Select(row => row.Id).ToArrayAsync(ct);
        var members = await _db.MessageConversationParticipants.AsNoTracking().Where(row => allowedConversationIds.Contains(row.ConversationId) && row.IsActive)
            .Select(row => new { row.ConversationId, row.UserId, row.ParticipantType }).ToArrayAsync(ct);
        var actorIds = await ParticipantUserIdFormsAsync(actor, ct);
        var others = members.Where(row => !IsCurrentActor(row.UserId, row.ParticipantType, actorIds, actor.ParticipantType)).ToArray();
        var identities = await _participantIdentities.ResolveIdentitiesAsync(allowedPeople.Concat(others.Select(row => new MessagingParticipantReference(row.UserId, row.ParticipantType))), ct);
        var profileIds = identities.Values.Select(identity => identity.ProfileId).Distinct().ToArray();
        var now = DateTime.UtcNow;
        var online = await _db.MessagingConnectionLeases.AsNoTracking().Where(row => profileIds.Contains(row.ProfileId) && row.ExpiresUtc > now)
            .Select(row => new { row.ProfileId, row.ParticipantType }).Distinct().ToArrayAsync(ct);
        bool IsOnline(string id, string type) => identities.TryGetValue(MessagingParticipantIdentityKey.Create(id, type), out var identity)
            && online.Any(row => row.ProfileId == identity.ProfileId && row.ParticipantType == identity.ParticipantType);
        return new(now, PresenceRefreshSeconds,
            allowedPeople.Select(person => new MessagingParticipantPresence(person.UserId, person.ParticipantType, IsOnline(person.UserId, person.ParticipantType))).ToArray(),
            allowedConversationIds.Select(id => new MessagingConversationPresence(id, others.Any(member => member.ConversationId == id && IsOnline(member.UserId, member.ParticipantType)))).ToArray());
    }
}
