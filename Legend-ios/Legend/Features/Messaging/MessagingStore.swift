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

    private let api: any MessagingAPI
    private let accessToken: String
    private let diagnostics: LegendDiagnostics

    init(api: any MessagingAPI, accessToken: String, diagnostics: LegendDiagnostics) {
        self.api = api
        self.accessToken = accessToken
        self.diagnostics = diagnostics
    }

    func load() {
        state = .loading
        Task {
            do {
                let conversations = try await api.conversations(accessToken: accessToken)
                state = .loaded(conversations)
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
                let conversation = try await api.conversation(id: conversationID, accessToken: accessToken)
                detailState = .loaded(conversation)
                try await api.markRead(conversationID: conversationID, accessToken: accessToken)
                updateUnreadCount(for: conversationID)
            } catch {
                detailState = detailFailureState(for: error)
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
                    accessToken: accessToken)
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
    }

    private func listFailureState(for error: Error) -> MessagingLoadState {
        if case MobileMessagingContractError.unavailable = error {
            return .unavailable("Secure mobile messaging is unavailable until the approved bearer API contract is configured.")
        }
        let failure = failure(for: error, title: "Messages unavailable")
        switch error as? MobileAPIError {
        case .unauthorized:
            return .unauthorized(failure)
        case .forbidden, .conflict:
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
        case .unauthorized:
            return .unauthorized(failure)
        case .forbidden, .conflict:
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
