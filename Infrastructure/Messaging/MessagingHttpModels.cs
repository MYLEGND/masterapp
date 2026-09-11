namespace Infrastructure.Messaging;

public sealed record StartMessagingConversationRequest(
    string? ContactKey,
    string? Subject,
    string? Body,
    string? ClientMessageId,
    Guid? SharedPostId = null);

public sealed record SendMessagingMessageRequest(
    string Body,
    string? ClientMessageId,
    Guid? ReplyToMessageId = null,
    Guid? SharedPostId = null);

public sealed record SetMessagingConversationMutedRequest(bool IsMuted);

public sealed record SetMessagingConversationClosedRequest(bool IsClosed);
