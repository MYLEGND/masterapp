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

protocol MobileNotificationAPI: Sendable {
    func snapshot(accessToken: String) async throws -> MobileNotificationSnapshot
    func markRead(id: UUID, accessToken: String) async throws -> MobileNotificationUnreadCount
    func clearBadges(accessToken: String) async throws -> MobileNotificationUnreadCount
    func registerAPNSDevice(
        token: String,
        environment: String,
        accessToken: String
    ) async throws -> MobileNotificationUnreadCount
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
}

/// Client synchronization is deliberately thin: the database-backed API owns
/// state and revisioning; this store only mirrors a returned server snapshot
/// into the icon and any in-app notification surface.
@MainActor
final class MobileNotificationStore: ObservableObject {
    @Published private(set) var snapshot: MobileNotificationSnapshot?
    @Published private(set) var isSynchronizing = false

    private let api: any MobileNotificationAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var lastRegisteredDeviceToken: String?

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
        guard !normalizedToken.isEmpty, normalizedToken != lastRegisteredDeviceToken else { return }

        do {
            apply(badge: try await api.registerAPNSDevice(
                token: normalizedToken,
                environment: environment,
                accessToken: try await accessTokenProvider()))
            lastRegisteredDeviceToken = normalizedToken
        } catch {
            // Retain no token on failure so the next active lifecycle pass can retry.
            diagnostics.record(category: .networking, summary: "Push notifications could not be registered.")
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
