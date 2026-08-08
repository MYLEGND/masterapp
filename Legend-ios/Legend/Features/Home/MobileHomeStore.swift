import Foundation

enum MobileDataLoadState<Value: Equatable>: Equatable {
    case idle
    case loading
    case loaded(Value)
    case unavailable(UserFacingFailure)
}

enum MobileStoreLoadResult: Equatable {
    case loaded
    case authenticationFailure(UserFacingFailure)
    case failed(UserFacingFailure)
}

func mobileLoadResult(
    for error: Error,
    failure: UserFacingFailure
) -> MobileStoreLoadResult {
    switch error as? MobileAPIError {
    case .unauthorized, .apiUnauthorized:
        return .authenticationFailure(failure)
    default:
        return .failed(failure)
    }
}

protocol MobileHomeAPI: Sendable {
    func home(accessToken: String) async throws -> MobileHomeResponse
}

protocol MobileFinancialAPI: Sendable {
    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse
}

protocol MobileJourneyCirclesAPI: Sendable {
    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse
    func saveProfile(_ profile: MobileJourneyProfileInput, accessToken: String) async throws
    func requestConnection(_ request: MobileJourneyConnectionRequestBody, accessToken: String) async throws
    func respondToConnection(id: UUID, accept: Bool, accessToken: String) async throws
    func disconnectConnection(id: UUID, accessToken: String) async throws
    func blockProfile(id: UUID, accessToken: String) async throws
    func reportProfile(id: UUID, request: MobileJourneyReportRequestBody, accessToken: String) async throws
}

extension MobileJourneyCirclesAPI {
    func disconnectConnection(id: UUID, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func blockProfile(id: UUID, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func reportProfile(
        id: UUID,
        request: MobileJourneyReportRequestBody,
        accessToken: String
    ) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

protocol MobileAgentWorkspaceAPI: Sendable {
    func clients(accessToken: String) async throws -> [MobileAgentClientSummary]
    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary]
    func clientCreationForm(accessToken: String) async throws -> MobileClientCreationForm
    func createClient(
        _ request: MobileClientCreationRequest,
        accessToken: String
    ) async throws -> MobileClientCreationResult
}

struct MobileUnavailableHomeAPI: MobileHomeAPI {
    func home(accessToken: String) async throws -> MobileHomeResponse {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableFinancialAPI: MobileFinancialAPI {
    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableJourneyCirclesAPI: MobileJourneyCirclesAPI {
    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func saveProfile(_ profile: MobileJourneyProfileInput, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func requestConnection(_ request: MobileJourneyConnectionRequestBody, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func respondToConnection(id: UUID, accept: Bool, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableAgentWorkspaceAPI: MobileAgentWorkspaceAPI {
    func clients(accessToken: String) async throws -> [MobileAgentClientSummary] {
        throw MobileAPIError.forbidden(correlationID: nil)
    }

    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary] {
        throw MobileAPIError.forbidden(correlationID: nil)
    }

    func clientCreationForm(accessToken: String) async throws -> MobileClientCreationForm {
        throw MobileAPIError.forbidden(correlationID: nil)
    }

    func createClient(
        _ request: MobileClientCreationRequest,
        accessToken: String
    ) async throws -> MobileClientCreationResult {
        throw MobileAPIError.forbidden(correlationID: nil)
    }
}

struct URLSessionMobileHomeAPI: MobileHomeAPI, MobileFinancialAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func home(accessToken: String) async throws -> MobileHomeResponse {
        try await client.get(
            "/api/v1/mobile/home",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileHomeResponse.self)
    }

    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse {
        try await client.get(
            "/api/v1/mobile/financial",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileFinancialSnapshotResponse.self)
    }

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }
}

enum MobileFinancialLoadState: Equatable {
    case idle
    case loading
    case available(MobileFinancialSnapshotResponse)
    case incomplete(MobileFinancialSnapshotResponse, detail: String)
    case neverSaved(MobileFinancialSnapshotResponse, detail: String)
    case projectionUnavailable(MobileFinancialSnapshotResponse, detail: String)
    case authenticationRequired(UserFacingFailure)
    case retryableFailure(UserFacingFailure)
}

struct URLSessionMobileJourneyCirclesAPI: MobileJourneyCirclesAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse {
        try await client.get(
            "/api/v1/mobile/journey-circles",
            accessToken: accessToken,
            headers: ["X-Legend-Participant-Type": participantType.rawValue],
            response: MobileJourneyDashboardResponse.self)
    }

    func saveProfile(_ profile: MobileJourneyProfileInput, accessToken: String) async throws {
        try await client.put(
            "/api/v1/mobile/journey-circles/profile",
            body: profile,
            accessToken: accessToken,
            headers: participantHeader)
    }

    func requestConnection(_ request: MobileJourneyConnectionRequestBody, accessToken: String) async throws {
        try await client.post(
            "/api/v1/mobile/journey-circles/connections",
            body: request,
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func respondToConnection(id: UUID, accept: Bool, accessToken: String) async throws {
        try await client.post(
            "/api/v1/mobile/journey-circles/connections/\(id.uuidString)/response",
            body: MobileJourneyConnectionResponseBody(accept: accept),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func disconnectConnection(id: UUID, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/journey-circles/connections/\(id.uuidString)/disconnect",
            accessToken: accessToken,
            headers: participantHeader)
    }

    func blockProfile(id: UUID, accessToken: String) async throws {
        try await client.post(
            "/api/v1/mobile/journey-circles/profiles/\(id.uuidString)/block",
            body: MobileJourneyProfileActionRequest(),
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    func reportProfile(
        id: UUID,
        request: MobileJourneyReportRequestBody,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/journey-circles/profiles/\(id.uuidString)/report",
            body: request,
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader)
    }

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }
}

struct URLSessionMobileAgentWorkspaceAPI: MobileAgentWorkspaceAPI {
    let client: MobileHTTPClient

    private let participantHeader = ["X-Legend-Participant-Type": ParticipantType.agent.rawValue]

    func clients(accessToken: String) async throws -> [MobileAgentClientSummary] {
        try await client.get(
            "/api/v1/mobile/agent/clients",
            accessToken: accessToken,
            headers: participantHeader,
            response: [MobileAgentClientSummary].self)
    }

    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary] {
        try await client.get(
            "/api/v1/mobile/agent/leads",
            accessToken: accessToken,
            headers: participantHeader,
            response: [MobileAgentLeadSummary].self)
    }

    func clientCreationForm(accessToken: String) async throws -> MobileClientCreationForm {
        try await client.get(
            "/api/v1/mobile/agent/clients/create-form",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileClientCreationForm.self)
    }

    func createClient(
        _ request: MobileClientCreationRequest,
        accessToken: String
    ) async throws -> MobileClientCreationResult {
        try await client.post(
            "/api/v1/mobile/agent/clients",
            body: request,
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: MobileClientCreationResult.self)
    }
}

@MainActor
final class MobileHomeStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileHomeResponse> = .idle
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any MobileHomeAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private let persistence: LegendStorePersistence<MobileHomeResponse>
    private var loadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileHomeAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics,
        persistence: LegendStorePersistence<MobileHomeResponse> = .none()
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
        self.persistence = persistence

        // Home paints last-known content on the very first frame instead of a spinner.
        // This is a single small local read, and the network refresh below still runs.
        if let cached = persistence.read() {
            state = .loaded(cached)
        }
    }

    func load() {
        guard loadTask == nil else { return }
        if !hasCachedValue {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    func refresh() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    private var hasCachedValue: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func request(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let loadTask { return await loadTask.value }

        // Content already on screen (including a hydrated cache) is never replaced by
        // a loading state; it refreshes underneath instead.
        let preservingCachedValue = preservingCachedValue || hasCachedValue

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Home unavailable",
                    message: "The home store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeLoad(
                preservingCachedValue: preservingCachedValue)
        }
        loadTask = task
        let result = await task.value
        loadTask = nil
        return result
    }

    private func executeLoad(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let accessToken = try await accessTokenProvider()
            let home = try await api.home(accessToken: accessToken)
            state = .loaded(home)
            persistence.write(home)
            refreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, title: "Home unavailable")
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A native home request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}

/// Owns the dedicated financial projection request. Home intentionally keeps
/// its independent `/home` state so an older aggregate response can never
/// replace a newer financial projection.
@MainActor
final class MobileFinancialStore: ObservableObject {
    @Published private(set) var state: MobileFinancialLoadState = .idle
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any MobileFinancialAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var loadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileFinancialAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    deinit {
        loadTask?.cancel()
    }

    func load() {
        guard loadTask == nil else { return }
        if !hasCachedValue {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    func refresh() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    private var hasCachedValue: Bool {
        switch state {
        case .available, .incomplete, .neverSaved, .projectionUnavailable:
            return true
        case .idle, .loading, .authenticationRequired, .retryableFailure:
            return false
        }
    }

    private func request(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let loadTask { return await loadTask.value }

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Financial intelligence unavailable",
                    message: "The financial store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeLoad(
                preservingCachedValue: preservingCachedValue)
        }
        loadTask = task
        let result = await task.value
        loadTask = nil
        return result
    }

    private func executeLoad(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let accessToken = try await accessTokenProvider()
            let snapshot = try await api.financial(accessToken: accessToken)
            state = classify(snapshot)
            refreshFailure = nil
            return .loaded
        } catch is CancellationError {
            return .failed(UserFacingFailure(
                title: "Financial intelligence unavailable",
                message: "The financial request was cancelled.",
                correlationID: nil))
        } catch {
            let presentation = failure(for: error)
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = isAuthenticationFailure(error)
                    ? .authenticationRequired(presentation)
                    : .retryableFailure(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func classify(
        _ snapshot: MobileFinancialSnapshotResponse
    ) -> MobileFinancialLoadState {
        guard let operatingSystem = snapshot.operatingSystem else {
            return .projectionUnavailable(
                snapshot,
                detail:
                    "The financial service did not return the saved Expense Lens projection."
            )
        }

        let projection = operatingSystem.projection
        let detail = projection.summary
            ?? "Your saved Expense Lens projection is not available yet."

        if snapshot.position == nil,
           projection.reasonCode == "EXPENSE_LENS_STATE_NOT_FOUND" {
            return .neverSaved(snapshot, detail: detail)
        }

        if projection.status.caseInsensitiveCompare("Available") != .orderedSame {
            return .projectionUnavailable(snapshot, detail: detail)
        }

        if snapshot.position == nil {
            return .incomplete(
                snapshot,
                detail:
                    "Your saved cash-flow projection is available, but Financial Health Snapshot has not been saved."
            )
        }

        return .available(snapshot)
    }

    private func isAuthenticationFailure(_ error: Error) -> Bool {
        guard let apiError = error as? MobileAPIError else {
            return false
        }

        switch apiError {
        case .unauthorized, .apiUnauthorized:
            return true
        default:
            return false
        }
    }

    private func failure(for error: Error) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary:
                "A native financial intelligence request could not be completed.",
            correlationID: apiError?.correlationID
        )

        return UserFacingFailure(
            title: isAuthenticationFailure(error)
                ? "Sign-in required"
                : "Financial intelligence unavailable",
            message: error.localizedDescription,
            correlationID: apiError?.correlationID
        )
    }
}

@MainActor
final class MobileJourneyCirclesStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileJourneyDashboardResponse> = .idle
    @Published private(set) var actionFailure: UserFacingFailure?
    @Published private(set) var isPerformingAction = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any MobileJourneyCirclesAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var loadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileJourneyCirclesAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() {
        guard loadTask == nil else { return }
        if !hasCachedValue {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    func refresh() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    private var hasCachedValue: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func request(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let loadTask { return await loadTask.value }
        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }
        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Journey Circles unavailable",
                    message: "The Journey Circles store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeLoad(
                preservingCachedValue: preservingCachedValue)
        }
        loadTask = task
        let result = await task.value
        loadTask = nil
        return result
    }

    private func executeLoad(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let accessToken = try await accessTokenProvider()
            state = .loaded(try await api.dashboard(accessToken: accessToken))
            refreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error)
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    func requestConnection(to profileID: UUID) {
        performAction {
            try await self.api.requestConnection(
                MobileJourneyConnectionRequestBody(
                    targetClientProfileID: profileID,
                    connectionReason: nil,
                    introduction: nil),
                accessToken: try await self.accessTokenProvider())
        }
    }

    /// Sends a connection request and reports whether the server accepted it, so a
    /// caller such as Discover can update one row instead of reloading a dashboard.
    func requestConnectionConfirmed(to profileID: UUID) async -> Bool {
        await performConfirmedAction(title: "Could not send the request") {
            try await api.requestConnection(
                MobileJourneyConnectionRequestBody(
                    targetClientProfileID: profileID,
                    connectionReason: nil,
                    introduction: nil),
                accessToken: try await accessTokenProvider())
        }
    }

    func disconnectConnectionConfirmed(id: UUID) async -> Bool {
        await performConfirmedAction(title: "Could not remove the connection") {
            try await self.api.disconnectConnection(
                id: id,
                accessToken: try await self.accessTokenProvider())
        }
    }

    func blockProfileConfirmed(id: UUID) async -> Bool {
        await performConfirmedAction(title: "Could not block this profile") {
            try await self.api.blockProfile(
                id: id,
                accessToken: try await self.accessTokenProvider())
        }
    }

    func reportProfileConfirmed(
        id: UUID,
        category: String,
        detail: String? = nil
    ) async -> Bool {
        await performConfirmedAction(title: "Could not submit this report") {
            try await self.api.reportProfile(
                id: id,
                request: MobileJourneyReportRequestBody(
                    category: category,
                    detail: detail),
                accessToken: try await self.accessTokenProvider())
        }
    }

    func saveProfile(_ profile: MobileJourneyProfileInput) {
        performAction {
            try await self.api.saveProfile(
                profile,
                accessToken: try await self.accessTokenProvider())
        }
    }

    func respondToConnection(id: UUID, accept: Bool) {
        performAction {
            try await self.api.respondToConnection(
                id: id,
                accept: accept,
                accessToken: try await self.accessTokenProvider())
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    private func performAction(_ operation: @escaping () async throws -> Void) {
        guard !isPerformingAction else { return }
        isPerformingAction = true
        actionFailure = nil
        Task {
            defer { isPerformingAction = false }
            do {
                try await operation()
                _ = await refresh()
            } catch {
                actionFailure = failure(for: error, title: "Journey Circles unavailable")
            }
        }
    }

    private func performConfirmedAction(
        title: String,
        operation: () async throws -> Void
    ) async -> Bool {
        actionFailure = nil
        do {
            try await operation()
            _ = await refresh()
            return true
        } catch {
            actionFailure = failure(for: error, title: title)
            return false
        }
    }

    private func failure(for error: Error, title: String = "Journey Circles unavailable") -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A native Journey Circles request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}

@MainActor
final class MobileAgentWorkspaceStore: ObservableObject {
    @Published private(set) var clientsState: MobileDataLoadState<[MobileAgentClientSummary]> = .idle
    @Published private(set) var leadsState: MobileDataLoadState<[MobileAgentLeadSummary]> = .idle
    @Published private(set) var isRefreshingClients = false
    @Published private(set) var isRefreshingLeads = false
    @Published private(set) var clientsRefreshFailure: UserFacingFailure?
    @Published private(set) var leadsRefreshFailure: UserFacingFailure?

    private let api: any MobileAgentWorkspaceAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var clientsLoadTask: Task<MobileStoreLoadResult, Never>?
    private var leadsLoadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileAgentWorkspaceAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func loadClients() {
        guard clientsLoadTask == nil else { return }
        if !hasClients {
            clientsState = .loading
        }
        Task { _ = await loadClientsIfNeeded() }
    }

    func loadLeads() {
        guard leadsLoadTask == nil else { return }
        if !hasLeads {
            leadsState = .loading
        }
        Task { _ = await loadLeadsIfNeeded() }
    }

    func loadClientsIfNeeded() async -> MobileStoreLoadResult {
        hasClients ? .loaded : await requestClients(preservingCachedValue: false)
    }

    func loadLeadsIfNeeded() async -> MobileStoreLoadResult {
        hasLeads ? .loaded : await requestLeads(preservingCachedValue: false)
    }

    func refreshClients() async -> MobileStoreLoadResult {
        await requestClients(preservingCachedValue: hasClients)
    }

    func refreshLeads() async -> MobileStoreLoadResult {
        await requestLeads(preservingCachedValue: hasLeads)
    }

    func clientCreationForm() async throws -> MobileClientCreationForm {
        let accessToken = try await accessTokenProvider()
        return try await api.clientCreationForm(accessToken: accessToken)
    }

    func createClient(
        fields: [String: String]
    ) async throws -> MobileClientCreationResult {
        let accessToken = try await accessTokenProvider()
        return try await api.createClient(
            MobileClientCreationRequest(fields: fields),
            accessToken: accessToken)
    }

    private var hasClients: Bool {
        if case .loaded = clientsState { return true }
        return false
    }

    private var hasLeads: Bool {
        if case .loaded = leadsState { return true }
        return false
    }

    private func requestClients(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let clientsLoadTask { return await clientsLoadTask.value }
        if preservingCachedValue {
            isRefreshingClients = true
            clientsRefreshFailure = nil
        } else {
            clientsState = .loading
        }
        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Client CRM unavailable",
                    message: "The client CRM store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeClientsLoad(
                preservingCachedValue: preservingCachedValue)
        }
        clientsLoadTask = task
        let result = await task.value
        clientsLoadTask = nil
        return result
    }

    private func requestLeads(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let leadsLoadTask { return await leadsLoadTask.value }
        if preservingCachedValue {
            isRefreshingLeads = true
            leadsRefreshFailure = nil
        } else {
            leadsState = .loading
        }
        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Lead CRM unavailable",
                    message: "The lead CRM store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeLeadsLoad(
                preservingCachedValue: preservingCachedValue)
        }
        leadsLoadTask = task
        let result = await task.value
        leadsLoadTask = nil
        return result
    }

    private func executeClientsLoad(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshingClients = false }
        do {
            let accessToken = try await accessTokenProvider()
            clientsState = .loaded(try await api.clients(accessToken: accessToken))
            clientsRefreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, resource: "client CRM")
            if preservingCachedValue {
                clientsRefreshFailure = presentation
            } else {
                clientsState = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func executeLeadsLoad(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer { isRefreshingLeads = false }
        do {
            let accessToken = try await accessTokenProvider()
            leadsState = .loaded(try await api.leads(accessToken: accessToken))
            leadsRefreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, resource: "lead CRM")
            if preservingCachedValue {
                leadsRefreshFailure = presentation
            } else {
                leadsState = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func failure(for error: Error, resource: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A native agent \(resource) request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: "\(resource.capitalized) unavailable",
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}
