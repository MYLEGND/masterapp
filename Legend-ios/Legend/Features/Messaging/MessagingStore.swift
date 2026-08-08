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

/// The requester-visible result for a founder-controlled resource request.
/// This is intentionally independent of conversation creation: requests go to
/// the one private staff queue, while the requester remains in Profile settings.
enum MessagingControlledResourceRequestSubmission {
    case sent
    case failed(UserFacingFailure)
}

/// A deliberately small client boundary over the server's existing SignalR
/// contract. The event contains only the identifiers needed to reconcile the
/// server-owned inbox and an already-open conversation.
struct MobileMessagingRealtimeEvent: Decodable, Sendable {
    let conversationID: UUID?
    let messageID: UUID?
    let notificationID: UUID?
    let unreadCount: Int?
    let revision: Int64?
    let occurredUTC: Date

    private enum CodingKeys: String, CodingKey {
        case conversationID = "conversationId"
        case messageID = "messageId"
        case notificationID = "notificationId"
        case unreadCount, revision
        case occurredUTC = "occurredUtc"
    }

    init(
        conversationID: UUID?,
        messageID: UUID?,
        notificationID: UUID? = nil,
        unreadCount: Int? = nil,
        revision: Int64? = nil,
        occurredUTC: Date
    ) {
        self.conversationID = conversationID
        self.messageID = messageID
        self.notificationID = notificationID
        self.unreadCount = unreadCount
        self.revision = revision
        self.occurredUTC = occurredUTC
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            conversationID: try container.decodeIfPresent(UUID.self, forKey: .conversationID),
            messageID: try container.decodeIfPresent(UUID.self, forKey: .messageID),
            notificationID: try container.decodeIfPresent(UUID.self, forKey: .notificationID),
            unreadCount: try container.decodeIfPresent(Int.self, forKey: .unreadCount),
            revision: try container.decodeIfPresent(Int64.self, forKey: .revision),
            occurredUTC: try container.decode(Date.self, forKey: .occurredUTC))
    }
}

@MainActor
protocol MessagingRealtimeTransport: AnyObject {
    var onEvent: ((MobileMessagingRealtimeEvent) -> Void)? { get set }
    func start()
    func stop()
}

/// Native WebSocket transport for the existing ASP.NET Core SignalR JSON
/// protocol. It intentionally carries no message body or inbox copy: each event
/// triggers a bounded REST reconciliation with the same server projection used
/// for normal loading and recovery after reconnects.
@MainActor
final class MobileMessagingRealtimeClient: MessagingRealtimeTransport {
    var onEvent: ((MobileMessagingRealtimeEvent) -> Void)?

    private static let recordSeparator = "\u{001E}"
    private static let reconnectDelays: [Duration] = [
        .seconds(1), .seconds(2), .seconds(5), .seconds(10), .seconds(30)
    ]

    private let hubURL: URL
    private let accessTokenProvider: () async throws -> String
    private let participantType: ParticipantType
    private var socket: URLSessionWebSocketTask?
    private var connectionTask: Task<Void, Never>?
    private var reconnectTask: Task<Void, Never>?
    private var generation = 0
    private var reconnectAttempt = 0
    private var shouldRemainConnected = false

    init?(
        apiBaseURL: URL,
        participantType: ParticipantType,
        accessTokenProvider: @escaping () async throws -> String
    ) {
        guard let hubURL = Self.makeHubURL(from: apiBaseURL) else { return nil }
        self.hubURL = hubURL
        self.participantType = participantType
        self.accessTokenProvider = accessTokenProvider
    }

    deinit {
        socket?.cancel(with: .goingAway, reason: nil)
        connectionTask?.cancel()
        reconnectTask?.cancel()
    }

    func start() {
        guard !shouldRemainConnected else { return }
        shouldRemainConnected = true
        reconnectAttempt = 0
        startConnection()
    }

    func stop() {
        shouldRemainConnected = false
        generation += 1
        reconnectTask?.cancel()
        reconnectTask = nil
        connectionTask?.cancel()
        connectionTask = nil
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
    }

    private func startConnection() {
        guard shouldRemainConnected,
              connectionTask == nil,
              socket == nil else { return }

        let connectionGeneration = generation
        connectionTask = Task { [weak self] in
            await self?.openConnection(generation: connectionGeneration)
        }
    }

    private func openConnection(generation connectionGeneration: Int) async {
        defer {
            if connectionGeneration == generation {
                connectionTask = nil
            }
        }

        do {
            let token = try await accessTokenProvider()
            guard shouldRemainConnected,
                  connectionGeneration == generation else { return }

            var request = URLRequest(url: hubURL)
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(
                participantType.rawValue,
                forHTTPHeaderField: "X-Legend-Participant-Type")
            let newSocket = URLSession.shared.webSocketTask(with: request)
            socket = newSocket
            newSocket.resume()

            try await newSocket.send(.string(
                "{\"protocol\":\"json\",\"version\":1}\(Self.recordSeparator)"))
            let handshake = try await newSocket.receive()
            guard isSuccessfulHandshake(handshake) else {
                throw MobileMessagingRealtimeError.invalidHandshake
            }

            reconnectAttempt = 0
            try await receiveEvents(from: newSocket, generation: connectionGeneration)
        } catch is CancellationError {
            // Explicit stop and role teardown are expected lifecycle events.
        } catch {
            scheduleReconnect(after: connectionGeneration)
        }

        guard connectionGeneration == generation else { return }
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        if shouldRemainConnected {
            scheduleReconnect(after: connectionGeneration)
        }
    }

    private func isSuccessfulHandshake(
        _ message: URLSessionWebSocketTask.Message
    ) -> Bool {
        let text: String
        switch message {
        case .string(let value): text = value
        case .data(let value): text = String(decoding: value, as: UTF8.self)
        @unknown default: return false
        }

        return text
            .split(separator: Character(Self.recordSeparator))
            .contains { frame in
                guard let object = try? JSONSerialization.jsonObject(
                    with: Data(frame.utf8)) as? [String: Any] else {
                    return false
                }
                return object["error"] == nil
            }
    }

    private func receiveEvents(
        from socket: URLSessionWebSocketTask,
        generation connectionGeneration: Int
    ) async throws {
        while shouldRemainConnected,
              connectionGeneration == generation,
              !Task.isCancelled {
            let message = try await socket.receive()
            for event in events(in: message) {
                onEvent?(event)
            }
        }
    }

    private func events(
        in message: URLSessionWebSocketTask.Message
    ) -> [MobileMessagingRealtimeEvent] {
        let text: String
        switch message {
        case .string(let value): text = value
        case .data(let value): text = String(decoding: value, as: UTF8.self)
        @unknown default: return []
        }

        return text
            .split(separator: Character(Self.recordSeparator))
            .compactMap { frame in
                guard let envelope = try? JSONDecoder.mobile.decode(
                    SignalRInvocation.self,
                    from: Data(frame.utf8)),
                    envelope.type == 1,
                    ["messagereceived", "conversationupdated", "notificationupdated"].contains(
                        envelope.target?.lowercased() ?? "") else {
                    return nil
                }
                return envelope.arguments?.first
            }
    }

    private func scheduleReconnect(after connectionGeneration: Int) {
        guard shouldRemainConnected,
              connectionGeneration == generation,
              reconnectTask == nil else { return }

        let delay = Self.reconnectDelays[
            min(reconnectAttempt, Self.reconnectDelays.count - 1)
        ]
        reconnectAttempt += 1
        reconnectTask = Task { [weak self] in
            guard let self else { return }
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled,
                  self.shouldRemainConnected,
                  self.generation == connectionGeneration else {
                return
            }
            self.reconnectTask = nil
            self.startConnection()
        }
    }

    private static func makeHubURL(from apiBaseURL: URL) -> URL? {
        guard var components = URLComponents(
            url: apiBaseURL,
            resolvingAgainstBaseURL: false) else {
            return nil
        }
        switch components.scheme?.lowercased() {
        case "https": components.scheme = "wss"
        case "http": components.scheme = "ws"
        default: return nil
        }
        let basePath = components.path.trimmingCharacters(
            in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, "messaginghub"]
            .filter { !$0.isEmpty }
            .joined(separator: "/")
        components.query = nil
        components.fragment = nil
        return components.url
    }

    private struct SignalRInvocation: Decodable {
        let type: Int?
        let target: String?
        let arguments: [MobileMessagingRealtimeEvent]?
    }
}

private enum MobileMessagingRealtimeError: Error {
    case invalidHandshake
}

@MainActor
final class MessagingStore: ObservableObject {
    @Published private(set) var state: MessagingLoadState = .idle
    @Published private(set) var detailState: ConversationDetailLoadState = .idle
    @Published private(set) var selectedConversationID: UUID?
    @Published private(set) var isSending = false
    @Published private(set) var isUploadingAttachment = false
    @Published private(set) var isLoadingOlderMessages = false
    @Published private(set) var sendFailure: UserFacingFailure?
    @Published private(set) var recipientState: MobileDataLoadState<[MessagingRecipient]> = .idle
    @Published private(set) var selectedRecipientScope: MessagingRecipientScope
    @Published private(set) var isStartingConversation = false
    @Published private(set) var isCreatingGroup = false
    @Published private(set) var isSubmittingControlledResourceRequest = false
    @Published private(set) var activityNotifications: [MobileActivityNotification] = []
    @Published private(set) var isRefreshing = false
    @Published private(set) var isLoadingMoreConversations = false
    @Published private(set) var hasMoreConversations = true
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any MessagingAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private let actorParticipantType: ParticipantType
    private let realtime: (any MessagingRealtimeTransport)?
    let isFounder: Bool
    private var conversationListTask: Task<MobileStoreLoadResult, Never>?
    private var isRefreshingActivityNotifications = false
    private var conversationDetailTasks: [UUID: Task<ConversationDetail, Error>] = [:]
    /// Conversation details are a bounded, account-scoped presentation cache.
    /// The API remains authoritative and every cached thread is revalidated as
    /// soon as it is selected or receives a realtime event.
    private var conversationDetailCache: [UUID: ConversationDetail] = [:]
    private var conversationDetailCacheOrder: [UUID] = []
    private var recipientSearchTask: Task<Void, Never>?
    private var recipientRequestGeneration = 0
    /// Recipient search is an in-memory, account-scoped presentation cache. It
    /// never grants access or starts a conversation without the API confirming it.
    private var recipientSearchCache: [RecipientSearchCacheKey: [MessagingRecipient]] = [:]
    private var recipientSearchCacheOrder: [RecipientSearchCacheKey] = []
    private var notificationBadgeUpdateHandler: ((Int, Int64) -> Void)?

    private static let recipientSearchDebounceNanoseconds: UInt64 = 90_000_000
    private static let maximumCachedRecipientSearches = 20
    private static let maximumCachedConversationDetails = 12
    private static let inboxPageSize = 24

    init(
        api: any MessagingAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics,
        actorParticipantType: ParticipantType,
        isFounder: Bool = false,
        realtime: (any MessagingRealtimeTransport)? = nil
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
        self.actorParticipantType = actorParticipantType
        self.isFounder = isFounder
        self.realtime = realtime
        selectedRecipientScope = .clients
        realtime?.onEvent = { [weak self] event in
            self?.reconcileRealtimeEvent(event)
        }
    }

    deinit {
        // `deinit` is nonisolated in Swift 6, so hand the main-actor transport
        // its teardown without retaining this store. This breaks any outstanding
        // receive loop as the account shell is released.
        let realtime = realtime
        Task { @MainActor [realtime] in
            realtime?.stop()
        }
    }

    var availableRecipientScopes: [MessagingRecipientScope] {
        actorParticipantType == .agent
            ? [.clients, .agents, .leads]
            : [.clients, .agents]
    }

    func load() {
        guard conversationListTask == nil else { return }
        realtime?.start()
        Task {
            // Entering Messages must always reconcile with the server-owned inbox.
            // A cached empty/non-empty list is only a presentation cache; it must
            // never prevent a newly persisted conversation from becoming visible.
            _ = await refresh()
        }
    }

    /// The centralized notification store owns the app icon. Messaging only
    /// forwards the server's signed-in realtime snapshot; it never sums inbox
    /// rows into a competing badge total.
    func setNotificationBadgeUpdateHandler(
        _ handler: @escaping (Int, Int64) -> Void
    ) {
        notificationBadgeUpdateHandler = handler
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        realtime?.start()
        let result = hasCachedConversations
            ? MobileStoreLoadResult.loaded
            : await requestConversationList(preservingCachedValue: false)
        await refreshActivityNotifications()
        return result
    }

    func refresh() async -> MobileStoreLoadResult {
        realtime?.start()
        let result = await requestConversationList(preservingCachedValue: hasCachedConversations)
        await refreshActivityNotifications()
        return result
    }

    func openConversation(_ conversationID: UUID) {
        selectedConversationID = conversationID
        sendFailure = nil

        if let cached = cachedConversationDetail(for: conversationID) {
            // Navigation never waits for a revalidation request. The last
            // server-authorized projection is rendered synchronously, then
            // refreshed in place if anything has changed.
            detailState = .loaded(cached)
        } else {
            detailState = .loading
        }

        // A direct tap is foreground work. It must begin ahead of inbox
        // pagination, activity refreshes, and any realtime reconciliation.
        Task(priority: .userInitiated) {
            await refreshConversation(
                conversationID,
                presentsResult: true,
                marksRead: true)
        }
    }

    func loadMoreConversations() {
        guard !isLoadingMoreConversations,
              hasMoreConversations,
              case .loaded(let loadedConversations) = state else {
            return
        }

        isLoadingMoreConversations = true
        refreshFailure = nil
        Task(priority: .utility) { [weak self] in
            guard let self else { return }
            defer { self.isLoadingMoreConversations = false }

            do {
                let page = try await self.api.conversations(
                    offset: loadedConversations.count,
                    limit: Self.inboxPageSize,
                    accessToken: try await self.accessTokenProvider())
                guard case .loaded(let currentConversations) = self.state else {
                    return
                }

                self.state = .loaded(self.orderedInbox(
                    currentConversations + page.map(self.preservingCachedGroupAvatar)))
                self.hasMoreConversations = page.count == Self.inboxPageSize
            } catch {
                self.refreshFailure = self.failure(
                    for: error,
                    title: "Earlier conversations unavailable")
            }
        }
    }

    func loadRecipients(search: String? = nil) {
        requestRecipients(search: search, debounceNanoseconds: 0)
    }

    func searchRecipients(_ search: String) {
        requestRecipients(
            search: search,
            debounceNanoseconds: Self.recipientSearchDebounceNanoseconds)
    }

    func selectRecipientScope(_ scope: MessagingRecipientScope) {
        guard availableRecipientScopes.contains(scope) else { return }
        selectedRecipientScope = scope
        requestRecipients(search: nil, debounceNanoseconds: 0)
    }

    private func requestRecipients(
        search: String?,
        debounceNanoseconds: UInt64
    ) {
        recipientSearchTask?.cancel()
        recipientRequestGeneration += 1
        let requestGeneration = recipientRequestGeneration
        let selectedScope = selectedRecipientScope
        let cacheKey = RecipientSearchCacheKey(search: search, scope: selectedScope)
        let hadCachedRecipients: Bool
        if let cached = cachedRecipients(for: cacheKey) {
            hadCachedRecipients = true
            recipientState = .loaded(cached)
        } else {
            hadCachedRecipients = false
            recipientState = .loading
        }
        recipientSearchTask = Task { [weak self] in
            guard let self else { return }
            do {
                if debounceNanoseconds > 0 {
                    try await Task.sleep(nanoseconds: debounceNanoseconds)
                }
                try Task.checkCancellation()
                let recipients = try await self.api.recipients(
                    search: search,
                    scope: selectedScope,
                    accessToken: try await self.accessTokenProvider())
                guard !Task.isCancelled,
                      self.recipientRequestGeneration == requestGeneration else {
                    return
                }
                self.recipientState = .loaded(recipients)
                self.cache(recipients, for: cacheKey)
            } catch {
                guard !Task.isCancelled,
                      self.recipientRequestGeneration == requestGeneration else {
                    return
                }
                // Keep a last-known authorized directory visible during a
                // transient revalidation failure. A later search or scope change
                // will still ask the server again; this never expands access.
                if hadCachedRecipients {
                    return
                }
                self.recipientState = .unavailable(
                    self.failure(for: error, title: "Recipients unavailable")
                )
            }
        }
    }

    func startConversation(with recipient: MessagingRecipient, completion: @escaping (UUID) -> Void) {
        beginConversation(resolveRecipient: { recipient }, completion: completion)
    }

    func createGroup(
        subject: String,
        recipients: [MessagingRecipient],
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        completion: @escaping (UUID) -> Void
    ) {
        guard !isCreatingGroup else { return }
        isCreatingGroup = true
        sendFailure = nil
        Task {
            defer { isCreatingGroup = false }
            do {
                let conversation = try await api.createGroup(
                    subject: subject,
                    recipients: recipients,
                    groupImage: groupImage,
                    meeting: meeting,
                    accessToken: try await accessTokenProvider())
                presentConversation(conversation)
                selectedConversationID = conversation.id
                completion(conversation.id)
                Task { [weak self] in
                    _ = await self?.refresh()
                }
            } catch {
                sendFailure = failure(for: error, title: "Group not created")
            }
        }
    }

    func submitControlledResourceRequest(
        _ resourceType: ControlledResourceType
    ) async -> MessagingControlledResourceRequestSubmission {
        guard !isSubmittingControlledResourceRequest else {
            return .failed(UserFacingFailure(
                title: "Request already sending",
                message: "Please wait for the current request to finish.",
                correlationID: nil))
        }

        isSubmittingControlledResourceRequest = true
        sendFailure = nil
        defer { isSubmittingControlledResourceRequest = false }

        do {
            // Both verification and language access use this one API path and
            // the server-owned private review queue; the user is never added to it.
            _ = try await api.startControlledResourceRequest(
                resourceType: resourceType,
                accessToken: try await accessTokenProvider())
            _ = await refresh()
            return .sent
        } catch {
            let requestFailure = failure(
                for: error,
                title: "Request not sent")
            sendFailure = requestFailure
            return .failed(requestFailure)
        }
    }

    func refreshActivityNotifications() async {
        guard !isRefreshingActivityNotifications else {
            return
        }

        isRefreshingActivityNotifications = true
        defer {
            isRefreshingActivityNotifications = false
        }

        do {
            activityNotifications = try await api.activityNotifications(
                accessToken: try await accessTokenProvider())
                .filter { $0.controlledResourceRequestID != nil }
        } catch {
            // Activity is supplementary: a temporary fetch failure must never
            // obscure conversations or overwrite a more relevant user action error.
        }
    }

    func controlledResourceRecipients(
        _ resourceType: ControlledResourceType,
        search: String? = nil
    ) async -> [MessagingRecipient]? {
        do {
            return try await api.controlledResourceRecipients(
                resourceType: resourceType,
                search: search,
                accessToken: try await accessTokenProvider())
        } catch {
            sendFailure = failure(for: error, title: "Access directory unavailable")
            return nil
        }
    }

    func communicationLanguages() async -> [LegendCommunicationLanguage]? {
        do {
            return try await api.communicationLanguages(
                accessToken: try await accessTokenProvider())
        } catch {
            sendFailure = failure(for: error, title: "Languages unavailable")
            return nil
        }
    }

    @discardableResult
    func setControlledResourceGrant(
        _ resourceType: ControlledResourceType,
        recipient: MessagingRecipient,
        isGranted: Bool
    ) async -> Bool {
        guard !isCreatingGroup else { return false }
        isCreatingGroup = true
        sendFailure = nil
        defer { isCreatingGroup = false }
        do {
            try await api.setControlledResourceGrant(
                resourceType: resourceType,
                recipient: recipient,
                isGranted: isGranted,
                accessToken: try await accessTokenProvider())
            _ = await refresh()
            return true
        } catch {
            sendFailure = failure(for: error, title: "Access not updated")
            return false
        }
    }

    func addGroupParticipant(
        _ recipient: MessagingRecipient,
        to conversationID: UUID,
        completion: @escaping () -> Void
    ) {
        guard !isCreatingGroup else { return }
        isCreatingGroup = true
        sendFailure = nil
        Task {
            defer { isCreatingGroup = false }
            do {
                try await api.addGroupParticipant(
                    conversationID: conversationID,
                    recipient: recipient,
                    accessToken: try await accessTokenProvider())
                let conversation = try await api.conversation(
                    id: conversationID,
                    accessToken: try await accessTokenProvider())
                presentConversation(conversation)
                completion()
                Task { [weak self] in
                    _ = await self?.refresh()
                }
            } catch {
                sendFailure = failure(for: error, title: "Member not added")
            }
        }
    }

    func setGroupCollaborator(
        _ participant: MessagingParticipant,
        in conversationID: UUID,
        isManager: Bool
    ) {
        guard !isCreatingGroup else { return }

        isCreatingGroup = true
        sendFailure = nil

        Task {
            defer { isCreatingGroup = false }

            do {
                try await api.setGroupCollaborator(
                    conversationID: conversationID,
                    participant: participant.identity,
                    isManager: isManager,
                    accessToken: try await accessTokenProvider())

                let conversation = try await api.conversation(
                    id: conversationID,
                    accessToken: try await accessTokenProvider())

                presentConversation(conversation)
                _ = await refresh()
            } catch {
                sendFailure = failure(
                    for: error,
                    title: isManager
                        ? "Collaborator not added"
                        : "Collaborator not removed")
            }
        }
    }

    func deleteGroup(
        conversationID: UUID,
        completion: @escaping () -> Void
    ) {
        sendFailure = nil

        Task {
            do {
                try await api.deleteGroup(
                    conversationID: conversationID,
                    accessToken: try await accessTokenProvider())

                if selectedConversationID == conversationID {
                    selectedConversationID = nil
                }

                conversationDetailCache.removeValue(forKey: conversationID)
                conversationDetailCacheOrder.removeAll { $0 == conversationID }
                detailState = .idle
                _ = await refresh()
                completion()
            } catch {
                sendFailure = failure(
                    for: error,
                    title: "Group not deleted")
            }
        }
    }

    func setGroupPromotion(conversationID: UUID, isPromoted: Bool) {
        guard isFounder, !isCreatingGroup else { return }
        isCreatingGroup = true
        sendFailure = nil

        Task {
            defer { isCreatingGroup = false }
            do {
                let conversation = try await api.setGroupPromotion(
                    conversationID: conversationID,
                    isPromoted: isPromoted,
                    accessToken: try await accessTokenProvider())
                presentConversation(conversation)
                _ = await refresh()
            } catch {
                sendFailure = failure(for: error, title: "Group promotion not updated")
            }
        }
    }

    @discardableResult
    func joinPromotedGroup(conversationID: UUID) async -> Bool {
        guard !isCreatingGroup else { return false }
        isCreatingGroup = true
        sendFailure = nil
        defer { isCreatingGroup = false }

        do {
            let conversation = try await api.joinPromotedGroup(
                conversationID: conversationID,
                accessToken: try await accessTokenProvider())
            presentConversation(conversation)
            selectedConversationID = conversation.id
            _ = await refresh()
            return true
        } catch {
            sendFailure = failure(for: error, title: "Group not joined")
            return false
        }
    }

    func updateGroup(
        conversationID: UUID,
        subject: String,
        groupImage: MessagingGroupImageRequest?,
        meeting: MessagingGroupMeetingRequest?,
        completion: @escaping () -> Void
    ) {
        guard !isCreatingGroup else { return }
        isCreatingGroup = true
        sendFailure = nil
        Task {
            defer { isCreatingGroup = false }
            do {
                try await api.updateGroup(
                    conversationID: conversationID,
                    subject: subject,
                    groupImage: groupImage,
                    meeting: meeting,
                    accessToken: try await accessTokenProvider())
                let conversation = try await api.conversation(
                    id: conversationID,
                    accessToken: try await accessTokenProvider())
                presentConversation(conversation)
                completion()
                Task { [weak self] in
                    _ = await self?.refresh()
                }
            } catch {
                sendFailure = failure(for: error, title: "Group not updated")
            }
        }
    }

    func resolveVerificationRequest(
        _ request: VerificationReview,
        approve: Bool,
        note: String? = nil
    ) async -> Bool {
        guard !isCreatingGroup else { return false }
        isCreatingGroup = true
        sendFailure = nil
        defer { isCreatingGroup = false }
        do {
            try await api.resolveControlledResourceRequest(
                requestID: request.id,
                approve: approve,
                note: note,
                accessToken: try await accessTokenProvider())
            if let conversationID = selectedConversationID {
                let conversation = try await api.conversation(
                    id: conversationID,
                    accessToken: try await accessTokenProvider())
                presentConversation(conversation)
            }
            _ = await refresh()
            return true
        } catch {
            sendFailure = failure(for: error, title: "Verification not resolved")
            return false
        }
    }

    /// The agent CRM is allowed to open a conversation only through the same
    /// recipient authority used by the Messages screen. A CRM profile is never
    /// treated as a messaging recipient on the device by itself.
    func startConversation(forClientProfileID clientProfileID: UUID, completion: @escaping (UUID) -> Void) {
        beginConversation(resolveRecipient: {
            let recipients = try await self.api.recipients(
                search: nil,
                scope: .clients,
                accessToken: try await self.accessTokenProvider())
            guard let recipient = recipients.first(where: {
                $0.identity.participantType == .client &&
                    UUID(uuidString: $0.profileID) == clientProfileID
            }) else {
                throw MobileMessagingRecipientError.clientNoLongerAvailable
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
                presentConversation(conversation)
                selectedConversationID = conversation.id
                completion(conversation.id)
                Task { [weak self] in
                    _ = await self?.refresh()
                }
            } catch {
                sendFailure = failure(for: error, title: "Conversation not started")
            }
        }
    }

    func send(
        body: String,
        replyingTo replyTarget: ConversationMessage? = nil
    ) async -> ConversationMessage? {
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
                replyToMessageID: replyTarget?.id,
                accessToken: try await accessTokenProvider())
            append(message: message, to: conversationID)

            // The server conversation/participant/message projection is the one
            // inbox source of truth. This is critical for the first message: the
            // blank conversation is intentionally absent from Previous Messages
            // until the first message is persisted, so reconcile immediately after
            // a successful send instead of maintaining a second local recent-chat list.
            Task { [weak self] in
                guard let self else { return }
                _ = await self.requestConversationList(
                    preservingCachedValue: self.hasCachedConversations)
            }
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

    func setPinned(conversationID: UUID, isPinned: Bool) {
        Task {
            do {
                try await api.setPinned(
                    conversationID: conversationID,
                    isPinned: isPinned,
                    accessToken: try await accessTokenProvider())
                updateConversation(conversationID) { conversation in
                    ConversationSummary(
                        id: conversation.id,
                        conversationType: conversation.conversationType,
                        counterparty: conversation.counterparty,
                        title: conversation.title,
                        lastMessagePreview: conversation.lastMessagePreview,
                        lastMessageUTC: conversation.lastMessageUTC,
                        unreadCount: conversation.unreadCount,
                        isClosed: conversation.isClosed,
                        purpose: conversation.purpose,
                        groupAvatar: conversation.groupAvatar,
                        isPinned: isPinned,
                        isMuted: conversation.isMuted)
                }
            } catch {
                sendFailure = failure(for: error, title: "Conversation not updated")
            }
        }
    }

    func setMuted(conversationID: UUID, isMuted: Bool) {
        Task {
            do {
                try await api.setMuted(
                    conversationID: conversationID,
                    isMuted: isMuted,
                    accessToken: try await accessTokenProvider())
                updateConversation(conversationID) { conversation in
                    ConversationSummary(
                        id: conversation.id,
                        conversationType: conversation.conversationType,
                        counterparty: conversation.counterparty,
                        title: conversation.title,
                        lastMessagePreview: conversation.lastMessagePreview,
                        lastMessageUTC: conversation.lastMessageUTC,
                        unreadCount: conversation.unreadCount,
                        isClosed: conversation.isClosed,
                        purpose: conversation.purpose,
                        groupAvatar: conversation.groupAvatar,
                        isPinned: conversation.isPinned,
                        isMuted: isMuted)
                }
            } catch {
                sendFailure = failure(for: error, title: "Conversation not updated")
            }
        }
    }

    func removeConversation(conversationID: UUID) {
        Task {
            do {
                try await api.removeConversation(
                    conversationID: conversationID,
                    accessToken: try await accessTokenProvider())
                guard case .loaded(let conversations) = state else { return }
                let remaining = conversations.filter { $0.id != conversationID }
                state = .loaded(remaining)
            } catch {
                sendFailure = failure(for: error, title: "Conversation not removed")
            }
        }
    }

    func deleteMessage(_ message: ConversationMessage) {
        guard message.isMine, !message.isDeleted else { return }
        Task {
            do {
                try await api.deleteMessage(
                    conversationID: message.conversationID,
                    messageID: message.id,
                    accessToken: try await accessTokenProvider())
                replaceMessage(message.id) { original in
                    ConversationMessage(
                        id: original.id,
                        conversationID: original.conversationID,
                        sender: original.sender,
                        body: "Message unsent",
                        sentUTC: original.sentUTC,
                        attachments: [],
                        isMine: original.isMine,
                        isDeleted: true,
                        reply: original.reply,
                        verificationReview: original.verificationReview)
                }
            } catch {
                sendFailure = failure(for: error, title: "Message not unsent")
            }
        }
    }

    func callOptions(for conversationID: UUID) async -> ConversationCallOptions? {
        do {
            return try await api.callOptions(
                conversationID: conversationID,
                accessToken: try await accessTokenProvider())
        } catch {
            sendFailure = failure(for: error, title: "Calling unavailable")
            return nil
        }
    }

    func loadOlderMessages() {
        guard !isLoadingOlderMessages,
              case .loaded(let conversation) = detailState,
              conversation.hasOlderMessages == true,
              let oldestMessageUTC = conversation.messages.first?.sentUTC else {
            return
        }

        isLoadingOlderMessages = true
        Task {
            defer { isLoadingOlderMessages = false }
            do {
                let olderPage = try await api.conversation(
                    id: conversation.id,
                    beforeUTC: oldestMessageUTC,
                    accessToken: try await accessTokenProvider())
                guard selectedConversationID == conversation.id,
                      case .loaded(let currentConversation) = detailState,
                      currentConversation.id == conversation.id else {
                    return
                }

                let mergedMessages = (olderPage.messages + currentConversation.messages)
                    .reduce(into: [UUID: ConversationMessage]()) { messages, message in
                        messages[message.id] = message
                    }
                    .values
                    .sorted { $0.sentUTC < $1.sentUTC }
                presentConversation(copyConversation(
                    currentConversation,
                    messages: mergedMessages,
                    hasOlderMessages: olderPage.hasOlderMessages))
            } catch {
                sendFailure = failure(for: error, title: "Earlier messages unavailable")
            }
        }
    }

    private func append(message: ConversationMessage, to conversationID: UUID) {
        guard case .loaded(let conversation) = detailState,
              conversation.id == conversationID else {
            return
        }
        presentConversation(copyConversation(
            conversation,
            messages: conversation.messages + [message]))
    }

    private func append(attachment: MessagingAttachment, to messageID: UUID) {
        guard case .loaded(let conversation) = detailState else { return }
        presentConversation(copyConversation(
            conversation,
            messages: conversation.messages.map { message in
                guard message.id == messageID else { return message }
                return ConversationMessage(
                    id: message.id,
                    conversationID: message.conversationID,
                    sender: message.sender,
                    body: message.body,
                    sentUTC: message.sentUTC,
                    attachments: message.attachments + [attachment],
                    isMine: message.isMine,
                    reply: message.reply,
                    verificationReview: message.verificationReview)
            }))
    }

    private func updateConversation(
        _ conversationID: UUID,
        transform: (ConversationSummary) -> ConversationSummary
    ) {
        guard case .loaded(let conversations) = state else { return }
        state = .loaded(orderedInbox(
            conversations.map { $0.id == conversationID ? transform($0) : $0 }))
    }

    private func replaceMessage(
        _ messageID: UUID,
        transform: (ConversationMessage) -> ConversationMessage
    ) {
        guard case .loaded(let conversation) = detailState else { return }
        presentConversation(copyConversation(
            conversation,
            messages: conversation.messages.map {
                $0.id == messageID ? transform($0) : $0
            }))
    }

    private func copyConversation(
        _ conversation: ConversationDetail,
        messages: [ConversationMessage],
        hasOlderMessages: Bool? = nil
    ) -> ConversationDetail {
        ConversationDetail(
            id: conversation.id,
            conversationType: conversation.conversationType,
            title: conversation.title,
            participants: conversation.participants,
            messages: messages,
            isMuted: conversation.isMuted,
            isClosed: conversation.isClosed,
            canManageMembers: conversation.canManageMembers,
            purpose: conversation.purpose,
            groupAvatar: conversation.groupAvatar,
            canManageCollaborators: conversation.canManageCollaborators,
            canDeleteGroup: conversation.canDeleteGroup,
            isPromoted: conversation.isPromoted,
            promotionStartedUTC: conversation.promotionStartedUTC,
            promotionEndedUTC: conversation.promotionEndedUTC,
            canManagePromotion: conversation.canManagePromotion,
            meeting: conversation.meeting,
            canManageMeeting: conversation.canManageMeeting,
            hasOlderMessages: hasOlderMessages ?? conversation.hasOlderMessages)
    }

    private var hasCachedConversations: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func refreshConversation(
        _ conversationID: UUID,
        presentsResult: Bool,
        marksRead: Bool
    ) async {
        do {
            let conversation = try await conversationDetail(for: conversationID)
            cacheConversationDetail(conversation)

            guard !presentsResult || selectedConversationID == conversationID else {
                return
            }
            if presentsResult {
                presentConversation(conversation)
            }

            guard marksRead else { return }
            // Read acknowledgement is deliberately after presentation. It is
            // important for inbox state, but must never hold up the thread.
            Task { [weak self] in
                guard let self else { return }
                do {
                    try await self.api.markRead(
                        conversationID: conversationID,
                        accessToken: try await self.accessTokenProvider())
                    guard self.selectedConversationID == conversationID else { return }
                    self.updateUnreadCount(for: conversationID)
                } catch {
                    // A later inbox refresh reconciles the read status. The
                    // already-authorized thread remains usable either way.
                }
            }
        } catch {
            guard presentsResult, selectedConversationID == conversationID else {
                return
            }
            if cachedConversationDetail(for: conversationID) == nil {
                detailState = detailFailureState(for: error)
            }
        }
    }

    private func conversationDetail(for conversationID: UUID) async throws -> ConversationDetail {
        if let existingTask = conversationDetailTasks[conversationID] {
            return try await existingTask.value
        }

        let task = Task { [api, accessTokenProvider] in
            try await api.conversation(
                id: conversationID,
                accessToken: try await accessTokenProvider())
        }
        conversationDetailTasks[conversationID] = task
        defer { conversationDetailTasks[conversationID] = nil }
        return try await task.value
    }

    private func cachedConversationDetail(for conversationID: UUID) -> ConversationDetail? {
        guard let cached = conversationDetailCache[conversationID] else { return nil }
        touchConversationDetail(conversationID)
        return cached
    }

    private func presentConversation(_ conversation: ConversationDetail) {
        detailState = .loaded(conversation)
        cacheConversationDetail(conversation)
    }

    private func cacheConversationDetail(_ conversation: ConversationDetail) {
        conversationDetailCache[conversation.id] = conversation
        touchConversationDetail(conversation.id)

        while conversationDetailCacheOrder.count > Self.maximumCachedConversationDetails {
            let evictedConversationID = conversationDetailCacheOrder.removeFirst()
            conversationDetailCache.removeValue(forKey: evictedConversationID)
        }
    }

    private func touchConversationDetail(_ conversationID: UUID) {
        conversationDetailCacheOrder.removeAll { $0 == conversationID }
        conversationDetailCacheOrder.append(conversationID)
    }

    private func preservingCachedGroupAvatar(
        _ conversation: ConversationSummary
    ) -> ConversationSummary {
        guard conversation.conversationType == "Group",
              conversation.groupAvatar == nil,
              let groupAvatar = conversationDetailCache[conversation.id]?.groupAvatar else {
            return conversation
        }

        return ConversationSummary(
            id: conversation.id,
            conversationType: conversation.conversationType,
            counterparty: conversation.counterparty,
            title: conversation.title,
            lastMessagePreview: conversation.lastMessagePreview,
            lastMessageUTC: conversation.lastMessageUTC,
            unreadCount: conversation.unreadCount,
            isClosed: conversation.isClosed,
            purpose: conversation.purpose,
            groupAvatar: groupAvatar,
            isPinned: conversation.isPinned,
            isMuted: conversation.isMuted)
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
            let conversations = try await api.conversations(
                offset: 0,
                limit: Self.inboxPageSize,
                accessToken: accessToken)
            // The server-owned messaging service is the sole authority for
            // inbox visibility. Do not apply a second client-side persistence
            // rule here. The server intentionally hides empty direct drafts,
            // while allowing explicit user-created groups and every persisted
            // conversation that belongs in this actor's inbox.
            state = .loaded(orderedInbox(
                conversations.map(preservingCachedGroupAvatar)))
            hasMoreConversations = conversations.count == Self.inboxPageSize
            refreshFailure = nil
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
        state = .loaded(orderedInbox(conversations.map { conversation in
            guard conversation.id == conversationID else { return conversation }
            return ConversationSummary(
                id: conversation.id,
                conversationType: conversation.conversationType,
                counterparty: conversation.counterparty,
                title: conversation.title,
                lastMessagePreview: conversation.lastMessagePreview,
                lastMessageUTC: conversation.lastMessageUTC,
                unreadCount: 0,
                isClosed: conversation.isClosed,
                purpose: conversation.purpose,
                groupAvatar: conversation.groupAvatar,
                isPinned: conversation.isPinned,
                isMuted: conversation.isMuted)
        }))
    }

    /// This local ordering is intentionally the same as the server ordering.
    /// It protects the UI from an out-of-order response or equal timestamps
    /// without creating a second persistence rule for the inbox.
    private func orderedInbox(
        _ conversations: [ConversationSummary]
    ) -> [ConversationSummary] {
        let uniqueConversations = Dictionary(
            conversations.map { ($0.id, $0) },
            uniquingKeysWith: { latest, _ in latest })

        return uniqueConversations.values.sorted { left, right in
            let leftTimestamp = left.lastMessageUTC ?? .distantPast
            let rightTimestamp = right.lastMessageUTC ?? .distantPast
            if leftTimestamp != rightTimestamp {
                return leftTimestamp > rightTimestamp
            }
            return left.id.uuidString > right.id.uuidString
        }
    }

    private func reconcileRealtimeEvent(_ event: MobileMessagingRealtimeEvent) {
        if let unreadCount = event.unreadCount {
            notificationBadgeUpdateHandler?(unreadCount, event.revision ?? 0)
            return
        }
        guard let conversationID = event.conversationID else { return }
        Task { [weak self] in
            guard let self else { return }

            async let inbox = self.requestConversationList(
                preservingCachedValue: self.hasCachedConversations)
            async let selectedConversation = self.refreshSelectedConversation(
                ifSelected: conversationID)
            _ = await (inbox, selectedConversation)
        }
    }

    private func refreshSelectedConversation(ifSelected conversationID: UUID) async {
        guard selectedConversationID == conversationID else { return }
        await refreshConversation(
            conversationID,
            presentsResult: true,
            marksRead: false)
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

    private func cachedRecipients(
        for key: RecipientSearchCacheKey
    ) -> [MessagingRecipient]? {
        guard let recipients = recipientSearchCache[key] else { return nil }
        touchCachedRecipients(key)
        return recipients
    }

    private func cache(
        _ recipients: [MessagingRecipient],
        for key: RecipientSearchCacheKey
    ) {
        recipientSearchCache[key] = recipients
        touchCachedRecipients(key)
        while recipientSearchCacheOrder.count > Self.maximumCachedRecipientSearches {
            let evicted = recipientSearchCacheOrder.removeFirst()
            recipientSearchCache.removeValue(forKey: evicted)
        }
    }

    private func touchCachedRecipients(_ key: RecipientSearchCacheKey) {
        recipientSearchCacheOrder.removeAll { $0 == key }
        recipientSearchCacheOrder.append(key)
    }

    private struct RecipientSearchCacheKey: Hashable {
        let search: String
        let scope: MessagingRecipientScope

        init(search: String?, scope: MessagingRecipientScope) {
            self.search = search?
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
                ?? ""
            self.scope = scope
        }
    }
}

private enum MobileMessagingRecipientError: LocalizedError {
    case clientNoLongerAvailable

    var errorDescription: String? {
        "This CRM record is no longer an active client available for messaging."
    }
}
