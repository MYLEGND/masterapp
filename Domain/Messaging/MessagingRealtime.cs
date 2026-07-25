namespace Domain.Messaging;

public interface IMessagingRealtimePublisher
{
    Task PublishAsync(
        MessagingRealtimeEvent notification,
        CancellationToken cancellationToken = default);
}

public sealed record MessagingRealtimeEvent(
    string EventType,
    Guid ConversationId,
    Guid? MessageId,
    DateTime OccurredUtc,
    IReadOnlyCollection<MessagingRealtimeRecipient> Recipients);

public sealed record MessagingRealtimeRecipient(string UserId, string ParticipantType);
