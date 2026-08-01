namespace Domain.Messaging;

public interface IMessagingService
{
    Task<MessagingConversationListResult> ListConversationsAsync(
        MessagingActor actor,
        MessagingConversationListQuery query,
        CancellationToken cancellationToken = default);

    Task<MessagingRecipientListResult> ListRecipientsAsync(
        MessagingActor actor,
        string? search = null,
        string? recipientScope = null,
        CancellationToken cancellationToken = default);

    Task<MessagingRecipientResult> GetAuthorizedParticipantAsync(
        MessagingActor actor,
        string userId,
        string participantType,
        CancellationToken cancellationToken = default);

    Task<MessagingConversationResult> GetConversationAsync(
        MessagingActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<MessagingConversationResult> StartConversationAsync(
        StartMessagingConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingConversationResult> CreateGroupAsync(
        CreateMessagingGroupCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingVerificationRequestResult> StartVerificationRequestAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> ResolveVerificationReviewRequestAsync(
        ResolveVerificationReviewRequestCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> UpdateGroupProfileAsync(
        UpdateMessagingGroupProfileCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> AddGroupParticipantAsync(
        AddMessagingGroupParticipantCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingMessageResult> SendMessageAsync(
        SendMessagingMessageCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> MarkConversationReadAsync(
        MessagingConversationActionCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> SetConversationMutedAsync(
        SetMessagingConversationMutedCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> SetConversationClosedAsync(
        SetMessagingConversationClosedCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingAttachmentResult> AddPendingAttachmentAsync(
        AddPendingMessagingAttachmentCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingAttachmentAccessResult> GetAttachmentForDownloadAsync(
        MessagingAttachmentDownloadCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Integration point for a trusted malware-scanning provider. No provider is registered by default.
    /// </summary>
    Task<MessagingOperationResult> UpdateAttachmentScanStatusAsync(
        UpdateMessagingAttachmentScanStatusCommand command,
        CancellationToken cancellationToken = default);
}
