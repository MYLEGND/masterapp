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
        canDeleteGroup: Bool? = nil
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
        originalBody: String? = nil
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

enum ControlledResourceType: String, Codable, Sendable {
    case verificationBadge = "VerificationBadge"
    case languageTranslation = "LanguageTranslation"

    var displayName: String {
        switch self {
        case .verificationBadge: "Legend verification"
        case .languageTranslation: "Language Translation Access"
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

struct SendMessageRequest: Encodable, Sendable {
    let body: String
    let replyToMessageID: UUID?

    private enum CodingKeys: String, CodingKey {
        case body
        case replyToMessageID = "replyToMessageId"
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

    private enum CodingKeys: String, CodingKey {
        case subject, participants, initialMessageBody, groupImage
    }
}

struct MessagingGroupImageRequest: Codable, Sendable {
    let contentType: String
    let base64Content: String
}

struct UpdateMessagingGroupRequest: Encodable, Sendable {
    let subject: String
    let groupImage: MessagingGroupImageRequest?
}

struct ConversationPinnedRequest: Encodable, Sendable {
    let isPinned: Bool
}

struct ConversationMutedRequest: Encodable, Sendable {
    let isMuted: Bool
}

struct ConversationCallOptions: Codable, Equatable, Sendable {
    let conversationID: UUID
    let displayName: String
    let phoneNumber: String?
    let faceTimeAddress: String?

    private enum CodingKeys: String, CodingKey {
        case conversationID = "conversationId"
        case displayName
        case phoneNumber
        case faceTimeAddress
    }
}

struct ResolveVerificationRequest: Encodable, Sendable {
    let approve: Bool
    let note: String?
}

protocol MessagingAPI: Sendable {
    func conversations(accessToken: String) async throws -> [ConversationSummary]
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
    func callOptions(conversationID: UUID, accessToken: String) async throws -> ConversationCallOptions
}

extension MessagingAPI {
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

    func callOptions(conversationID: UUID, accessToken: String) async throws -> ConversationCallOptions {
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

    func callOptions(conversationID: UUID, accessToken: String) async throws -> ConversationCallOptions {
        throw MobileMessagingContractError.unavailable
    }
}

enum MobileMessagingContractError: LocalizedError, Equatable {
    case unavailable

    var errorDescription: String? {
        "Secure mobile messaging is waiting for the approved server contract."
    }
}

struct URLSessionMessagingAPI: MessagingAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }

    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        try await client.get(
            "/api/v1/mobile/messaging/conversations",
            accessToken: accessToken,
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
                groupImage: groupImage),
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
        try await client.put(
            "/api/v1/mobile/messaging/groups/\(conversationID.uuidString)",
            body: UpdateMessagingGroupRequest(subject: subject, groupImage: groupImage),
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
        try await client.get(
            "/api/v1/mobile/messaging/conversations/\(id.uuidString)",
            accessToken: accessToken,
            headers: participantHeader,
            response: ConversationDetail.self
        )
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        try await client.get(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            accessToken: accessToken,
            headers: participantHeader,
            response: [ConversationMessage].self
        )
    }

    func send(
        conversationID: UUID,
        body: String,
        replyToMessageID: UUID?,
        accessToken: String
    ) async throws -> ConversationMessage {
        try await client.post(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            body: SendMessageRequest(
                body: body,
                replyToMessageID: replyToMessageID),
            accessToken: accessToken,
            idempotencyKey: UUID(),
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

    func callOptions(conversationID: UUID, accessToken: String) async throws -> ConversationCallOptions {
        try await client.get(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/call-options",
            accessToken: accessToken,
            headers: participantHeader,
            response: ConversationCallOptions.self)
    }
}

private struct EmptyMobileRequest: Encodable {}
