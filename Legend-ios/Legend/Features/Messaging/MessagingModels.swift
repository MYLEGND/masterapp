import Foundation

struct MessagingParticipant: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let avatar: ProfileAvatar?

    var id: LogicalParticipantIdentity { identity }
    var avatarURL: URL? { avatar?.url }
}

struct ConversationSummary: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let counterparty: MessagingParticipant
    let preview: String?
    let lastActivityUTC: Date
    let unreadCount: Int
    let isMuted: Bool
    let isClosed: Bool
}

struct ConversationDetail: Codable, Equatable, Sendable {
    let id: UUID
    let participants: [MessagingParticipant]
    let messages: [ConversationMessage]
    let isMuted: Bool
    let isClosed: Bool
}

struct ConversationMessage: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let sender: MessagingParticipant
    let body: String
    let sentAtUTC: Date
}

enum RecipientScope: String, Codable, CaseIterable, Sendable {
    case agents
    case clients
    case archived
}

struct MessagingRecipient: Codable, Equatable, Identifiable, Sendable {
    let contactKey: String
    let participant: MessagingParticipant

    var id: String { contactKey }
}

enum MessagingLoadState: Equatable {
    case idle
    case loading
    case loaded([ConversationSummary])
    case unavailable(String)
    case failed(UserFacingFailure)
}

protocol MessagingAPI: Sendable {
    func conversations(accessToken: String) async throws -> [ConversationSummary]
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail
    func recipients(scope: RecipientScope, accessToken: String) async throws -> [MessagingRecipient]
    func markRead(conversationID: UUID, accessToken: String) async throws
}

struct MobileContractUnavailableMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileMessagingContractError.unavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func recipients(scope: RecipientScope, accessToken: String) async throws -> [MessagingRecipient] {
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

    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        try await client.get(
            "/api/mobile/v1/messaging/conversations",
            accessToken: accessToken,
            response: [ConversationSummary].self
        )
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        try await client.get(
            "/api/mobile/v1/messaging/conversations/\(id.uuidString)",
            accessToken: accessToken,
            response: ConversationDetail.self
        )
    }

    func recipients(scope: RecipientScope, accessToken: String) async throws -> [MessagingRecipient] {
        try await client.get(
            "/api/mobile/v1/messaging/recipients",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "scope", value: scope.rawValue)],
            response: [MessagingRecipient].self
        )
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        try await client.post(
            "/api/mobile/v1/messaging/conversations/\(conversationID.uuidString)/read",
            body: EmptyMobileRequest(),
            accessToken: accessToken
        )
    }
}

private struct EmptyMobileRequest: Encodable {}
