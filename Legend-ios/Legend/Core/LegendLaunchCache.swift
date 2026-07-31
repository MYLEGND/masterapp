import Foundation

/// On-device cache of the last known good launch state.
///
/// Legend used to need two serial network round trips before it could show anything:
/// one to resolve the session, another to load Home. This cache lets a returning user
/// open straight into their own shell with their last content already on screen, while
/// the network revalidates silently behind it.
///
/// Nothing here is an authority. Tokens stay in the keychain, and every cached value is
/// replaced the moment the server answers. If the server disagrees, the server wins.
///
/// Everything is written with complete file protection, so the payload is unreadable
/// while the device is locked, and the whole cache is destroyed on sign-out.
protocol LegendLaunchCaching: Sendable {
    func readSession() -> MobileSessionCacheEntry?
    func writeSession(_ entry: MobileSessionCacheEntry)

    func readPayload(_ kind: LegendLaunchPayloadKind, actorKey: String) -> Data?
    func writePayload(_ data: Data, kind: LegendLaunchPayloadKind, actorKey: String)

    func clear()
}

enum LegendLaunchPayloadKind: String, Sendable, CaseIterable {
    case home
    case socialFeed
}

/// The identity half of a launch: enough to render the correct shell before the
/// network confirms anything.
struct MobileSessionCacheEntry: Codable, Equatable, Sendable {
    let actor: MobileActor
    let capabilities: [String]
    let permittedParticipantTypes: [ParticipantType]
    let cachedUtc: Date

    /// Cached identity is only trusted briefly. Past this the app resolves the session
    /// over the network before showing a shell, so a revoked account cannot linger.
    private static let maximumAge: TimeInterval = 60 * 60 * 24 * 14

    var isFresh: Bool {
        Date().timeIntervalSince(cachedUtc) < Self.maximumAge
    }

    var session: MobileSession {
        MobileSession(
            actor: actor,
            capabilities: Set(capabilities),
            permittedParticipantTypes: permittedParticipantTypes)
    }
}

/// A cache key that changes with the acting identity, so switching roles never shows
/// the previous role's content.
func legendLaunchActorKey(_ identity: LogicalParticipantIdentity) -> String {
    "\(identity.participantType.rawValue)-\(identity.userID)"
        .lowercased()
        .replacingOccurrences(of: "/", with: "_")
        .replacingOccurrences(of: ".", with: "_")
}

final class LegendLaunchCache: LegendLaunchCaching, @unchecked Sendable {
    private let directory: URL?
    private let lock = NSLock()

    init(directoryName: String = "LegendLaunchCache") {
        directory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent(directoryName, isDirectory: true)
    }

    func readSession() -> MobileSessionCacheEntry? {
        guard let data = read(fileNamed: "session.json") else { return nil }
        let entry = try? JSONDecoder().decode(MobileSessionCacheEntry.self, from: data)
        guard let entry, entry.isFresh else { return nil }
        return entry
    }

    func writeSession(_ entry: MobileSessionCacheEntry) {
        guard let data = try? JSONEncoder().encode(entry) else { return }
        write(data, fileNamed: "session.json")
    }

    func readPayload(_ kind: LegendLaunchPayloadKind, actorKey: String) -> Data? {
        read(fileNamed: fileName(for: kind, actorKey: actorKey))
    }

    func writePayload(_ data: Data, kind: LegendLaunchPayloadKind, actorKey: String) {
        write(data, fileNamed: fileName(for: kind, actorKey: actorKey))
    }

    func clear() {
        guard let directory else { return }
        lock.lock()
        defer { lock.unlock() }
        try? FileManager.default.removeItem(at: directory)
    }

    private func fileName(for kind: LegendLaunchPayloadKind, actorKey: String) -> String {
        "\(kind.rawValue)-\(actorKey).json"
    }

    private func read(fileNamed name: String) -> Data? {
        guard let directory else { return nil }
        lock.lock()
        defer { lock.unlock() }
        return try? Data(contentsOf: directory.appendingPathComponent(name))
    }

    private func write(_ data: Data, fileNamed name: String) {
        guard let directory else { return }
        lock.lock()
        defer { lock.unlock() }

        try? FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.protectionKey: FileProtectionType.complete])

        try? data.write(
            to: directory.appendingPathComponent(name),
            options: [.atomic, .completeFileProtection])
    }
}

/// A store's hook into the launch cache. Stores stay unaware of files and paths; they
/// only know how to hand over a value and take one back.
struct LegendStorePersistence<Value: Codable & Sendable>: Sendable {
    let read: @Sendable () -> Value?
    let write: @Sendable (Value) -> Void

    static func none() -> Self {
        Self(read: { nil }, write: { _ in })
    }

    static func cached(
        _ cache: any LegendLaunchCaching,
        kind: LegendLaunchPayloadKind,
        actorKey: String
    ) -> Self {
        Self(
            read: {
                guard let data = cache.readPayload(kind, actorKey: actorKey) else { return nil }
                return try? JSONDecoder.mobile.decode(Value.self, from: data)
            },
            write: { value in
                // The shared mobile coders are used on purpose: a cached payload must
                // round-trip through exactly the same date handling as a server payload.
                guard let data = try? JSONEncoder.mobile.encode(value) else { return }
                cache.writePayload(data, kind: kind, actorKey: actorKey)
            })
    }
}

/// Used by tests and by any build that should never touch the disk.
struct LegendEphemeralLaunchCache: LegendLaunchCaching {
    func readSession() -> MobileSessionCacheEntry? { nil }
    func writeSession(_ entry: MobileSessionCacheEntry) {}
    func readPayload(_ kind: LegendLaunchPayloadKind, actorKey: String) -> Data? { nil }
    func writePayload(_ data: Data, kind: LegendLaunchPayloadKind, actorKey: String) {}
    func clear() {}
}
