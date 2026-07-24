namespace Infrastructure.Messaging;

public sealed record StartMessagingConversationRequest(
    string TargetUserId,
    string TargetParticipantType,
    string? Subject,
    string? Body,
    string? ClientMessageId);

public sealed record SendMessagingMessageRequest(
    string Body,
    string? ClientMessageId);

public sealed record SetMessagingConversationMutedRequest(bool IsMuted);

public sealed record SetMessagingConversationClosedRequest(bool IsClosed);
