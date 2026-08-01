import Foundation

struct MessagingParticipant: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let roleLabel: String?
    let avatar: ProfileAvatar?
    var isVerified: Bool? = nil

    var id: LogicalParticipantIdentity { identity }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName
        case roleLabel
        case avatar
        case isVerified
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
    let purpose: String? = nil
    let groupAvatar: ProfileAvatar? = nil

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
    let purpose: String? = nil
    let groupAvatar: ProfileAvatar? = nil
}

struct ConversationMessage: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let conversationID: UUID
    let sender: MessagingParticipant
    let body: String
    let sentUTC: Date
    let attachments: [MessagingAttachment]
    let isMine: Bool
    let reply: MessageReplyPreview?
    let verificationReview: VerificationReview? = nil

    private enum CodingKeys: String, CodingKey {
        case id
        case conversationID = "conversationId"
        case sender
        case body
        case sentUTC = "sentUtc"
        case attachments
        case isMine
        case reply
        case verificationReview
    }
}

struct VerificationReview: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let requesterUserID: String
    let requesterParticipantType: ParticipantType
    let status: String
    let requestedUTC: Date
    let canResolve: Bool

    private enum CodingKeys: String, CodingKey {
        case id
        case requesterUserID = "requesterUserId"
        case requesterParticipantType
        case status
        case requestedUTC = "requestedUtc"
        case canResolve
    }
}

struct VerificationRequestSubmission: Codable, Equatable, Sendable {
    let id: UUID
    let status: String
    let requestedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id
        case status
        case requestedUTC = "requestedUtc"
    }
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

struct ResolveVerificationRequest: Encodable, Sendable {
    let approve: Bool
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

    func resolveVerificationRequest(
        requestID: UUID,
        approve: Bool,
        accessToken: String
    ) async throws {
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

    func resolveVerificationRequest(
        requestID: UUID,
        approve: Bool,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/messaging/verification-requests/\(requestID.uuidString)/resolution",
            body: ResolveVerificationRequest(approve: approve),
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
}

private struct EmptyMobileRequest: Encodable {}
