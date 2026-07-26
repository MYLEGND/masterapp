import Foundation

enum MobileDataLoadState<Value: Equatable>: Equatable {
    case idle
    case loading
    case loaded(Value)
    case unavailable(UserFacingFailure)
}

protocol MobileHomeAPI: Sendable {
    func home(accessToken: String) async throws -> MobileHomeResponse
    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse
}

protocol MobileJourneyCirclesAPI: Sendable {
    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse
}

protocol MobileAgentWorkspaceAPI: Sendable {
    func clients(accessToken: String) async throws -> [MobileAgentClientSummary]
    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary]
}

struct MobileUnavailableHomeAPI: MobileHomeAPI {
    func home(accessToken: String) async throws -> MobileHomeResponse {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableJourneyCirclesAPI: MobileJourneyCirclesAPI {
    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse {
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

struct URLSessionMobileHomeAPI: MobileHomeAPI {
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
                state = .loaded(try await api.home(accessToken: accessToken))
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

@MainActor
final class MobileJourneyCirclesStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileJourneyDashboardResponse> = .idle

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

    private func failure(for error: Error) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A native Journey Circles request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: "Journey Circles unavailable",
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
