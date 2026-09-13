import Foundation

struct MobileDailyScriptureOverride: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let displayDate: String
    let reference: String
    let translation: String
    let passageText: String
    let createdUTC: Date
    let updatedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, displayDate, reference, translation, passageText
        case createdUTC = "createdUtc"
        case updatedUTC = "updatedUtc"
    }
}

struct MobileDailyScriptureManagementSnapshot: Codable, Equatable, Sendable {
    let businessDate: String
    let current: MobileDailyScripture
    let upcoming: [MobileDailyScriptureOverride]
}

struct MobileDailyScriptureOverrideDraft: Encodable, Equatable, Sendable {
    let displayDate: String
    let reference: String
    let translation: String
    let passageText: String
}

protocol MobileDailyScriptureManagementAPI: Sendable {
    func management(accessToken: String) async throws -> MobileDailyScriptureManagementSnapshot
    func create(
        _ draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride
    func update(
        id: UUID,
        draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride
    func remove(id: UUID, accessToken: String) async throws
}

struct MobileUnavailableDailyScriptureManagementAPI: MobileDailyScriptureManagementAPI {
    func management(accessToken: String) async throws -> MobileDailyScriptureManagementSnapshot {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func create(
        _ draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func update(
        id: UUID,
        draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func remove(id: UUID, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionMobileDailyScriptureManagementAPI: MobileDailyScriptureManagementAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func management(accessToken: String) async throws -> MobileDailyScriptureManagementSnapshot {
        try await client.get(
            "/api/v1/mobile/daily-scripture/management",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileDailyScriptureManagementSnapshot.self)
    }

    func create(
        _ draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride {
        try await client.post(
            "/api/v1/mobile/daily-scripture/overrides",
            body: draft,
            accessToken: accessToken,
            idempotencyKey: UUID(),
            headers: participantHeader,
            response: MobileDailyScriptureOverride.self)
    }

    func update(
        id: UUID,
        draft: MobileDailyScriptureOverrideDraft,
        accessToken: String
    ) async throws -> MobileDailyScriptureOverride {
        try await client.put(
            "/api/v1/mobile/daily-scripture/overrides/\(id.uuidString)",
            body: draft,
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileDailyScriptureOverride.self)
    }

    func remove(id: UUID, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/daily-scripture/overrides/\(id.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }
}

@MainActor
final class MobileDailyScriptureManagementStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileDailyScriptureManagementSnapshot> = .idle
    @Published private(set) var isSaving = false
    @Published private(set) var actionFailure: UserFacingFailure?

    private let api: any MobileDailyScriptureManagementAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    init(
        api: any MobileDailyScriptureManagementAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() async {
        state = .loading
        do {
            let snapshot = try await api.management(accessToken: accessTokenProvider())
            state = .loaded(snapshot)
        } catch {
            let failure = UserFacingFailure(
                title: LegendLocalized("Daily Scripture unavailable"),
                message: LegendLocalized("Legend could not load the scripture schedule. Try again."),
                correlationID: (error as? MobileAPIError)?.correlationID)
            state = .unavailable(failure)
            diagnostics.record(
                category: .networking,
                summary: "Daily Scripture management load failed.",
                correlationID: failure.correlationID)
        }
    }

    func create(_ draft: MobileDailyScriptureOverrideDraft) async -> Bool {
        await save {
            _ = try await self.api.create(
                draft,
                accessToken: self.accessTokenProvider())
        }
    }

    func update(
        id: UUID,
        draft: MobileDailyScriptureOverrideDraft
    ) async -> Bool {
        await save {
            _ = try await self.api.update(
                id: id,
                draft: draft,
                accessToken: self.accessTokenProvider())
        }
    }

    func remove(id: UUID) async -> Bool {
        await save {
            try await self.api.remove(
                id: id,
                accessToken: self.accessTokenProvider())
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    private func save(_ operation: () async throws -> Void) async -> Bool {
        guard !isSaving else { return false }
        isSaving = true
        actionFailure = nil
        defer { isSaving = false }

        do {
            try await operation()
            await load()
            return true
        } catch {
            let failure = UserFacingFailure(
                title: LegendLocalized("Daily Scripture was not saved"),
                message: LegendLocalized("Your changes were not applied. Review the passage and try again."),
                correlationID: (error as? MobileAPIError)?.correlationID)
            actionFailure = failure
            diagnostics.record(
                category: .networking,
                summary: "Daily Scripture management save failed.",
                correlationID: failure.correlationID)
            return false
        }
    }
}
