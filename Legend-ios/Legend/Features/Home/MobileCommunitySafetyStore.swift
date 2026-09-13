import Foundation

/// Server-projected community report. The native client deliberately receives
/// no moderation authority in this model; every action is re-authorized by the
/// mobile API against the signed-in identity and its durable grant.
struct MobileCommunitySafetyReport: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let targetKind: String
    let targetEntityID: UUID?
    let category: String
    let detail: String?
    let status: String
    let createdUTC: Date
    let reporterParticipantType: String
    let reportedParticipantType: String

    private enum CodingKeys: String, CodingKey {
        case id, targetKind, category, detail, status, reporterParticipantType, reportedParticipantType
        case targetEntityID = "targetEntityId"
        case createdUTC = "createdUtc"
    }
}

enum MobileCommunitySafetyResolution: String, CaseIterable, Identifiable, Sendable {
    case dismissed = "Dismissed"
    case needsInvestigation = "NeedsInvestigation"
    case actioned = "Actioned"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .dismissed: LegendLocalized("Dismiss")
        case .needsInvestigation: LegendLocalized("Needs investigation")
        case .actioned: LegendLocalized("Remove reported content")
        }
    }
}

private struct MobileCommunitySafetyResolutionRequest: Encodable, Sendable {
    let resolution: String
}

protocol MobileCommunitySafetyAPI: Sendable {
    func reports(accessToken: String) async throws -> [MobileCommunitySafetyReport]
    func resolve(
        reportID: UUID,
        resolution: MobileCommunitySafetyResolution,
        accessToken: String
    ) async throws
}

struct MobileUnavailableCommunitySafetyAPI: MobileCommunitySafetyAPI {
    func reports(accessToken: String) async throws -> [MobileCommunitySafetyReport] {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func resolve(
        reportID: UUID,
        resolution: MobileCommunitySafetyResolution,
        accessToken: String
    ) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionMobileCommunitySafetyAPI: MobileCommunitySafetyAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func reports(accessToken: String) async throws -> [MobileCommunitySafetyReport] {
        try await client.get(
            "/api/v1/mobile/community-safety/reports",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "take", value: "100")],
            headers: participantHeader,
            response: [MobileCommunitySafetyReport].self)
    }

    func resolve(
        reportID: UUID,
        resolution: MobileCommunitySafetyResolution,
        accessToken: String
    ) async throws {
        try await client.post(
            "/api/v1/mobile/community-safety/reports/\(reportID.uuidString)/resolution",
            body: MobileCommunitySafetyResolutionRequest(resolution: resolution.rawValue),
            accessToken: accessToken,
            headers: participantHeader)
    }

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }
}

@MainActor
final class MobileCommunitySafetyStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<[MobileCommunitySafetyReport]> = .idle
    @Published private(set) var isResolvingReportID: UUID?
    @Published private(set) var actionFailure: UserFacingFailure?

    private let api: any MobileCommunitySafetyAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    init(
        api: any MobileCommunitySafetyAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() async {
        state = .loading
        actionFailure = nil
        do {
            state = .loaded(try await api.reports(accessToken: accessTokenProvider()))
        } catch {
            let failure = failure(for: error, title: LegendLocalized("Community review unavailable"))
            state = .unavailable(failure)
            diagnostics.record(
                category: .networking,
                summary: "Community safety review load failed.",
                correlationID: failure.correlationID)
        }
    }

    @discardableResult
    func resolve(
        _ report: MobileCommunitySafetyReport,
        as resolution: MobileCommunitySafetyResolution
    ) async -> Bool {
        guard isResolvingReportID == nil else { return false }
        isResolvingReportID = report.id
        actionFailure = nil
        defer { isResolvingReportID = nil }
        do {
            try await api.resolve(
                reportID: report.id,
                resolution: resolution,
                accessToken: accessTokenProvider())
            if case .loaded(let reports) = state {
                state = .loaded(reports.filter { $0.id != report.id })
            }
            return true
        } catch {
            let failure = failure(for: error, title: LegendLocalized("Report not updated"))
            actionFailure = failure
            diagnostics.record(
                category: .networking,
                summary: "Community safety report resolution failed.",
                correlationID: failure.correlationID)
            return false
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        UserFacingFailure(
            title: title,
            message: LegendLocalized("Legend could not complete this community-review action. Try again."),
            correlationID: (error as? MobileAPIError)?.correlationID)
    }
}
