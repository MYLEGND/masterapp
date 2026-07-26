import Foundation

enum MessagingLoadState: Equatable {
    case idle
    case loading
    case loaded([ConversationSummary])
    case unavailable(String)
    case unauthorized(UserFacingFailure)
    case forbidden(UserFacingFailure)
    case offline(UserFacingFailure)
    case failed(UserFacingFailure)
}

enum ConversationDetailLoadState: Equatable {
    case idle
    case loading
    case loaded(ConversationDetail)
    case unavailable(String)
    case unauthorized(UserFacingFailure)
    case forbidden(UserFacingFailure)
    case offline(UserFacingFailure)
    case failed(UserFacingFailure)
}

@MainActor
final class MessagingStore: ObservableObject {
    @Published private(set) var state: MessagingLoadState = .idle
    @Published private(set) var detailState: ConversationDetailLoadState = .idle
    @Published private(set) var selectedConversationID: UUID?
    @Published private(set) var isSending = false
    @Published private(set) var sendFailure: UserFacingFailure?
    @Published private(set) var recipientState: MobileDataLoadState<[MessagingRecipient]> = .idle
    @Published private(set) var isStartingConversation = false

    private let api: any MessagingAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    init(
        api: any MessagingAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() {
        state = .loading
        Task {
            do {
                let conversations = try await api.conversations(accessToken: try await accessTokenProvider())
                state = .loaded(conversations)
                NativeUnreadBadge.update(with: conversations.reduce(0) { $0 + max(0, $1.unreadCount) })
            } catch {
                state = listFailureState(for: error)
            }
        }
    }

    func openConversation(_ conversationID: UUID) {
        selectedConversationID = conversationID
        detailState = .loading
        sendFailure = nil
        Task {
            do {
                let accessToken = try await accessTokenProvider()
                let conversation = try await api.conversation(id: conversationID, accessToken: accessToken)
                detailState = .loaded(conversation)
                try await api.markRead(conversationID: conversationID, accessToken: accessToken)
                updateUnreadCount(for: conversationID)
            } catch {
                detailState = detailFailureState(for: error)
            }
        }
    }

    func loadRecipients(search: String? = nil) {
        recipientState = .loading
        Task {
            do {
                let recipients = try await api.recipients(
                    search: search,
                    accessToken: try await accessTokenProvider())
                recipientState = .loaded(recipients)
            } catch {
                recipientState = .unavailable(failure(for: error, title: "Recipients unavailable"))
            }
        }
    }

    func startConversation(with recipient: MessagingRecipient, completion: @escaping (UUID) -> Void) {
        beginConversation(resolveRecipient: { recipient }, completion: completion)
    }

    /// The agent CRM is allowed to open a conversation only through the same
    /// recipient authority used by the Messages screen. A CRM profile is never
    /// treated as a messaging recipient on the device by itself.
    func startConversation(forClientProfileID clientProfileID: UUID, completion: @escaping (UUID) -> Void) {
        beginConversation(resolveRecipient: {
            let recipients = try await self.api.recipients(
                search: nil,
                accessToken: try await self.accessTokenProvider())
            guard let recipient = recipients.first(where: {
                $0.identity.participantType == .client &&
                    UUID(uuidString: $0.profileID) == clientProfileID
            }) else {
                throw MobileMessagingRecipientError.clientNoLongerAuthorized
            }
            return recipient
        }, completion: completion)
    }

    private func beginConversation(
        resolveRecipient: @escaping () async throws -> MessagingRecipient,
        completion: @escaping (UUID) -> Void
    ) {
        guard !isStartingConversation else { return }
        isStartingConversation = true
        sendFailure = nil
        Task {
            defer { isStartingConversation = false }
            do {
                let recipient = try await resolveRecipient()
                let conversation = try await api.start(
                    recipient: recipient,
                    accessToken: try await accessTokenProvider())
                detailState = .loaded(conversation)
                selectedConversationID = conversation.id
                let conversations = try await api.conversations(accessToken: try await accessTokenProvider())
                state = .loaded(conversations)
                NativeUnreadBadge.update(with: conversations.reduce(0) { $0 + max(0, $1.unreadCount) })
                completion(conversation.id)
            } catch {
                sendFailure = failure(for: error, title: "Conversation not started")
            }
        }
    }

    func send(body: String) {
        let normalizedBody = body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalizedBody.isEmpty,
              let conversationID = selectedConversationID,
              !isSending else {
            return
        }

        isSending = true
        sendFailure = nil
        Task {
            defer { isSending = false }
            do {
                let message = try await api.send(
                    conversationID: conversationID,
                    body: normalizedBody,
                    accessToken: try await accessTokenProvider())
                append(message: message, to: conversationID)
            } catch {
                sendFailure = failure(for: error, title: "Message not sent")
                diagnostics.record(
                    category: .messaging,
                    summary: "A native message could not be sent.",
                    correlationID: (error as? MobileAPIError)?.correlationID)
            }
        }
    }

    private func append(message: ConversationMessage, to conversationID: UUID) {
        guard case .loaded(let conversation) = detailState,
              conversation.id == conversationID else {
            return
        }
        detailState = .loaded(ConversationDetail(
            id: conversation.id,
            title: conversation.title,
            participants: conversation.participants,
            messages: conversation.messages + [message],
            isMuted: conversation.isMuted,
            isClosed: conversation.isClosed))
    }

    private func updateUnreadCount(for conversationID: UUID) {
        guard case .loaded(let conversations) = state else { return }
        state = .loaded(conversations.map { conversation in
            guard conversation.id == conversationID else { return conversation }
            return ConversationSummary(
                id: conversation.id,
                counterparty: conversation.counterparty,
                title: conversation.title,
                lastMessagePreview: conversation.lastMessagePreview,
                lastMessageUTC: conversation.lastMessageUTC,
                unreadCount: 0,
                isClosed: conversation.isClosed)
        })
        if case .loaded(let updatedConversations) = state {
            NativeUnreadBadge.update(with: updatedConversations.reduce(0) { $0 + max(0, $1.unreadCount) })
        }
    }

    private func listFailureState(for error: Error) -> MessagingLoadState {
        if case MobileMessagingContractError.unavailable = error {
            return .unavailable("Secure mobile messaging is unavailable until the approved bearer API contract is configured.")
        }
        let failure = failure(for: error, title: "Messages unavailable")
        switch error as? MobileAPIError {
        case .unauthorized, .apiUnauthorized:
            return .unauthorized(failure)
        case .forbidden, .conflict, .apiForbidden, .apiConflict:
            return .forbidden(failure)
        case .invalidServerResponse, .networkUnavailable:
            return .offline(failure)
        default:
            return .failed(failure)
        }
    }

    private func detailFailureState(for error: Error) -> ConversationDetailLoadState {
        if case MobileMessagingContractError.unavailable = error {
            return .unavailable("Secure mobile messaging is unavailable until the approved bearer API contract is configured.")
        }
        let failure = failure(for: error, title: "Conversation unavailable")
        switch error as? MobileAPIError {
        case .unauthorized, .apiUnauthorized:
            return .unauthorized(failure)
        case .forbidden, .conflict, .apiForbidden, .apiConflict:
            return .forbidden(failure)
        case .invalidServerResponse, .networkUnavailable:
            return .offline(failure)
        default:
            return .failed(failure)
        }
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .messaging,
            summary: "A native messaging request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}

private enum MobileMessagingRecipientError: LocalizedError {
    case clientNoLongerAuthorized

    var errorDescription: String? {
        "This CRM record is no longer an authorized active client for messaging."
    }
}
