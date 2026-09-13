namespace Shared.Messaging;

// Presentation is available only through the authenticated messaging hub.
// A connection lease is reachability evidence, never a CRM/account status.
public sealed record MessagingPresenceParticipant(string UserId, string ParticipantType);
public sealed record MessagingPresenceRequest(MessagingPresenceParticipant[]? Participants = null, Guid[]? ConversationIds = null);
public sealed record MessagingParticipantPresence(string UserId, string ParticipantType, bool IsOnline);
public sealed record MessagingConversationPresence(Guid ConversationId, bool IsOnline);
public sealed record MessagingPresenceResult(DateTime ObservedUtc, int RefreshSeconds,
    MessagingParticipantPresence[] Participants, MessagingConversationPresence[] Conversations);
public interface IMessagingPresenceAuthority
{
    Task TouchConnectionAsync(string userId, string participantType, string connectionId, CancellationToken cancellationToken);
    Task RemoveConnectionAsync(string connectionId, CancellationToken cancellationToken);
    Task<MessagingPresenceResult> ReadPresenceAsync(string userId, string participantType, MessagingPresenceRequest request, CancellationToken cancellationToken);
}
