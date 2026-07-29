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
    @Published private(set) var isUploadingAttachment = false
    @Published private(set) var sendFailure: UserFacingFailure?
    @Published private(set) var recipientState: MobileDataLoadState<[MessagingRecipient]> = .idle
    @Published private(set) var isStartingConversation = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any MessagingAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var conversationListTask: Task<MobileStoreLoadResult, Never>?

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
        guard conversationListTask == nil else { return }
        if !hasCachedConversations {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        hasCachedConversations ? .loaded : await requestConversationList(preservingCachedValue: false)
    }

    func refresh() async -> MobileStoreLoadResult {
        await requestConversationList(preservingCachedValue: hasCachedConversations)
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
                _ = await refresh()
                completion(conversation.id)
            } catch {
                sendFailure = failure(for: error, title: "Conversation not started")
            }
        }
    }

    func send(body: String) async -> ConversationMessage? {
        let normalizedBody = body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalizedBody.isEmpty,
              let conversationID = selectedConversationID,
              !isSending else {
            return nil
        }

        isSending = true
        sendFailure = nil
        defer { isSending = false }
        do {
            let message = try await api.send(
                conversationID: conversationID,
                body: normalizedBody,
                accessToken: try await accessTokenProvider())
            append(message: message, to: conversationID)
            return message
        } catch {
            sendFailure = failure(for: error, title: "Message not sent")
            diagnostics.record(
                category: .messaging,
                summary: "A native message could not be sent.",
                correlationID: (error as? MobileAPIError)?.correlationID)
            return nil
        }
    }

    func upload(
        attachment: MessagingAttachmentDraft,
        to message: ConversationMessage
    ) async -> MessagingAttachment? {
        guard !isUploadingAttachment else { return nil }
        isUploadingAttachment = true
        sendFailure = nil
        defer { isUploadingAttachment = false }

        do {
            let uploaded = try await api.upload(
                conversationID: message.conversationID,
                messageID: message.id,
                attachment: attachment,
                accessToken: try await accessTokenProvider())
            append(attachment: uploaded, to: message.id)
            return uploaded
        } catch {
            sendFailure = failure(for: error, title: "Attachment not sent")
            return nil
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

    private func append(attachment: MessagingAttachment, to messageID: UUID) {
        guard case .loaded(let conversation) = detailState else { return }
        detailState = .loaded(ConversationDetail(
            id: conversation.id,
            title: conversation.title,
            participants: conversation.participants,
            messages: conversation.messages.map { message in
                guard message.id == messageID else { return message }
                return ConversationMessage(
                    id: message.id,
                    conversationID: message.conversationID,
                    sender: message.sender,
                    body: message.body,
                    sentUTC: message.sentUTC,
                    attachments: message.attachments + [attachment],
                    isMine: message.isMine)
            },
            isMuted: conversation.isMuted,
            isClosed: conversation.isClosed))
    }

    private var hasCachedConversations: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func requestConversationList(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let conversationListTask {
            return await conversationListTask.value
        }

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Messages unavailable",
                    message: "The messaging store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeConversationListRequest(
                preservingCachedValue: preservingCachedValue)
        }
        conversationListTask = task
        let result = await task.value
        conversationListTask = nil
        return result
    }

    private func executeConversationListRequest(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let accessToken = try await accessTokenProvider()
            let conversations = try await api.conversations(accessToken: accessToken)
            state = .loaded(conversations)
            refreshFailure = nil
            NativeUnreadBadge.update(with: conversations.reduce(0) { $0 + max(0, $1.unreadCount) })
            return .loaded
        } catch {
            let presentation = failure(for: error, title: "Messages unavailable")
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = listFailureState(for: error, presentation: presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
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

    private func listFailureState(
        for error: Error,
        presentation: UserFacingFailure
    ) -> MessagingLoadState {
        if case MobileMessagingContractError.unavailable = error {
            return .unavailable("Secure mobile messaging is unavailable until the approved bearer API contract is configured.")
        }
        switch error as? MobileAPIError {
        case .unauthorized, .apiUnauthorized:
            return .unauthorized(presentation)
        case .forbidden, .conflict, .apiForbidden, .apiConflict:
            return .forbidden(presentation)
        case .invalidServerResponse, .networkUnavailable:
            return .offline(presentation)
        default:
            return .failed(presentation)
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
