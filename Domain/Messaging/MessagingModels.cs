using Domain.Entities;

namespace Domain.Messaging;

public static class MessagingConversationTypes
{
    public const string ClientAgent = "ClientAgent";
    public const string AgentDirect = "AgentDirect";
    public const string ClientJourney = "ClientJourney";
    public const string Group = "Group";
}

public static class MessagingConversationPurposes
{
    /// <summary>
    /// The original persisted name of the one private Founder + Legend staff
    /// queue. It now serves every controlled-resource review and is retained so
    /// existing verification rows and production data remain intact.
    /// </summary>
    public const string VerificationReview = "VerificationReview";
    public const string ControlledResourceReview = VerificationReview;
}

/// <summary>
/// Server-owned language choices for automatic message translation. Haitian
/// Creole is intentionally first: it is a priority Legend language and uses
/// Azure Translator's supported <c>ht</c> identifier.
/// </summary>
public static class CommunicationLanguages
{
    public static readonly IReadOnlyList<CommunicationLanguage> Supported =
    [
        new("en", "English"),
        // English remains the default experience. Haitian Creole is placed
        // directly beside it as a priority language, never as a replacement.
        new("ht", "Haitian Creole"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("pt", "Portuguese"),
        new("de", "German"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh-Hans", "Chinese (Simplified)"),
        new("ar", "Arabic")
    ];

    public static bool TryNormalize(string? value, out string language)
    {
        language = string.Empty;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var supported = Supported.FirstOrDefault(item =>
            string.Equals(item.Code, candidate, StringComparison.OrdinalIgnoreCase));
        if (supported is null)
            return false;

        language = supported.Code;
        return true;
    }

    public static string? NormalizeOrNull(string? value) =>
        TryNormalize(value, out var language) ? language : null;
}

public sealed record CommunicationLanguage(string Code, string DisplayName);

/// <summary>Trusted server-side boundary for a translation provider.</summary>
public interface ITranslationService
{
    Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default);
}

public sealed record TranslationDetectionResult(
    bool Succeeded,
    string? Language,
    string? ErrorCode = null);

public sealed record TranslationProviderResult(
    bool Succeeded,
    string? TranslatedText,
    string? DetectedLanguage,
    string Provider,
    string? ErrorCode = null);

public static class MessagingParticipantTypes
{
    public const string Agent = "Agent";
    public const string Client = "Client";
}

/// <summary>
/// Server-enforced recipient collections available to an agent in the messaging command center.
/// </summary>
public static class MessagingRecipientScopes
{
    public const string Agents = "Agents";
    public const string Clients = "Clients";
    public const string Leads = "Leads";
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

public sealed record CreateMessagingGroupCommand(
    MessagingActor Actor,
    IReadOnlyList<MessagingParticipantReference> Participants,
    string Subject,
    string? InitialMessageBody = null,
    string? ClientMessageId = null,
    MessagingGroupImage? GroupImage = null,
    MessagingGroupMeetingSetup? Meeting = null);

public sealed record MessagingGroupImage(
    byte[] Content,
    string ContentType);

public sealed record UpdateMessagingGroupProfileCommand(
    MessagingActor Actor,
    Guid ConversationId,
    string Subject,
    MessagingGroupImage? GroupImage,
    MessagingGroupMeetingSetup? Meeting = null);

/// <summary>
/// The owner-controlled group meeting configuration. A group always has a
/// typed host, while the online link and its schedule are optional.
/// </summary>
public sealed record MessagingGroupMeetingSetup(
    MessagingParticipantReference? Host = null,
    string? LinkLabel = null,
    string? LinkUrl = null,
    MessagingGroupMeetingSchedule? Schedule = null);

/// <summary>
/// A portable recurring schedule expressed in the organizer's local time.
/// Weekdays use the full English day names (for example, "Wednesday").
/// </summary>
public sealed record MessagingGroupMeetingSchedule(
    string Frequency,
    IReadOnlyList<string>? Weekdays = null,
    string? LocalTime = null,
    string? TimeZoneId = null,
    DateTime? StartsUtc = null,
    string? CustomDescription = null);

public static class MessagingGroupMeetingFrequencies
{
    public const string OneTime = "OneTime";
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Biweekly = "Biweekly";
    public const string Monthly = "Monthly";
    public const string Custom = "Custom";
}

public sealed record AddMessagingGroupParticipantCommand(
    MessagingActor Actor,
    Guid ConversationId,
    string UserId,
    string ParticipantType);

public sealed record SetMessagingGroupManagerCommand(
    MessagingActor Actor,
    Guid ConversationId,
    string UserId,
    string ParticipantType,
    bool IsManager);

public sealed record DeleteMessagingGroupCommand(
    MessagingActor Actor,
    Guid ConversationId);

public sealed record SetMessagingGroupPromotionCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsPromoted);

public sealed record JoinPromotedMessagingGroupCommand(
    MessagingActor Actor,
    Guid ConversationId);

public sealed record ResolveVerificationReviewRequestCommand(
    MessagingActor Actor,
    Guid RequestId,
    bool Approve,
    string? ResolutionNote = null);

public sealed record StartControlledResourceRequestCommand(
    MessagingActor Actor,
    string ResourceType);

public sealed record ResolveControlledResourceRequestCommand(
    MessagingActor Actor,
    Guid RequestId,
    bool Approve,
    string? ResolutionNote = null);

public sealed record SetControlledResourceGrantCommand(
    MessagingActor Actor,
    string ResourceType,
    string TargetUserId,
    string TargetParticipantType,
    bool IsGranted);

public sealed record SendMessagingMessageCommand(
    MessagingActor Actor,
    Guid ConversationId,
    string Body,
    string? ClientMessageId = null,
    Guid? ReplyToMessageId = null);

public sealed record MessagingConversationActionCommand(
    MessagingActor Actor,
    Guid ConversationId);

public sealed record SetMessagingConversationMutedCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsMuted);

public sealed record SetMessagingConversationPinnedCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsPinned);

public sealed record RemoveMessagingConversationCommand(
    MessagingActor Actor,
    Guid ConversationId);

public sealed record SetMessagingConversationClosedCommand(
    MessagingActor Actor,
    Guid ConversationId,
    bool IsClosed);

public sealed record DeleteMessagingMessageCommand(
    MessagingActor Actor,
    Guid ConversationId,
    Guid MessageId);

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

public sealed record MessagingVerificationRequestResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingVerificationReview? Request)
{
    public static MessagingVerificationRequestResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingControlledResourceRequestResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingVerificationReview? Request)
{
    public static MessagingControlledResourceRequestResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

/// <summary>
/// Recipient-facing outcome surfaced in the mobile Activity sheet. It is not a
/// message and therefore never creates a direct or group conversation.
/// </summary>
public sealed record MessagingActivityNotification(
    Guid Id,
    string Kind,
    string Title,
    string Detail,
    DateTime OccurredUtc,
    Guid? ControlledResourceRequestId);

public sealed record MessagingActivityNotificationListResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MessagingActivityNotification> Notifications)
{
    public static MessagingActivityNotificationListResult Failure(
        string errorCode,
        string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<MessagingActivityNotification>());
}

public sealed record MessagingCommunicationLanguageListResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<CommunicationLanguage> Languages)
{
    public static MessagingCommunicationLanguageListResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<CommunicationLanguage>());
}

public sealed record MessagingControlledResourceRecipientListResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MessagingControlledResourceRecipient> Recipients)
{
    public static MessagingControlledResourceRecipientListResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<MessagingControlledResourceRecipient>());
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

public sealed record MessagingConversationCallOptionsResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingConversationCallOptions? Options)
{
    public static MessagingConversationCallOptionsResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingConversationSummary(
    Guid Id,
    string ConversationType,
    string? Subject,
    DateTime? LastMessageUtc,
    bool IsClosed,
    bool IsArchivedMembership,
    int UnreadCount,
    MessagingParticipantSummary Counterparty,
    string? LastMessagePreview,
    string? Purpose = null,
    MessagingGroupImage? GroupImage = null,
    bool IsPinned = false,
    bool IsMuted = false);

/// <summary>
/// A server-authorized native call target for a direct conversation. The app
/// launches the system Phone or FaceTime experience; it never fabricates a
/// separate calling identity or exposes targets for a group conversation.
/// </summary>
public sealed record MessagingConversationCallOptions(
    Guid ConversationId,
    string DisplayName,
    string? PhoneNumber,
    string? FaceTimeAddress);

public sealed record MessagingConversationDetail(
    Guid Id,
    string ConversationType,
    string? Subject,
    DateTime CreatedUtc,
    DateTime? LastMessageUtc,
    bool IsClosed,
    bool IsArchivedMembership,
    bool IsMuted,
    IReadOnlyList<MessagingParticipantSummary> Participants,
    IReadOnlyList<MessagingMessageSummary> Messages,
    bool CanManageMembers = false,
    string? Purpose = null,
    MessagingGroupImage? GroupImage = null,
    bool CanManageCollaborators = false,
    bool CanDeleteGroup = false,
    bool IsPromoted = false,
    DateTime? PromotionStartedUtc = null,
    DateTime? PromotionEndedUtc = null,
    bool CanManagePromotion = false,
    MessagingGroupMeeting? Meeting = null,
    bool CanManageMeeting = false);

/// <summary>
/// The resolved meeting presentation for a group conversation. Host identity
/// is typed and resolved through the same profile source as all participants.
/// </summary>
public sealed record MessagingGroupMeeting(
    MessagingParticipantSummary Host,
    string? LinkLabel,
    string? LinkUrl,
    MessagingGroupMeetingSchedule? Schedule);

public sealed record MessagingParticipantSummary(
    string UserId,
    string ParticipantType,
    string DisplayName,
    bool IsGroupManager = false);

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
    string Initials,
    bool IsVerified = false,
    string? RoleLabel = null,
    string? Phone = null);

public sealed record MessagingRecipientSummary(
    string UserId,
    string ParticipantType,
    string DisplayName,
    string? Email,
    string? RelationshipLabel = null,
    Guid? ExistingConversationId = null,
    string? ContactKey = null,
    string? Username = null);

public sealed record MessagingMessageSummary(
    Guid Id,
    Guid ConversationId,
    string SenderUserId,
    string SenderType,
    string Body,
    DateTime SentUtc,
    DateTime? EditedUtc,
    bool IsDeleted,
    IReadOnlyList<MessagingAttachmentSummary> Attachments,
    Guid? ReplyToMessageId = null,
    MessagingReplyPreview? Reply = null,
    MessagingVerificationReview? VerificationReview = null,
    MessagingTranslationPresentation? Translation = null,
    string? OriginalBody = null);

/// <summary>
/// Presentation metadata for a server-cached derivative. The message body's
/// original text remains authoritative and is exposed through OriginalBody.
/// </summary>
public sealed record MessagingTranslationPresentation(
    string OriginalLanguage,
    string TargetLanguage,
    string Provider);

public sealed record MessagingVerificationReview(
    Guid Id,
    string RequesterUserId,
    string RequesterParticipantType,
    string Status,
    DateTime RequestedUtc,
    bool CanResolve = false,
    string ResourceType = ControlledResourceTypes.VerificationBadge);

public sealed record MessagingControlledResourceRecipient(
    MessagingRecipientSummary Recipient,
    string ResourceType,
    string AccessState);

public sealed record MessagingReplyPreview(
    Guid Id,
    string SenderUserId,
    string SenderType,
    string Body,
    bool IsDeleted);

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
