import Foundation

struct MessagingParticipant: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let roleLabel: String?
    let avatar: ProfileAvatar?
    var isVerified: Bool? = nil
    var isGroupManager: Bool? = nil

    var id: LogicalParticipantIdentity { identity }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName
        case roleLabel
        case avatar
        case isVerified
        case isGroupManager
    }
}

struct ConversationSummary: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let conversationType: String
    let counterparty: MessagingParticipant
    let title: String
    let lastMessagePreview: String?
    let lastMessageUTC: Date?
    let unreadCount: Int
    let isClosed: Bool
    let purpose: String?
    let groupAvatar: ProfileAvatar?
    let isPinned: Bool
    let isMuted: Bool

    private enum CodingKeys: String, CodingKey {
        case id
        case conversationType
        case title
        case counterparty
        case lastMessagePreview
        case lastMessageUTC = "lastMessageUtc"
        case unreadCount
        case isClosed
        case purpose
        case groupAvatar
        case isPinned
        case isMuted
    }

    init(
        id: UUID,
        conversationType: String,
        counterparty: MessagingParticipant,
        title: String,
        lastMessagePreview: String?,
        lastMessageUTC: Date?,
        unreadCount: Int,
        isClosed: Bool,
        purpose: String? = nil,
        groupAvatar: ProfileAvatar? = nil,
        isPinned: Bool = false,
        isMuted: Bool = false
    ) {
        self.id = id
        self.conversationType = conversationType
        self.counterparty = counterparty
        self.title = title
        self.lastMessagePreview = lastMessagePreview
        self.lastMessageUTC = lastMessageUTC
        self.unreadCount = unreadCount
        self.isClosed = isClosed
        self.purpose = purpose
        self.groupAvatar = groupAvatar
        self.isPinned = isPinned
        self.isMuted = isMuted
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        conversationType = try container.decode(String.self, forKey: .conversationType)
        counterparty = try container.decode(MessagingParticipant.self, forKey: .counterparty)
        title = try container.decode(String.self, forKey: .title)
        lastMessagePreview = try container.decodeIfPresent(String.self, forKey: .lastMessagePreview)
        lastMessageUTC = try container.decodeIfPresent(Date.self, forKey: .lastMessageUTC)
        unreadCount = try container.decode(Int.self, forKey: .unreadCount)
        isClosed = try container.decode(Bool.self, forKey: .isClosed)
        purpose = try container.decodeIfPresent(String.self, forKey: .purpose)
        groupAvatar = try container.decodeIfPresent(ProfileAvatar.self, forKey: .groupAvatar)
        isPinned = try container.decodeIfPresent(Bool.self, forKey: .isPinned) ?? false
        isMuted = try container.decodeIfPresent(Bool.self, forKey: .isMuted) ?? false
    }
}

struct ConversationDetail: Codable, Equatable, Sendable {
    let id: UUID
    let conversationType: String
    let title: String
    let participants: [MessagingParticipant]
    let messages: [ConversationMessage]
    let isMuted: Bool
    let isClosed: Bool
    let canManageMembers: Bool
    let purpose: String?
    let groupAvatar: ProfileAvatar?
    let canManageCollaborators: Bool?
    let canDeleteGroup: Bool?
    let isPromoted: Bool?
    let promotionStartedUTC: Date?
    let promotionEndedUTC: Date?
    let canManagePromotion: Bool?
    let meeting: MessagingGroupMeeting?
    let canManageMeeting: Bool?
    let hasOlderMessages: Bool?
    let reactionOptions: [String]?
    let readReceipts: MessagingReadReceiptSettings?

    private enum CodingKeys: String, CodingKey {
        case id, conversationType, title, participants, messages, isMuted, isClosed
        case canManageMembers, purpose, groupAvatar, canManageCollaborators
        case canDeleteGroup, isPromoted, canManagePromotion, meeting, canManageMeeting
        case hasOlderMessages, readReceipts, reactionOptions
        case promotionStartedUTC = "promotionStartedUtc"
        case promotionEndedUTC = "promotionEndedUtc"
    }

    init(
        id: UUID,
        conversationType: String,
        title: String,
        participants: [MessagingParticipant],
        messages: [ConversationMessage],
        isMuted: Bool,
        isClosed: Bool,
        canManageMembers: Bool,
        purpose: String? = nil,
        groupAvatar: ProfileAvatar? = nil,
        canManageCollaborators: Bool? = nil,
        canDeleteGroup: Bool? = nil,
        isPromoted: Bool? = nil,
        promotionStartedUTC: Date? = nil,
        promotionEndedUTC: Date? = nil,
        canManagePromotion: Bool? = nil,
        meeting: MessagingGroupMeeting? = nil,
        canManageMeeting: Bool? = nil,
        hasOlderMessages: Bool? = nil,
        readReceipts: MessagingReadReceiptSettings? = nil,
        reactionOptions: [String]? = nil
    ) {
        self.id = id
        self.conversationType = conversationType
        self.title = title
        self.participants = participants
        self.messages = messages
        self.isMuted = isMuted
        self.isClosed = isClosed
        self.canManageMembers = canManageMembers
        self.purpose = purpose
        self.groupAvatar = groupAvatar
        self.canManageCollaborators = canManageCollaborators
        self.canDeleteGroup = canDeleteGroup
        self.isPromoted = isPromoted
        self.promotionStartedUTC = promotionStartedUTC
        self.promotionEndedUTC = promotionEndedUTC
        self.canManagePromotion = canManagePromotion
        self.meeting = meeting
        self.canManageMeeting = canManageMeeting
        self.hasOlderMessages = hasOlderMessages
        self.readReceipts = readReceipts
        self.reactionOptions = reactionOptions
    }
}

struct MessagingReadReceiptSettings: Codable, Equatable, Sendable {
    let globalEnabled: Bool
    let conversationEnabled: Bool
    let readers: [MessagingReadReceipt]
}
struct MessagingReadReceipt: Codable, Equatable, Sendable {
    let userId: String
    let participantType: String
    let readThroughUtc: Date
}
struct MessagingReadReceiptRequest: Encodable { let enabled: Bool; let globally: Bool }

struct MessagingGroupMeeting: Codable, Equatable, Sendable {
    let host: MessagingParticipant
    let linkLabel: String?
    let linkURL: String?
    let schedule: MessagingGroupMeetingSchedule?

    private enum CodingKeys: String, CodingKey {
        case host, linkLabel, schedule
        case linkURL = "linkUrl"
    }
}

struct MessagingGroupMeetingSchedule: Codable, Equatable, Sendable {
    let frequency: String
    let weekdays: [String]
    let localTime: String?
    let timeZoneID: String?
    let startsUTC: Date?
    let customDescription: String?

    private enum CodingKeys: String, CodingKey {
        case frequency, weekdays, localTime, customDescription
        case timeZoneID = "timeZoneId"
        case startsUTC = "startsUtc"
    }
}

struct ConversationMessage: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let conversationID: UUID
    let sender: MessagingParticipant
    let body: String
    let sentUTC: Date
    let attachments: [MessagingAttachment]
    let isMine: Bool
    let isDeleted: Bool
    let reply: MessageReplyPreview?
    let verificationReview: VerificationReview?
    let translation: MessageTranslationPresentation?
    let originalBody: String?
    var reactions: [MessageReaction]
    let sharedContent: MessagingSharedContent?

    private enum CodingKeys: String, CodingKey {
        case id
        case conversationID = "conversationId"
        case sender
        case body
        case sentUTC = "sentUtc"
        case attachments
        case isMine
        case isDeleted
        case reply
        case verificationReview
        case translation
        case reactions, sharedContent
        case originalBody
    }

    init(
        id: UUID,
        conversationID: UUID,
        sender: MessagingParticipant,
        body: String,
        sentUTC: Date,
        attachments: [MessagingAttachment],
        isMine: Bool,
        isDeleted: Bool = false,
        reply: MessageReplyPreview?,
        verificationReview: VerificationReview? = nil,
        translation: MessageTranslationPresentation? = nil,
        originalBody: String? = nil,
        reactions: [MessageReaction] = [],
        sharedContent: MessagingSharedContent? = nil
    ) {
        self.id = id
        self.conversationID = conversationID
        self.sender = sender
        self.body = body
        self.sentUTC = sentUTC
        self.attachments = attachments
        self.isMine = isMine
        self.isDeleted = isDeleted
        self.reply = reply
        self.verificationReview = verificationReview
        self.translation = translation
        self.originalBody = originalBody
        self.reactions = reactions
        self.sharedContent = sharedContent
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        conversationID = try container.decode(UUID.self, forKey: .conversationID)
        sender = try container.decode(MessagingParticipant.self, forKey: .sender)
        body = try container.decode(String.self, forKey: .body)
        sentUTC = try container.decode(Date.self, forKey: .sentUTC)
        attachments = try container.decode([MessagingAttachment].self, forKey: .attachments)
        isMine = try container.decode(Bool.self, forKey: .isMine)
        isDeleted = try container.decodeIfPresent(Bool.self, forKey: .isDeleted) ?? false
        reply = try container.decodeIfPresent(MessageReplyPreview.self, forKey: .reply)
        verificationReview = try container.decodeIfPresent(VerificationReview.self, forKey: .verificationReview)
        translation = try container.decodeIfPresent(MessageTranslationPresentation.self, forKey: .translation)
        originalBody = try container.decodeIfPresent(String.self, forKey: .originalBody)
        reactions = try container.decodeIfPresent([MessageReaction].self, forKey: .reactions) ?? []
        sharedContent = try container.decodeIfPresent(MessagingSharedContent.self, forKey: .sharedContent)
    }
}

struct MessageTranslationPresentation: Codable, Equatable, Sendable {
    let originalLanguage: String
    let targetLanguage: String
    let provider: String
}

struct VerificationReview: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let requesterUserID: String
    let requesterParticipantType: ParticipantType
    let status: String
    let requestedUTC: Date
    let canResolve: Bool
    let resourceType: ControlledResourceType

    private enum CodingKeys: String, CodingKey {
        case id
        case requesterUserID = "requesterUserId"
        case requesterParticipantType
        case status
        case requestedUTC = "requestedUtc"
        case canResolve
        case resourceType
    }

    init(
        id: UUID,
        requesterUserID: String,
        requesterParticipantType: ParticipantType,
        status: String,
        requestedUTC: Date,
        canResolve: Bool,
        resourceType: ControlledResourceType = .verificationBadge
    ) {
        self.id = id
        self.requesterUserID = requesterUserID
        self.requesterParticipantType = requesterParticipantType
        self.status = status
        self.requestedUTC = requestedUTC
        self.canResolve = canResolve
        self.resourceType = resourceType
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try container.decode(UUID.self, forKey: .id),
            requesterUserID: try container.decode(String.self, forKey: .requesterUserID),
            requesterParticipantType: try container.decode(ParticipantType.self, forKey: .requesterParticipantType),
            status: try container.decode(String.self, forKey: .status),
            requestedUTC: try container.decode(Date.self, forKey: .requestedUTC),
            canResolve: try container.decode(Bool.self, forKey: .canResolve),
            resourceType: try container.decodeIfPresent(ControlledResourceType.self, forKey: .resourceType) ?? .verificationBadge)
    }
}

struct VerificationRequestSubmission: Codable, Equatable, Sendable {
    let id: UUID
    let status: String
    let requestedUTC: Date
    let resourceType: ControlledResourceType

    private enum CodingKeys: String, CodingKey {
        case id
        case status
        case requestedUTC = "requestedUtc"
        case resourceType
    }

    init(id: UUID, status: String, requestedUTC: Date, resourceType: ControlledResourceType = .verificationBadge) {
        self.id = id
        self.status = status
        self.requestedUTC = requestedUTC
        self.resourceType = resourceType
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try container.decode(UUID.self, forKey: .id),
            status: try container.decode(String.self, forKey: .status),
            requestedUTC: try container.decode(Date.self, forKey: .requestedUTC),
            resourceType: try container.decodeIfPresent(ControlledResourceType.self, forKey: .resourceType) ?? .verificationBadge)
    }
}

/// A server-owned Activity item for an administrative request outcome. It is
/// deliberately separate from messaging conversations, so review decisions do
/// not create requester threads or reveal the private Founder queue.
struct MobileActivityNotification: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let kind: String
    let title: String
    let detail: String
    let occurredUTC: Date
    let controlledResourceRequestID: UUID?

    private enum CodingKeys: String, CodingKey {
        case id, kind, title, detail
        case occurredUTC = "occurredUtc"
        case controlledResourceRequestID = "controlledResourceRequestId"
    }
}

enum ControlledResourceType: String, Codable, Identifiable, Sendable {
    case verificationBadge = "VerificationBadge"
    case languageTranslation = "LanguageTranslation"
    case scriptureManagement = "ScriptureManagement"
    case communityManagement = "CommunityManagement"
    case socialContentPriority = "SocialContentPriority"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .verificationBadge: LegendLocalized("Legend verification")
        case .languageTranslation: LegendLocalized("Language Translation Access")
        case .scriptureManagement: LegendLocalized("Daily Scripture Management")
        case .communityManagement: LegendLocalized("Community Manager")
        case .socialContentPriority: LegendLocalized("Featured Creator")
        }
    }
}

/// A language choice supplied by the server after the member has been granted
/// Language Translation Access. The app never invents its own accepted codes.
struct LegendCommunicationLanguage: Codable, Equatable, Identifiable, Sendable {
    let code: String
    let displayName: String

    var id: String { code }
}

struct MessageReplyPreview: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let sender: MessagingParticipant
    let body: String
    let isDeleted: Bool
}

struct MessagingAttachment: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let originalFileName: String
    let contentType: String
    let sizeBytes: Int64
    let scanStatus: String
    let createdUTC: Date
    let canDownload: Bool

    private enum CodingKeys: String, CodingKey {
        case id
        case originalFileName
        case contentType
        case sizeBytes
        case scanStatus
        case createdUTC = "createdUtc"
        case canDownload
    }
}

struct MessagingAttachmentDraft: Identifiable, Equatable, Sendable {
    enum State: Equatable, Sendable {
        case ready
        case uploading
        case failed(String)
    }

    let id: UUID
    let fileName: String
    let contentType: String
    let data: Data
    var state: State
    var acknowledgedMessageID: UUID?

    func canUpload(to messageID: UUID) -> Bool {
        acknowledgedMessageID == messageID && state != .uploading
    }

    static func hasPendingAcknowledgedUploads(_ attachments: [Self]) -> Bool {
        attachments.contains { $0.acknowledgedMessageID != nil }
    }

    init(
        id: UUID = UUID(),
        fileName: String,
        contentType: String,
        data: Data,
        state: State = .ready
    ) {
        self.id = id
        self.fileName = fileName
        self.contentType = contentType
        self.data = data
        self.state = state
    }
}

/// Captures the composer submission without owning message delivery state.
struct MessagingDraftSnapshot {
    let body: String
    let replyToMessageID: UUID?

    func matches(body: String, replyToMessageID: UUID?) -> Bool {
        self.body == body && self.replyToMessageID == replyToMessageID
    }
}

struct SendMessageRequest: Encodable, Sendable {
    let body: String
    let replyToMessageID: UUID?
    let clientMessageID: UUID
    var sharedPostId: UUID? = nil

    private enum CodingKeys: String, CodingKey {
        case body, sharedPostId
        case replyToMessageID = "replyToMessageId"
        case clientMessageID = "clientMessageId"
    }
}

private struct ControlledResourceGrantRequest: Encodable, Sendable {
    let targetUserID: String
    let targetParticipantType: ParticipantType
    let isGranted: Bool

    private enum CodingKeys: String, CodingKey {
        case targetUserID = "targetUserId"
        case targetParticipantType
        case isGranted
    }
}

struct MessagingRecipient: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let email: String?
    let roleLabel: String?
    let relationshipLabel: String?
    let existingConversationID: UUID?
    let avatar: ProfileAvatar?
    var isVerified: Bool? = nil
    let resourceType: ControlledResourceType? = nil
    let resourceAccessState: String? = nil

    var id: LogicalParticipantIdentity { identity }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName
        case email
        case roleLabel
        case relationshipLabel
        case avatar
        case isVerified
        case existingConversationID = "existingConversationId"
        case resourceType
        case resourceAccessState
    }
}

enum MessagingRecipientScope: String, CaseIterable, Identifiable, Sendable {
    case clients = "Clients"
    case agents = "Agents"
    case leads = "Leads"

    var id: String { rawValue }
    var title: String {
        switch self {
        case .clients: LegendLocalized("Clients")
        case .agents: LegendLocalized("Agents")
        case .leads: LegendLocalized("Leads")
        }
    }
    var icon: String {
        switch self {
        case .clients: "person.2.fill"
        case .agents: "briefcase.fill"
        case .leads: "target"
        }
    }
}

struct StartConversationRequest: Encodable, Sendable {
    let targetUserID: String
    let targetParticipantType: ParticipantType
    let initialMessageBody: String?

    private enum CodingKeys: String, CodingKey {
        case targetUserID = "targetUserId"
        case targetParticipantType = "targetParticipantType"
        case initialMessageBody
    }
}

struct MessagingGroupMemberRequest: Encodable, Sendable {
    let userID: String
    let participantType: ParticipantType

    private enum CodingKeys: String, CodingKey {
        case userID = "userId"
        case participantType
    }
}

struct MessagingGroupCollaboratorRequest: Encodable, Sendable {
    let userID: String
    let participantType: ParticipantType
    let isManager: Bool

    private enum CodingKeys: String, CodingKey {
        case userID = "userId"
        case participantType
        case isManager
    }
}

struct CreateMessagingGroupRequest: Encodable, Sendable {
    let subject: String
    let participants: [MessagingGroupMemberRequest]
    let initialMessageBody: String?
    let groupImage: MessagingGroupImageRequest?
    let meeting: MessagingGroupMeetingRequest?

    private enum CodingKeys: String, CodingKey {
        case subject, participants, initialMessageBody, groupImage, meeting
    }
}

struct MessagingGroupImageRequest: Codable, Sendable {
    let contentType: String
    let base64Content: String
}

struct UpdateMessagingGroupRequest: Encodable, Sendable {
    let subject: String
    let groupImage: MessagingGroupImageRequest?
    let meeting: MessagingGroupMeetingRequest?
}

struct MessagingGroupMeetingRequest: Encodable, Sendable {
    let host: MessagingGroupMemberRequest?
    let linkLabel: String?
    let linkURL: String?
    let schedule: MessagingGroupMeetingScheduleRequest?

    private enum CodingKeys: String, CodingKey {
        case host, linkLabel, schedule
        case linkURL = "linkUrl"
    }
}

struct MessagingGroupMeetingScheduleRequest: Encodable, Sendable {
    let frequency: String
    let weekdays: [String]
    let localTime: String?
    let timeZoneID: String?
    let startsUTC: Date?
    let customDescription: String?

    private enum CodingKeys: String, CodingKey {
        case frequency, weekdays, localTime, customDescription
        case timeZoneID = "timeZoneId"
        case startsUTC = "startsUtc"
    }
}

struct MessagingGroupPromotionRequest: Encodable, Sendable {
    let isPromoted: Bool
}

struct ConversationPinnedRequest: Encodable, Sendable {
    let isPinned: Bool
}

struct ConversationMutedRequest: Encodable, Sendable {
    let isMuted: Bool
}

struct ResolveVerificationRequest: Encodable, Sendable {
    let approve: Bool
    let note: String?
}

struct FounderManagedAccount: Codable, Equatable, Identifiable, Sendable {
    let profileID: UUID
    let userID: String
    let participantType: ParticipantType
    let displayName: String
    let email: String?
    let lifecycleState: String
    let hasCancelableSubscription: Bool
    let isActive: Bool
    var canRestore: Bool? = nil

    var id: UUID { profileID }

    private enum CodingKeys: String, CodingKey {
        case userID = "userId"
        case profileID = "profileId"
        case participantType, displayName, email, lifecycleState
        case hasCancelableSubscription, isActive, canRestore
    }
}

struct FounderAccountRemovalRequest: Encodable, Sendable {
    let profileID: UUID
    let participantType: ParticipantType
    let confirmation: String

    private enum CodingKeys: String, CodingKey {
        case profileID = "profileId"
        case participantType, confirmation
    }
}

struct FounderAccountRemovalOutcome: Codable, Equatable, Sendable {
    let completed: Bool
    let message: String
    let lifecycleState: String
}

enum FounderAccountDirectoryScope: String, Sendable {
    case active
    case archive
}

struct FounderAccountTargetRequest: Encodable, Sendable {
    let profileID: UUID
    let participantType: ParticipantType

    private enum CodingKeys: String, CodingKey {
        case profileID = "profileId"
        case participantType
    }
}

struct FounderAccountBatchRequest: Encodable, Sendable {
    let accounts: [FounderAccountTargetRequest]
    let confirmation: String
}

struct FounderAccountBatchItemOutcome: Codable, Equatable, Sendable {
    let succeeded: Bool
    let completed: Bool
    let errorCode: String?
    let message: String
    let lifecycleState: String
}

struct FounderAccountBatchOutcome: Codable, Equatable, Sendable {
    let completedCount: Int
    let failedCount: Int
    let results: [FounderAccountBatchItemOutcome]
}

struct MessagingReactionPreferences: Codable, Sendable {
    let preferredReactionSkinTone: Int
}

protocol MessagingAPI: Sendable {
    func reactionPreferences(accessToken: String) async throws -> MessagingReactionPreferences
    func setReactionPreferences(_ preferences: MessagingReactionPreferences, accessToken: String) async throws -> MessagingReactionPreferences
    func sendShared(conversationID: UUID, body: String, sharedPostID: UUID, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage
    func downloadAttachment(_ attachment: MessagingAttachment, accessToken: String) async throws -> URL
    func sharedPostURL(_ postID: UUID) -> URL?
    func markRead(conversationID: UUID, readThroughMessageID: UUID, accessToken: String) async throws

    func react(conversationID: UUID, messageID: UUID, emoji: String?, accessToken: String) async throws -> MessageReactionResult
    func setReadReceipts(conversationID: UUID, enabled: Bool, globally: Bool, accessToken: String) async throws
    func conversations(accessToken: String) async throws -> [ConversationSummary]
    func conversations(offset: Int, limit: Int, accessToken: String) async throws -> [ConversationSummary]
    func conversation(id: UUID, beforeUTC: Date?, accessToken: String) async throws -> ConversationDetail
    func conversation(id: UUID, beforeUTC: Date?, beforeMessageID: UUID?, accessToken: String) async throws -> ConversationDetail
    func recipients(
        search: String?,
        scope: MessagingRecipientScope?,
        accessToken: String
    ) async throws -> [MessagingRecipient]
    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail
    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        accessToken: String
    ) async throws -> ConversationDetail
    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws -> ConversationDetail
    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission
    func startControlledResourceRequest(
        resourceType: ControlledResourceType,
        accessToken: String
    ) async throws -> VerificationRequestSubmission
    func communicationLanguages(accessToken: String) async throws -> [LegendCommunicationLanguage]
    func activityNotifications(accessToken: String) async throws -> [MobileActivityNotification]
    func controlledResourceRecipients(
        resourceType: ControlledResourceType,
        search: String?,
        accessToken: String
    ) async throws -> [MessagingRecipient]
    func setControlledResourceGrant(
        resourceType: ControlledResourceType,
        recipient: MessagingRecipient,
        isGranted: Bool,
        accessToken: String
    ) async throws
    func founderAccounts(search: String?, accessToken: String) async throws -> [FounderManagedAccount]
    func founderAccounts(
        search: String?,
        scope: FounderAccountDirectoryScope,
        accessToken: String
    ) async throws -> [FounderManagedAccount]
    func restoreFounderClient(account: FounderManagedAccount, accessToken: String) async throws -> FounderAccountRemovalOutcome
    func removeFounderAccount(
        account: FounderManagedAccount,
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountRemovalOutcome
    func removeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome
    func purgeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome
    func resolveControlledResourceRequest(
        requestID: UUID,
        approve: Bool,
        note: String?,
        accessToken: String
    ) async throws
    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        accessToken: String
    ) async throws
    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws
    func addGroupParticipant(
        conversationID: UUID,
        recipient: MessagingRecipient,
        accessToken: String
    ) async throws
    func setGroupCollaborator(
        conversationID: UUID,
        participant: LogicalParticipantIdentity,
        isManager: Bool,
        accessToken: String
    ) async throws
    func deleteGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws
    func setGroupPromotion(
        conversationID: UUID,
        isPromoted: Bool,
        accessToken: String
    ) async throws -> ConversationDetail
    func joinPromotedGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws -> ConversationDetail
    func resolveVerificationRequest(
        requestID: UUID,
        approve: Bool,
        accessToken: String
    ) async throws
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage]
    func send(
        conversationID: UUID,
        body: String,
        replyToMessageID: UUID?,
        clientMessageID: UUID,
        accessToken: String
    ) async throws -> ConversationMessage
    func upload(
        conversationID: UUID,
        messageID: UUID,
        attachment: MessagingAttachmentDraft,
        accessToken: String
    ) async throws -> MessagingAttachment
    func markRead(conversationID: UUID, accessToken: String) async throws
    func setPinned(conversationID: UUID, isPinned: Bool, accessToken: String) async throws
    func setMuted(conversationID: UUID, isMuted: Bool, accessToken: String) async throws
    func removeConversation(conversationID: UUID, accessToken: String) async throws
    func deleteMessage(conversationID: UUID, messageID: UUID, accessToken: String) async throws
}

extension MessagingAPI {
    func reactionPreferences(accessToken: String) async throws -> MessagingReactionPreferences {
        throw MobileMessagingContractError.unavailable
    }
    func setReactionPreferences(_ preferences: MessagingReactionPreferences, accessToken: String) async throws -> MessagingReactionPreferences {
        throw MobileMessagingContractError.unavailable
    }

    func sendShared(conversationID: UUID, body: String, sharedPostID: UUID, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage { throw MobileAPIError.invalidServerResponse }
    func downloadAttachment(_ attachment: MessagingAttachment, accessToken: String) async throws -> URL { throw MobileAPIError.invalidServerResponse }
    func sharedPostURL(_ postID: UUID) -> URL? { nil }
    func markRead(conversationID: UUID, readThroughMessageID: UUID, accessToken: String) async throws {
        try await markRead(conversationID: conversationID, accessToken: accessToken)
    }

    func react(conversationID: UUID, messageID: UUID, emoji: String?, accessToken: String) async throws -> MessageReactionResult {
        throw MobileMessagingContractError.unavailable
    }
    func setReadReceipts(conversationID: UUID, enabled: Bool, globally: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    /// Older test doubles and integration implementations can continue to
    /// provide the original inbox contract. The production transport supplies
    /// the bounded server page below so the Messages landing screen never has
    /// to wait for the entire inbox.
    func conversations(
        offset: Int,
        limit: Int,
        accessToken: String
    ) async throws -> [ConversationSummary] {
        try await conversations(accessToken: accessToken)
    }

    func conversation(id: UUID, beforeUTC: Date?, beforeMessageID: UUID?, accessToken: String) async throws -> ConversationDetail {
        try await conversation(id: id, beforeUTC: beforeUTC, accessToken: accessToken)
    }

    func conversation(
        id: UUID,
        beforeUTC: Date?,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await conversation(id: id, accessToken: accessToken)
    }

    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await createGroup(
            subject: subject,
            recipients: recipients,
            groupImage: groupImage,
            accessToken: accessToken)
    }

    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest? = nil,
        accessToken: String
    ) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission {
        throw MobileMessagingContractError.unavailable
    }

    func startControlledResourceRequest(
        resourceType: ControlledResourceType,
        accessToken: String
    ) async throws -> VerificationRequestSubmission {
        throw MobileMessagingContractError.unavailable
    }

    func communicationLanguages(accessToken: String) async throws -> [LegendCommunicationLanguage] {
        throw MobileMessagingContractError.unavailable
    }

    func activityNotifications(accessToken: String) async throws -> [MobileActivityNotification] {
        throw MobileMessagingContractError.unavailable
    }

    func controlledResourceRecipients(
        resourceType: ControlledResourceType,
        search: String?,
        accessToken: String
    ) async throws -> [MessagingRecipient] {
        throw MobileMessagingContractError.unavailable
    }

    func setControlledResourceGrant(
        resourceType: ControlledResourceType,
        recipient: MessagingRecipient,
        isGranted: Bool,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func founderAccounts(search: String?, accessToken: String) async throws -> [FounderManagedAccount] {
        throw MobileMessagingContractError.unavailable
    }

    func founderAccounts(
        search: String?,
        scope: FounderAccountDirectoryScope,
        accessToken: String
    ) async throws -> [FounderManagedAccount] {
        try await founderAccounts(search: search, accessToken: accessToken)
    }

    func restoreFounderClient(account: FounderManagedAccount, accessToken: String) async throws -> FounderAccountRemovalOutcome { throw MobileAPIError.invalidServerResponse }

    func removeFounderAccount(
        account: FounderManagedAccount,
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountRemovalOutcome {
        throw MobileMessagingContractError.unavailable
    }

    func removeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome {
        throw MobileMessagingContractError.unavailable
    }

    func purgeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome {
        throw MobileMessagingContractError.unavailable
    }

    func resolveControlledResourceRequest(
        requestID: UUID,
        approve: Bool,
        note: String?,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws {
        try await updateGroup(
            conversationID: conversationID,
            subject: subject,
            groupImage: groupImage,
            accessToken: accessToken)
    }

    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func addGroupParticipant(
        conversationID: UUID,
        recipient: MessagingRecipient,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setGroupCollaborator(
        conversationID: UUID,
        participant: LogicalParticipantIdentity,
        isManager: Bool,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func deleteGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setGroupPromotion(
        conversationID: UUID,
        isPromoted: Bool,
        accessToken: String
    ) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func joinPromotedGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func resolveVerificationRequest(
        requestID: UUID,
        approve: Bool,
        accessToken: String
    ) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setPinned(conversationID: UUID, isPinned: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setMuted(conversationID: UUID, isMuted: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func removeConversation(conversationID: UUID, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func deleteMessage(conversationID: UUID, messageID: UUID, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

}

struct MobileContractUnavailableMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileMessagingContractError.unavailable
    }

    func recipients(
        search: String?,
        scope: MessagingRecipientScope?,
        accessToken: String
    ) async throws -> [MessagingRecipient] {
        throw MobileMessagingContractError.unavailable
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func createGroup(subject: String, recipients: [MessagingRecipient], groupImage: MessagingGroupImageRequest?, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission {
        throw MobileMessagingContractError.unavailable
    }

    func updateGroup(conversationID: UUID, subject: String, groupImage: MessagingGroupImageRequest?, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func addGroupParticipant(conversationID: UUID, recipient: MessagingRecipient, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func resolveVerificationRequest(requestID: UUID, approve: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileMessagingContractError.unavailable
    }

    func send(
        conversationID: UUID,
        body: String,
        replyToMessageID: UUID?,
        clientMessageID: UUID,
        accessToken: String
    ) async throws -> ConversationMessage {
        throw MobileMessagingContractError.unavailable
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileMessagingContractError.unavailable
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setPinned(conversationID: UUID, isPinned: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func setMuted(conversationID: UUID, isMuted: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func removeConversation(conversationID: UUID, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func deleteMessage(conversationID: UUID, messageID: UUID, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

}

enum MobileMessagingContractError: LocalizedError, Equatable {
    case unavailable

    var errorDescription: String? {
        LegendLocalized("Secure mobile messaging is waiting for the approved server contract.")
    }
}

struct URLSessionMessagingAPI: MessagingAPI {
    func react(conversationID: UUID, messageID: UUID, emoji: String?, accessToken: String) async throws -> MessageReactionResult {
        let path = "/api/v1/mobile/messaging/conversations/\(conversationID)/messages/\(messageID)/reaction"
        if let emoji {
            return try await client.put(path, body: MessageReactionRequest(emoji: emoji), accessToken: accessToken,
                headers: participantHeader, response: MessageReactionResult.self)
        }
        return try await client.delete(path, accessToken: accessToken, headers: participantHeader, response: MessageReactionResult.self)
    }

    let client: MobileHTTPClient
    let participantType: ParticipantType

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }

    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        try await conversations(offset: 0, limit: 24, accessToken: accessToken)
    }

    func conversations(
        offset: Int,
        limit: Int,
        accessToken: String
    ) async throws -> [ConversationSummary] {
        try await client.get(
            "/api/v1/mobile/messaging/conversations",
            accessToken: accessToken,
            queryItems: [
                URLQueryItem(name: "take", value: "\(max(1, min(limit, 50)))"),
                URLQueryItem(name: "skip", value: "\(max(0, offset))")
            ],
            headers: participantHeader,
            response: [ConversationSummary].self
        )
    }

    func recipients(
        search: String?,
        scope: MessagingRecipientScope?,
        accessToken: String
    ) async throws -> [MessagingRecipient] {
        var queryItems: [URLQueryItem] = []
        if search?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false {
            queryItems.append(URLQueryItem(name: "search", value: search))
        }
        if let scope {
            queryItems.append(URLQueryItem(name: "scope", value: scope.rawValue))
        }
        return try await client.get(
            "/api/v1/mobile/messaging/recipients",
            accessToken: accessToken,
            queryItems: queryItems,
            headers: participantHeader,
            response: [MessagingRecipient].self)
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        try await client.post(
            "/api/v1/mobile/messaging/conversations",
            body: StartConversationRequest(
                targetUserID: recipient.identity.userID,
                targetParticipantType: recipient.identity.participantType,
                initialMessageBody: nil),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: ConversationDetail.self)
    }

    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await createGroup(
            subject: subject,
            recipients: recipients,
            groupImage: groupImage,
            meeting: nil,
            accessToken: accessToken)
    }

    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await client.post(
            "/api/v1/mobile/messaging/groups",
            body: CreateMessagingGroupRequest(
                subject: subject,
                participants: recipients.map {
                    MessagingGroupMemberRequest(
                        userID: $0.identity.userID,
                        participantType: $0.identity.participantType)
                },
                initialMessageBody: nil,
                groupImage: groupImage,
                meeting: meeting),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: ConversationDetail.self)
    }

    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission {
        try await client.post(
            "/api/v1/mobile/messaging/verification-requests",
            body: EmptyMobileRequest(),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: VerificationRequestSubmission.self)
    }

    func startControlledResourceRequest(
        resourceType: ControlledResourceType,
        accessToken: String
    ) async throws -> VerificationRequestSubmission {
        try await client.post(
            "/api/v1/mobile/messaging/controlled-resources/\(resourceType.rawValue)/requests",
            body: EmptyMobileRequest(),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: VerificationRequestSubmission.self)
    }

    func communicationLanguages(accessToken: String) async throws -> [LegendCommunicationLanguage] {
        try await client.get(
            "/api/v1/mobile/messaging/controlled-resources/languages",
            accessToken: accessToken,
            headers: participantHeader,
            response: [LegendCommunicationLanguage].self)
    }

    func activityNotifications(accessToken: String) async throws -> [MobileActivityNotification] {
        try await client.get(
            "/api/v1/mobile/messaging/activity",
            accessToken: accessToken,
            headers: participantHeader,
            response: [MobileActivityNotification].self)
    }

    func controlledResourceRecipients(
        resourceType: ControlledResourceType,
        search: String?,
        accessToken: String
    ) async throws -> [MessagingRecipient] {
        var queryItems: [URLQueryItem] = []
        if search?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false {
            queryItems.append(URLQueryItem(name: "search", value: search))
        }
        return try await client.get(
            "/api/v1/mobile/messaging/controlled-resources/\(resourceType.rawValue)/recipients",
            accessToken: accessToken,
            queryItems: queryItems,
            headers: participantHeader,
            response: [MessagingRecipient].self)
    }

    func setControlledResourceGrant(
        resourceType: ControlledResourceType,
        recipient: MessagingRecipient,
        isGranted: Bool,
        accessToken: String
    ) async throws {
        try await client.put(
            "/api/v1/mobile/messaging/controlled-resources/\(resourceType.rawValue)/recipients",
            body: ControlledResourceGrantRequest(
                targetUserID: recipient.identity.userID,
                targetParticipantType: recipient.identity.participantType,
                isGranted: isGranted),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func founderAccounts(search: String?, accessToken: String) async throws -> [FounderManagedAccount] {
        try await founderAccounts(search: search, scope: .active, accessToken: accessToken)
    }

    func founderAccounts(
        search: String?,
        scope: FounderAccountDirectoryScope,
        accessToken: String
    ) async throws -> [FounderManagedAccount] {
        var queryItems = [URLQueryItem(name: "take", value: "100")]
        queryItems.append(URLQueryItem(name: "scope", value: scope.rawValue))
        if search?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false {
            queryItems.append(URLQueryItem(name: "search", value: search))
        }
        return try await client.get(
            "/api/v1/mobile/founder/accounts",
            accessToken: accessToken,
            queryItems: queryItems,
            headers: participantHeader,
            response: [FounderManagedAccount].self)
    }

    func restoreFounderClient(account: FounderManagedAccount, accessToken: String) async throws -> FounderAccountRemovalOutcome {
        try await client.post("/api/v1/mobile/founder/accounts/restore",
            body: FounderAccountRemovalRequest(profileID: account.profileID, participantType: account.participantType, confirmation: "RESTORE"),
            accessToken: accessToken, idempotencyKey: UUID(), headers: participantHeader, response: FounderAccountRemovalOutcome.self)
    }

    func removeFounderAccount(
        account: FounderManagedAccount,
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountRemovalOutcome {
        try await client.post(
            "/api/v1/mobile/founder/accounts/remove",
            body: FounderAccountRemovalRequest(
                profileID: account.profileID,
                participantType: account.participantType,
                confirmation: confirmation),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: FounderAccountRemovalOutcome.self)
    }

    func removeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome {
        try await client.post(
            "/api/v1/mobile/founder/accounts/remove-batch",
            body: FounderAccountBatchRequest(
                accounts: accounts.map {
                    FounderAccountTargetRequest(
                        profileID: $0.profileID,
                        participantType: $0.participantType)
                },
                confirmation: confirmation),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: FounderAccountBatchOutcome.self)
    }

    func purgeFounderAccounts(
        accounts: [FounderManagedAccount],
        confirmation: String,
        accessToken: String
    ) async throws -> FounderAccountBatchOutcome {
        try await client.post(
            "/api/v1/mobile/founder/accounts/archive/purge",
            body: FounderAccountBatchRequest(
                accounts: accounts.map {
                    FounderAccountTargetRequest(
                        profileID: $0.profileID,
                        participantType: $0.participantType)
                },
                confirmation: confirmation),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: FounderAccountBatchOutcome.self)
    }

    func resolveControlledResourceRequest(
        requestID: UUID,
        approve: Bool,
        note: String?,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/messaging/controlled-resource-requests/\(requestID.uuidString)/resolution",
            body: ResolveVerificationRequest(approve: approve, note: note),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        accessToken: String
    ) async throws {
        try await updateGroup(
            conversationID: conversationID,
            subject: subject,
            groupImage: groupImage,
            meeting: nil,
            accessToken: accessToken)
    }

    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        accessToken: String
    ) async throws {
        try await client.put(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)",
            body: UpdateMessagingGroupRequest(
                subject: subject,
                groupImage: groupImage,
                meeting: meeting),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func addGroupParticipant(
        conversationID: UUID,
        recipient: MessagingRecipient,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/participants",
            body: MessagingGroupMemberRequest(
                userID: recipient.identity.userID,
                participantType: recipient.identity.participantType),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func setGroupCollaborator(
        conversationID: UUID,
        participant: LogicalParticipantIdentity,
        isManager: Bool,
        accessToken: String
    ) async throws {
        try await client.put(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)/collaborators",
            body: MessagingGroupCollaboratorRequest(
                userID: participant.userID,
                participantType: participant.participantType,
                isManager: isManager),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func deleteGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws {
        try await client.delete(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

    func setGroupPromotion(
        conversationID: UUID,
        isPromoted: Bool,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await client.put(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)/promotion",
            body: MessagingGroupPromotionRequest(isPromoted: isPromoted),
            accessToken: accessToken,
            headers: participantHeader,
            response: ConversationDetail.self)
    }

    func joinPromotedGroup(
        conversationID: UUID,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await client.post(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)/join",
            body: EmptyMobileRequest(),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: ConversationDetail.self)
    }

    func resolveVerificationRequest(
        requestID: UUID,
        approve: Bool,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/messaging/verification-requests/\(requestID.uuidString)/resolution",
            body: ResolveVerificationRequest(approve: approve, note: nil),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        try await conversation(
            id: id,
            beforeUTC: nil,
            accessToken: accessToken)
    }

    func conversation(
        id: UUID,
        beforeUTC: Date?,
        accessToken: String
    ) async throws -> ConversationDetail {
        try await conversation(id: id, beforeUTC: beforeUTC, beforeMessageID: nil, accessToken: accessToken)
    }

    func conversation(id: UUID, beforeUTC: Date?, beforeMessageID: UUID?, accessToken: String) async throws -> ConversationDetail {
        var queryItems = [URLQueryItem(name: "take", value: "60")]
        if let beforeMessageID {
            queryItems.append(URLQueryItem(name: "beforeMessageId", value: beforeMessageID.uuidString))
        }
        if let beforeUTC {
            queryItems.append(URLQueryItem(
                name: "beforeUtc",
                value: ISO8601DateFormatter().string(from: beforeUTC)))
        }
        return try await client.get(
            "/api/v1/mobile/messaging/conversations/\(id.uuidString)",
            accessToken: accessToken,
            queryItems: queryItems,
            headers: participantHeader,
            response: ConversationDetail.self
        )
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        try await client.get(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "take", value: "60")],
            headers: participantHeader,
            response: [ConversationMessage].self
        )
    }


    func sharedPostURL(_ postID: UUID) -> URL? {
        client.baseURL.appendingPathComponent("Social/Posts").appendingPathComponent(postID.uuidString)
    }

    func sendShared(conversationID: UUID, body: String, sharedPostID: UUID, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage {
        try await client.post("/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            body: SendMessageRequest(body: body, replyToMessageID: nil, clientMessageID: clientMessageID, sharedPostId: sharedPostID),
            accessToken: accessToken, idempotencyKey: clientMessageID, headers: participantHeader, response: ConversationMessage.self)
    }

    func downloadAttachment(_ attachment: MessagingAttachment, accessToken: String) async throws -> URL {
        guard attachment.canDownload else { throw MobileAPIError.invalidServerResponse }
        let temporary = try await client.downloadFile("/api/v1/mobile/messaging/attachments/\(attachment.id.uuidString)",
            accessToken: accessToken, headers: participantHeader)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let filename = (attachment.originalFileName as NSString).lastPathComponent
        let destination = directory.appendingPathComponent(filename.isEmpty ? "attachment" : filename)
        try FileManager.default.moveItem(at: temporary, to: destination)
        return destination
    }

    func markRead(conversationID: UUID, readThroughMessageID: UUID, accessToken: String) async throws {
        try await client.post("/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/read?readThroughMessageId=\(readThroughMessageID.uuidString)",
            body: EmptyMobileRequest(), accessToken: accessToken, headers: participantHeader)
    }

    func send(
        conversationID: UUID,
        body: String,
        replyToMessageID: UUID?,
        clientMessageID: UUID,
        accessToken: String
    ) async throws -> ConversationMessage {
        try await client.post(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            body: SendMessageRequest(
                body: body,
                replyToMessageID: replyToMessageID,
                clientMessageID: clientMessageID),
            accessToken: accessToken,
            idempotencyKey: clientMessageID,
            headers: participantHeader,
            response: ConversationMessage.self
        )
    }

    func upload(
        conversationID: UUID,
        messageID: UUID,
        attachment: MessagingAttachmentDraft,
        accessToken: String
    ) async throws -> MessagingAttachment {
        try await client.postMultipart(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages/\(messageID.uuidString)/attachments",
            accessToken: accessToken,
            fields: [:],
            files: [MultipartFormFile(
                fieldName: "file",
                fileName: attachment.fileName,
                mimeType: attachment.contentType,
                data: attachment.data)],
            headers: participantHeader,
            response: MessagingAttachment.self)
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        try await client.post(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/read",
            body: EmptyMobileRequest(),
            accessToken: accessToken,
            headers: participantHeader
        )
    }

    func setPinned(conversationID: UUID, isPinned: Bool, accessToken: String) async throws {
        try await client.put(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/pin",
            body: ConversationPinnedRequest(isPinned: isPinned),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func reactionPreferences(accessToken: String) async throws -> MessagingReactionPreferences {
        try await client.get("/api/v1/mobile/messaging/reaction-preferences", accessToken: accessToken,
            headers: participantHeader, response: MessagingReactionPreferences.self)
    }

    func setReactionPreferences(_ preferences: MessagingReactionPreferences, accessToken: String) async throws -> MessagingReactionPreferences {
        try await client.put("/api/v1/mobile/messaging/reaction-preferences", body: preferences,
            accessToken: accessToken, headers: participantHeader, response: MessagingReactionPreferences.self)
    }

    func setReadReceipts(conversationID: UUID, enabled: Bool, globally: Bool, accessToken: String) async throws {
        try await client.put("/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/read-receipts",
            body: MessagingReadReceiptRequest(enabled: enabled, globally: globally),
            accessToken: accessToken, headers: participantHeader)
    }

    func setMuted(conversationID: UUID, isMuted: Bool, accessToken: String) async throws {
        try await client.put(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/mute",
            body: ConversationMutedRequest(isMuted: isMuted),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func removeConversation(conversationID: UUID, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

    func deleteMessage(conversationID: UUID, messageID: UUID, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages/\(messageID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

}

private struct EmptyMobileRequest: Encodable {}

/// The server owns reader privacy and chronological ordering; this only reduces redundant labels.
func messageReceiptLabels(messages: [ConversationMessage], readers: [MessagingReadReceipt]) -> [UUID: String] {
    let own = messages.filter { $0.isMine && !$0.isDeleted }
    let latestRead = own.lastIndex { message in
        readers.contains { reader in
            !(reader.userId.caseInsensitiveCompare(message.sender.identity.userID) == .orderedSame &&
              reader.participantType == message.sender.identity.participantType.rawValue) &&
                reader.readThroughUtc >= message.sentUTC
        }
    }
    return Dictionary(uniqueKeysWithValues: own.enumerated().compactMap { index, message in
        if index == latestRead { return (message.id, "Read") }
        if latestRead == nil || index > latestRead! { return (message.id, "Sent") }
        return nil
    })
}

struct MessageReaction: Codable, Equatable, Sendable { let emoji: String; let count: Int; let reactedByCurrentActor: Bool }
struct MessageReactionRequest: Encodable { let emoji: String }
struct MessageReactionResult: Decodable { let messageId: UUID; let reactions: [MessageReaction] }

struct MessagingSharedContent: Codable, Equatable, Sendable {
    let sourcePostId: UUID
    let status: String
    let contentType: String?
    let body: String?
    let authorDisplayName: String?
    let media: [MobileSocialMedia]
    let url: String
}

struct LegendReactionEmojiCatalog: Decodable {
    struct Entry: Decodable, Identifiable {
        let emoji: String
        let name: String
        let keywords: [String]
        let baseEmoji: String
        let skinToneVariants: [String: String]
        var id: String { emoji }
    }
    let entries: [Entry]
    private let entriesByEmoji: [String: Entry]
    private enum CodingKeys: String, CodingKey { case entries }
    init(from decoder: Decoder) throws {
        entries = try decoder.container(keyedBy: CodingKeys.self).decode([Entry].self, forKey: .entries)
        entriesByEmoji = Dictionary(entries.map { ($0.emoji, $0) }, uniquingKeysWith: { first, _ in first })
    }
    static let bundled: LegendReactionEmojiCatalog? = {
        guard let url = Bundle.main.url(forResource: "legend-reaction-emoji", withExtension: "json"),
              let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Self.self, from: data)
    }()
    static let skinToneKeys = ["default", "light", "mediumLight", "medium", "mediumDark", "dark"]
    func applyingSkinTone(_ tone: Int, to emoji: String) -> String {
        guard Self.skinToneKeys.indices.contains(tone), let entry = entriesByEmoji[emoji] else { return emoji }
        return entry.skinToneVariants[Self.skinToneKeys[tone]] ?? entry.baseEmoji
    }
    func palette(_ query: String) -> [Entry] {
        var bases = Set<String>()
        return search(query).filter { bases.insert($0.baseEmoji).inserted }
    }
    func search(_ query: String) -> [Entry] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return entries }
        let normalized = query.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
        let tokens = normalized.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
        return entries.filter { entry in
            entry.emoji == query || (!tokens.isEmpty && tokens.allSatisfy { token in entry.keywords.contains { $0.contains(token) } })
        }
    }
}
