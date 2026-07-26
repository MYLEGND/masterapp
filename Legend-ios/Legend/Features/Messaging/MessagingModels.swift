import Foundation

struct MessagingParticipant: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let avatar: ProfileAvatar?

    var id: LogicalParticipantIdentity { identity }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName
        case avatar
    }
}

struct ConversationSummary: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let counterparty: MessagingParticipant
    let title: String
    let lastMessagePreview: String?
    let lastMessageUTC: Date?
    let unreadCount: Int
    let isClosed: Bool

    private enum CodingKeys: String, CodingKey {
        case id
        case title
        case counterparty
        case lastMessagePreview
        case lastMessageUTC = "lastMessageUtc"
        case unreadCount
        case isClosed
    }
}

struct ConversationDetail: Codable, Equatable, Sendable {
    let id: UUID
    let title: String
    let participants: [MessagingParticipant]
    let messages: [ConversationMessage]
    let isMuted: Bool
    let isClosed: Bool
}

struct ConversationMessage: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let conversationID: UUID
    let sender: MessagingParticipant
    let body: String
    let sentUTC: Date
    let isMine: Bool

    private enum CodingKeys: String, CodingKey {
        case id
        case conversationID = "conversationId"
        case sender
        case body
        case sentUTC = "sentUtc"
        case isMine
    }
}

struct SendMessageRequest: Encodable, Sendable {
    let body: String
}

protocol MessagingAPI: Sendable {
    func conversations(accessToken: String) async throws -> [ConversationSummary]
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage]
    func send(conversationID: UUID, body: String, accessToken: String) async throws -> ConversationMessage
    func markRead(conversationID: UUID, accessToken: String) async throws
}

struct MobileContractUnavailableMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileMessagingContractError.unavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileMessagingContractError.unavailable
    }

    func send(conversationID: UUID, body: String, accessToken: String) async throws -> ConversationMessage {
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

    func send(conversationID: UUID, body: String, accessToken: String) async throws -> ConversationMessage {
        try await client.post(
            "/api/v1/mobile/messaging/conversations/\(conversationID.uuidString)/messages",
            body: SendMessageRequest(body: body),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: ConversationMessage.self
        )
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
