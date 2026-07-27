import Foundation

enum MobileDataLoadState<Value: Equatable>: Equatable {
    case idle
    case loading
    case loaded(Value)
    case unavailable(UserFacingFailure)
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
    func disconnect(id: UUID, accessToken: String) async throws
}

protocol MobileAgentWorkspaceAPI: Sendable {
    func clients(accessToken: String) async throws -> [MobileAgentClientSummary]
    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary]
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

    func disconnect(id: UUID, accessToken: String) async throws {
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

    func disconnect(id: UUID, accessToken: String) async throws {
        try await client.post(
            "/api/v1/mobile/journey-circles/connections/\(id.uuidString)/disconnect",
            body: MobileEmptyRequest(),
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
}

@MainActor
final class MobileHomeStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileHomeResponse> = .idle

    private let api: any MobileHomeAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    init(
        api: any MobileHomeAPI,
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
                let accessToken = try await accessTokenProvider()
                let home = try await api.home(accessToken: accessToken)
                state = .loaded(home)
                NativeUnreadBadge.update(with: home.messaging.unreadCount)
            } catch {
                state = .unavailable(failure(for: error, title: "Home unavailable"))
            }
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

    private let api: any MobileFinancialAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var loadTask: Task<Void, Never>?
    private var requestSequence = 0

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
        requestSequence += 1
        let sequence = requestSequence

        loadTask?.cancel()
        state = .loading

        loadTask = Task { [weak self] in
            guard let self else { return }

            do {
                let accessToken = try await self.accessTokenProvider()
                guard !Task.isCancelled else { return }

                let snapshot = try await self.api.financial(
                    accessToken: accessToken
                )
                guard !Task.isCancelled,
                      self.requestSequence == sequence else {
                    return
                }

                self.state = self.classify(snapshot)
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled,
                      self.requestSequence == sequence else {
                    return
                }

                let failure = self.failure(for: error)
                self.state = self.isAuthenticationFailure(error)
                    ? .authenticationRequired(failure)
                    : .retryableFailure(failure)
            }
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

    private let api: any MobileJourneyCirclesAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

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
        state = .loading
        Task {
            do {
                let accessToken = try await accessTokenProvider()
                state = .loaded(try await api.dashboard(accessToken: accessToken))
            } catch {
                state = .unavailable(failure(for: error))
            }
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

    func disconnect(id: UUID) {
        performAction {
            try await self.api.disconnect(
                id: id,
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
                load()
            } catch {
                actionFailure = failure(for: error, title: "Journey Circles unavailable")
            }
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

    private let api: any MobileAgentWorkspaceAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

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
        clientsState = .loading
        Task {
            do {
                let accessToken = try await accessTokenProvider()
                clientsState = .loaded(try await api.clients(accessToken: accessToken))
            } catch {
                clientsState = .unavailable(failure(for: error, resource: "client CRM"))
            }
        }
    }

    func loadLeads() {
        leadsState = .loading
        Task {
            do {
                let accessToken = try await accessTokenProvider()
                leadsState = .loaded(try await api.leads(accessToken: accessToken))
            } catch {
                leadsState = .unavailable(failure(for: error, resource: "lead CRM"))
            }
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
