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

    /// <summary>
    /// Starts a request through the existing private Founder + Legend review
    /// queue. Verification is a compatibility wrapper around this one path.
    /// </summary>
    Task<MessagingControlledResourceRequestResult> StartControlledResourceRequestAsync(
        StartControlledResourceRequestCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the server-owned language allowlist only after Language
    /// Translation Access has been granted to the requesting profile.
    /// </summary>
    Task<MessagingCommunicationLanguageListResult> ListCommunicationLanguagesAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns recipient-scoped system outcomes for the mobile Activity sheet.
    /// It never projects staff-review conversations into a requester inbox.
    /// </summary>
    Task<MessagingActivityNotificationListResult> ListActivityNotificationsAsync(
        MessagingActor actor,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> ResolveVerificationReviewRequestAsync(
        ResolveVerificationReviewRequestCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> ResolveControlledResourceRequestAsync(
        ResolveControlledResourceRequestCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Founder-only search over the established people directory.</summary>
    Task<MessagingControlledResourceRecipientListResult> ListControlledResourceRecipientsAsync(
        MessagingActor actor,
        string resourceType,
        string? search = null,
        CancellationToken cancellationToken = default);

    /// <summary>Founder-only idempotent grant or revocation for a resource.</summary>
    Task<MessagingOperationResult> SetControlledResourceGrantAsync(
        SetControlledResourceGrantCommand command,
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

    Task<MessagingOperationResult> SetConversationPinnedAsync(
        SetMessagingConversationPinnedCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> RemoveConversationForActorAsync(
        RemoveMessagingConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> SetConversationClosedAsync(
        SetMessagingConversationClosedCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingOperationResult> DeleteMessageAsync(
        DeleteMessagingMessageCommand command,
        CancellationToken cancellationToken = default);

    Task<MessagingConversationCallOptionsResult> GetConversationCallOptionsAsync(
        MessagingActor actor,
        Guid conversationId,
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
