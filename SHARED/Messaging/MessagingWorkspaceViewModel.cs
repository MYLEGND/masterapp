namespace Shared.Messaging;

public sealed record MessagingWorkspaceViewModel(
    string CurrentUserId,
    IReadOnlyList<MessagingWorkspaceConversationViewModel> Conversations);

public sealed record MessagingWorkspaceConversationViewModel(
    Guid Id,
    string ConversationType,
    string DisplayName,
    string? Subject,
    string? LastMessagePreview,
    DateTime? LastMessageUtc,
    int UnreadCount,
    bool IsClosed);
