import Foundation

/// The server's canonical notification ledger projection. The numeric badge is
/// intentionally returned with the list so every app surface uses one snapshot.
struct MobileNotificationUnreadCount: Codable, Equatable, Sendable {
    let unreadCount: Int
    let revision: Int64
    let updatedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case unreadCount, revision
        case updatedUTC = "updatedUtc"
    }
}

struct MobileNotificationItem: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let kind: String
    let title: String
    let detail: String
    let conversationID: UUID?
    let occurredUTC: Date
    let isRead: Bool
    let isCleared: Bool

    private enum CodingKeys: String, CodingKey {
        case id, kind, title, detail, isRead, isCleared
        case conversationID = "conversationId"
        case occurredUTC = "occurredUtc"
    }
}

struct MobileNotificationSnapshot: Codable, Equatable, Sendable {
    var badge: MobileNotificationUnreadCount
    var notifications: [MobileNotificationItem]
}

private struct MobileApnsDeviceRegistration: Encodable, Sendable {
    let deviceToken: String
    let environment: String
}

private struct MobileApnsDeviceRemoval: Encodable, Sendable {
    let deviceToken: String
}

/// The server's authenticated, redacted APNs status projection. It never
/// includes a raw device token, token hash, or provider credential.
struct MobilePushDiagnostic: Codable, Equatable, Sendable {
    let registrationState: String
    let environment: String?
    let lastRegistrationUTC: Date?
    let lastRegistrationResult: String
    let lastDeliveryUTC: Date?
    let deliveryState: String
    let lastAPNSStatus: Int?
    let lastAPNSReason: String?
    let deliveryAttemptCount: Int?

    private enum CodingKeys: String, CodingKey {
        case registrationState, environment, lastRegistrationResult, deliveryState
        case lastRegistrationUTC = "lastRegistrationUtc"
        case lastDeliveryUTC = "lastDeliveryUtc"
        case lastAPNSStatus = "lastApnsStatus"
        case lastAPNSReason = "lastApnsReason"
        case deliveryAttemptCount
    }
}

enum MobilePushRegistrationState: Equatable, Sendable {
    case unknown
    case registering
    case registered
    case failed
}

protocol MobileNotificationAPI: Sendable {
    func snapshot(accessToken: String) async throws -> MobileNotificationSnapshot
    func markRead(id: UUID, accessToken: String) async throws -> MobileNotificationUnreadCount
    func clearBadges(accessToken: String) async throws -> MobileNotificationUnreadCount
    func registerAPNSDevice(
        token: String,
        environment: String,
        accessToken: String
    ) async throws -> MobileNotificationUnreadCount
    func deactivateAPNSDevice(token: String, accessToken: String) async throws
    func pushDiagnostic(accessToken: String) async throws -> MobilePushDiagnostic
}

struct MobileUnavailableNotificationAPI: MobileNotificationAPI {
    func snapshot(accessToken: String) async throws -> MobileNotificationSnapshot {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func markRead(id: UUID, accessToken: String) async throws -> MobileNotificationUnreadCount {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func clearBadges(accessToken: String) async throws -> MobileNotificationUnreadCount {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func registerAPNSDevice(
        token: String,
        environment: String,
        accessToken: String
    ) async throws -> MobileNotificationUnreadCount {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func deactivateAPNSDevice(token: String, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func pushDiagnostic(accessToken: String) async throws -> MobilePushDiagnostic {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionMobileNotificationAPI: MobileNotificationAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }

    func snapshot(accessToken: String) async throws -> MobileNotificationSnapshot {
        try await client.get(
            "/api/v1/mobile/notifications",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileNotificationSnapshot.self)
    }

    func markRead(id: UUID, accessToken: String) async throws -> MobileNotificationUnreadCount {
        try await client.post(
            "/api/v1/mobile/notifications/\(id.uuidString)/read",
            body: MobileEmptyRequest(),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileNotificationUnreadCount.self)
    }

    func clearBadges(accessToken: String) async throws -> MobileNotificationUnreadCount {
        try await client.post(
            "/api/v1/mobile/notifications/clear-badges",
            body: MobileEmptyRequest(),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileNotificationUnreadCount.self)
    }

    func registerAPNSDevice(
        token: String,
        environment: String,
        accessToken: String
    ) async throws -> MobileNotificationUnreadCount {
        try await client.put(
            "/api/v1/mobile/notifications/devices/apns",
            body: MobileApnsDeviceRegistration(
                deviceToken: token,
                environment: environment),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileNotificationUnreadCount.self)
    }

    func deactivateAPNSDevice(token: String, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/notifications/devices/apns",
            body: MobileApnsDeviceRemoval(deviceToken: token),
            accessToken: accessToken,
            headers: participantHeader)
    }

    func pushDiagnostic(accessToken: String) async throws -> MobilePushDiagnostic {
        try await client.get(
            "/api/v1/mobile/notifications/devices/apns/status",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobilePushDiagnostic.self)
    }
}

/// Client synchronization is deliberately thin: the database-backed API owns
/// state and revisioning; this store only mirrors a returned server snapshot
/// into the icon and any in-app notification surface.
@MainActor
final class MobileNotificationStore: ObservableObject {
    @Published private(set) var snapshot: MobileNotificationSnapshot?
    @Published private(set) var isSynchronizing = false
    @Published private(set) var pushDiagnostic: MobilePushDiagnostic?
    @Published private(set) var isRefreshingPushDiagnostic = false
    @Published private(set) var pushRegistrationState: MobilePushRegistrationState = .unknown
    @Published private(set) var lastPushRegistrationAttemptUTC: Date?

    private let api: any MobileNotificationAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var lastRegisteredDevice: (token: String, environment: String)?

    init(
        api: any MobileNotificationAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    /// Pull-to-sync on activation, app launch, and user refresh. A failed pull
    /// leaves the last verified badge in place rather than inventing a count.
    func sync() async {
        guard !isSynchronizing else { return }
        isSynchronizing = true
        defer { isSynchronizing = false }

        do {
            apply(try await api.snapshot(accessToken: try await accessTokenProvider()))
        } catch {
            diagnostics.record(
                category: .networking,
                summary: "Notification synchronization was unavailable.",
                correlationID: (error as? MobileAPIError)?.correlationID)
        }
    }

    func markRead(_ notificationID: UUID) async {
        do {
            apply(badge: try await api.markRead(
                id: notificationID,
                accessToken: try await accessTokenProvider()))
            if var snapshot {
                snapshot.notifications = snapshot.notifications.map { notification in
                    guard notification.id == notificationID else { return notification }
                    return MobileNotificationItem(
                        id: notification.id,
                        kind: notification.kind,
                        title: notification.title,
                        detail: notification.detail,
                        conversationID: notification.conversationID,
                        occurredUTC: notification.occurredUTC,
                        isRead: true,
                        isCleared: notification.isCleared)
                }
                self.snapshot = snapshot
            }
        } catch {
            diagnostics.record(category: .networking, summary: "Notification could not be marked read.")
        }
    }

    /// Explicit user action for settings or a notification center. This never
    /// runs automatically on launch because that would make the icon diverge
    /// from the unread state visible in the app.
    func clearBadges() async {
        do {
            apply(badge: try await api.clearBadges(accessToken: try await accessTokenProvider()))
            if var snapshot {
                snapshot.notifications = snapshot.notifications.map { notification in
                    guard !notification.isRead else { return notification }
                    return MobileNotificationItem(
                        id: notification.id,
                        kind: notification.kind,
                        title: notification.title,
                        detail: notification.detail,
                        conversationID: notification.conversationID,
                        occurredUTC: notification.occurredUTC,
                        isRead: notification.isRead,
                        isCleared: true)
                }
                self.snapshot = snapshot
            }
        } catch {
            diagnostics.record(category: .networking, summary: "Notification badges could not be cleared.")
        }
    }

    func registerAPNSDevice(token: String, environment: String) async {
        let normalizedToken = token.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let normalizedEnvironment = environment.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalizedToken.isEmpty,
              normalizedEnvironment == "sandbox" || normalizedEnvironment == "production" else {
            return
        }
        guard lastRegisteredDevice?.token != normalizedToken ||
              lastRegisteredDevice?.environment != normalizedEnvironment else {
            return
        }

        lastPushRegistrationAttemptUTC = Date()
        pushRegistrationState = .registering
        do {
            apply(badge: try await api.registerAPNSDevice(
                token: normalizedToken,
                environment: normalizedEnvironment,
                accessToken: try await accessTokenProvider()))
            lastRegisteredDevice = (normalizedToken, normalizedEnvironment)
            pushRegistrationState = .registered
        } catch {
            // Retain no token on failure so the next active lifecycle pass can retry.
            pushRegistrationState = .failed
            diagnostics.record(category: .networking, summary: "Push notifications could not be registered.")
        }
    }

    /// Removes only this app installation's current token from the active actor
    /// before local credentials are cleared. A failure must not block sign-out.
    func deactivateAPNSDevice(token: String?) async {
        guard let token else { return }
        let normalizedToken = token.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalizedToken.isEmpty else { return }

        do {
            try await api.deactivateAPNSDevice(
                token: normalizedToken,
                accessToken: try await accessTokenProvider())
            if lastRegisteredDevice?.token == normalizedToken {
                lastRegisteredDevice = nil
            }
            pushDiagnostic = nil
        } catch {
            diagnostics.record(category: .networking, summary: "Push registration could not be removed during sign out.")
        }
    }

    func refreshPushDiagnostic() async {
        guard !isRefreshingPushDiagnostic else { return }
        isRefreshingPushDiagnostic = true
        defer { isRefreshingPushDiagnostic = false }

        do {
            pushDiagnostic = try await api.pushDiagnostic(
                accessToken: try await accessTokenProvider())
        } catch {
            diagnostics.record(category: .networking, summary: "Push notification status was unavailable.")
        }
    }

    /// Push-to-update from the authenticated SignalR stream. Revisions make an
    /// older in-flight event harmless; a subsequent lifecycle pull rehydrates
    /// the ordered list itself.
    func applyRealtime(unreadCount: Int, revision: Int64) {
        if let current = snapshot?.badge, revision < current.revision { return }
        apply(badge: MobileNotificationUnreadCount(
            unreadCount: unreadCount,
            revision: revision,
            updatedUTC: Date()))
    }

    private func apply(_ next: MobileNotificationSnapshot) {
        snapshot = next
        NativeUnreadBadge.update(with: next.badge.unreadCount)
    }

    private func apply(badge: MobileNotificationUnreadCount) {
        if let current = snapshot?.badge, badge.revision < current.revision { return }
        if var snapshot {
            snapshot = MobileNotificationSnapshot(badge: badge, notifications: snapshot.notifications)
            self.snapshot = snapshot
        } else {
            snapshot = MobileNotificationSnapshot(badge: badge, notifications: [])
        }
        NativeUnreadBadge.update(with: badge.unreadCount)
    }
}
