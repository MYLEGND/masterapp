import Foundation

enum ParticipantType: String, Codable, CaseIterable, Sendable {
    case agent = "Agent"
    case client = "Client"
}

/// Mirrors the server's messaging identity rule: a user identifier alone is not
/// a participant identity. The server remains authoritative for validating it.
struct LogicalParticipantIdentity: Codable, Hashable, Sendable {
    let userID: String
    let participantType: ParticipantType

    init(userID: String, participantType: ParticipantType) throws {
        let normalized = Self.normalize(userID)
        guard !normalized.isEmpty else {
            throw MobileIdentityError.emptyUserID
        }

        self.userID = normalized
        self.participantType = participantType
    }

    static func normalize(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    private enum CodingKeys: String, CodingKey {
        case userID = "userId"
        case participantType
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(
            userID: container.decode(String.self, forKey: .userID),
            participantType: container.decode(ParticipantType.self, forKey: .participantType)
        )
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(userID, forKey: .userID)
        try container.encode(participantType, forKey: .participantType)
    }
}

enum MobileIdentityError: LocalizedError, Equatable {
    case emptyUserID

    var errorDescription: String? {
        switch self {
        case .emptyUserID:
            return "The server did not provide a valid participant identity."
        }
    }
}

struct MobileActor: Codable, Equatable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let avatar: ProfileAvatar?

    init(
        identity: LogicalParticipantIdentity,
        profileID: String,
        displayName: String,
        avatar: ProfileAvatar?
    ) throws {
        let normalizedProfileID = profileID.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedDisplayName = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalizedProfileID.isEmpty, !normalizedDisplayName.isEmpty else {
            throw MobileIdentityError.emptyUserID
        }

        self.identity = identity
        self.profileID = normalizedProfileID
        self.displayName = normalizedDisplayName
        self.avatar = avatar
    }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName
        case avatar
    }
}

struct ProfileAvatar: Codable, Equatable, Sendable {
    let kind: String
    let contentType: String
    let base64Content: String?
    let resourcePath: String?

    init(
        kind: String,
        contentType: String,
        base64Content: String? = nil,
        resourcePath: String? = nil
    ) {
        self.kind = kind
        self.contentType = contentType
        self.base64Content = base64Content
        self.resourcePath = resourcePath
    }

    /// Inline bytes exist only for native, device-local previews. Server
    /// projections use `resourcePath` so parent JSON never carries image data.
    var imageData: Data? {
        base64Content.flatMap { value in
            Data(base64Encoded: value)
        }
    }
}
