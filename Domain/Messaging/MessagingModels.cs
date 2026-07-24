namespace Domain.Messaging;

public static class MessagingConversationTypes
{
    public const string ClientAgent = "ClientAgent";
    public const string AgentDirect = "AgentDirect";
    public const string ClientJourney = "ClientJourney";
}

public static class MessagingParticipantTypes
{
    public const string Agent = "Agent";
    public const string Client = "Client";
}

public static class MessagingAttachmentScanStatuses
{
    public const string Pending = "Pending";
    public const string Scanning = "Scanning";
    public const string Clean = "Clean";
    public const string Rejected = "Rejected";
}

public sealed record MessagingActor(string UserId, string ParticipantType);

public sealed record MessagingConversationListQuery(
    string? Search = null,
    bool IncludeClosed = false,
    int Take = 50);

public sealed record StartMessagingConversationCommand(
    MessagingActor Actor,
    string TargetUserId,
    string TargetParticipantType,
    string? Subject = null,
    string? InitialMessageBody = null,
    string? ClientMessageId = null);

public sealed record SendMessagingMessageCommand(
    MessagingActor Actor,
    Guid ConversationId,
    string Body,
    string? ClientMessageId = null);

public sealed record MessagingConversationActionCommand(
    MessagingActor Actor,
    Guid ConversationId);

public sealed record SetMessagingConversationMutedCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsMuted);

public sealed record SetMessagingConversationClosedCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsClosed);

public sealed record AddPendingMessagingAttachmentCommand(
    MessagingActor Actor,
    Guid InternalMessageId,
    Guid AttachmentId,
    string OriginalFileName,
    string StoredFileName,
    string ContentType,
    long SizeBytes,
    string StoragePath);

public sealed record MessagingAttachmentDownloadCommand(
    MessagingActor Actor,
    Guid AttachmentId);

public sealed record UpdateMessagingAttachmentScanStatusCommand(
    string ActorUserId,
    Guid AttachmentId,
    string ScanStatus,
    string? Detail = null);

public sealed record MessagingConversationListResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MessagingConversationSummary> Conversations)
{
    public static MessagingConversationListResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<MessagingConversationSummary>());
}

public sealed record MessagingRecipientListResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MessagingRecipientSummary> Recipients)
{
    public static MessagingRecipientListResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<MessagingRecipientSummary>());
}

public sealed record MessagingRecipientResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingRecipientSummary? Recipient)
{
    public static MessagingRecipientResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingConversationResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingConversationDetail? Conversation)
{
    public static MessagingConversationResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingMessageResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingMessageSummary? Message,
    Guid? ConversationId)
{
    public static MessagingMessageResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null, null);
}

public sealed record MessagingAttachmentResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingAttachmentSummary? Attachment,
    Guid? ConversationId)
{
    public static MessagingAttachmentResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null, null);
}

public sealed record MessagingAttachmentAccessResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingAttachmentDownloadDescriptor? Attachment)
{
    public static MessagingAttachmentAccessResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingOperationResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static MessagingOperationResult Success() => new(true, null, null);
    public static MessagingOperationResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

public sealed record MessagingConversationSummary(
    Guid Id,
    string ConversationType,
    string? Subject,
    DateTime? LastMessageUtc,
    bool IsClosed,
    int UnreadCount,
    MessagingParticipantSummary Counterparty,
    string? LastMessagePreview);

public sealed record MessagingConversationDetail(
    Guid Id,
    string ConversationType,
    string? Subject,
    DateTime CreatedUtc,
    DateTime? LastMessageUtc,
    bool IsClosed,
    bool IsMuted,
    IReadOnlyList<MessagingParticipantSummary> Participants,
    IReadOnlyList<MessagingMessageSummary> Messages);

public sealed record MessagingParticipantSummary(
    string UserId,
    string ParticipantType,
    string DisplayName);

/// <summary>
/// A typed messaging identity reference. The user ID is interpreted only with its
/// participant type; it is never an email-address lookup key.
/// </summary>
public sealed record MessagingParticipantReference(
    string UserId,
    string ParticipantType);

/// <summary>
/// The current, typed profile projection used by messaging. Profile IDs remain
/// server-side and are not sent to the browser as routing authority.
/// </summary>
public sealed record MessagingParticipantIdentity(
    string UserId,
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string Initials);

public sealed record MessagingRecipientSummary(
    string UserId,
    string ParticipantType,
    string DisplayName,
    string? Email,
    string? RelationshipLabel = null,
    Guid? ExistingConversationId = null,
    string? ContactKey = null);

public sealed record MessagingMessageSummary(
    Guid Id,
    Guid ConversationId,
    string SenderUserId,
    string SenderType,
    string Body,
    DateTime SentUtc,
    DateTime? EditedUtc,
    bool IsDeleted,
    IReadOnlyList<MessagingAttachmentSummary> Attachments);

public sealed record MessagingAttachmentSummary(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string ScanStatus,
    DateTime CreatedUtc,
    bool CanDownload);

public sealed record MessagingAttachmentDownloadDescriptor(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string StoragePath);
