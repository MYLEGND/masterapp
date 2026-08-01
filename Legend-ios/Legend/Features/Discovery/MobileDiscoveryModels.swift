import Foundation

/// How the server ordered a discovery page. The client never chooses relevance
/// directly: typing a query implies it.
enum MobileDiscoverySortMode: String, Codable, Sendable {
    case recommended = "Recommended"
    case relevance = "Relevance"
    case directory = "Directory"
}

/// The scope the server decided this caller has. Derived from the authenticated
/// participant type, never requested by the app.
enum MobileDiscoveryScope: String, Codable, Sendable {
    case community = "Community"
    case ownedClients = "OwnedClients"
}

enum MobileDiscoveryConnectionStatus: String, Codable, Sendable {
    case none = "None"
    case pending = "Pending"
    case accepted = "Accepted"
    case declined = "Declined"
    case cancelled = "Cancelled"
    case disconnected = "Disconnected"
    case blocked = "Blocked"
}

struct MobileDiscoveryRelationship: Codable, Equatable, Sendable {
    let followedByCurrentActor: Bool
    let followRequestPending: Bool?
    let followsCurrentActor: Bool
    let connectionStatus: MobileDiscoveryConnectionStatus
    let connectionID: UUID?
    let canRequestConnection: Bool
    let canFollow: Bool

    private enum CodingKeys: String, CodingKey {
        case followedByCurrentActor, followRequestPending, followsCurrentActor, connectionStatus, canRequestConnection, canFollow
        case connectionID = "connectionId"
    }

    init(
        followedByCurrentActor: Bool,
        followRequestPending: Bool? = nil,
        followsCurrentActor: Bool,
        connectionStatus: MobileDiscoveryConnectionStatus,
        connectionID: UUID?,
        canRequestConnection: Bool,
        canFollow: Bool
    ) {
        self.followedByCurrentActor = followedByCurrentActor
        self.followRequestPending = followRequestPending
        self.followsCurrentActor = followsCurrentActor
        self.connectionStatus = connectionStatus
        self.connectionID = connectionID
        self.canRequestConnection = canRequestConnection
        self.canFollow = canFollow
    }
}

struct MobileDiscoveryResult: Codable, Equatable, Identifiable, Sendable {
    let clientProfileID: UUID
    let identity: LogicalParticipantIdentity
    let displayName: String
    let headline: String?
    let location: String?
    let goals: [String]
    let interests: [String]
    let circleCodes: [String]
    let compatibilityScore: Int
    let matchExplanation: String?
    let relationship: MobileDiscoveryRelationship
    let avatar: ProfileAvatar?
    let username: String?
    let bio: String?
    let website: String?
    let publicEmail: String?
    let publicPhone: String?
    let isPrivate: Bool?
    let isVerified: Bool?
    let roleLabel: String?

    var id: UUID { clientProfileID }

    private enum CodingKeys: String, CodingKey {
        case identity, displayName, headline, location, goals, interests, circleCodes
        case compatibilityScore, matchExplanation, relationship, avatar, username, bio, website, publicEmail, publicPhone, isPrivate, isVerified, roleLabel
        case clientProfileID = "clientProfileId"
    }

    init(
        clientProfileID: UUID,
        identity: LogicalParticipantIdentity,
        displayName: String,
        headline: String?,
        location: String?,
        goals: [String],
        interests: [String],
        circleCodes: [String],
        compatibilityScore: Int,
        matchExplanation: String?,
        relationship: MobileDiscoveryRelationship,
        avatar: ProfileAvatar?,
        username: String? = nil,
        bio: String? = nil,
        website: String? = nil,
        publicEmail: String? = nil,
        isPrivate: Bool? = nil,
        isVerified: Bool? = nil,
        roleLabel: String? = nil,
        publicPhone: String? = nil
    ) {
        self.clientProfileID = clientProfileID
        self.identity = identity
        self.displayName = displayName
        self.headline = headline
        self.location = location
        self.goals = goals
        self.interests = interests
        self.circleCodes = circleCodes
        self.compatibilityScore = compatibilityScore
        self.matchExplanation = matchExplanation
        self.relationship = relationship
        self.avatar = avatar
        self.username = username
        self.bio = bio
        self.website = website
        self.publicEmail = publicEmail
        self.publicPhone = publicPhone
        self.isPrivate = isPrivate
        self.isVerified = isVerified
        self.roleLabel = roleLabel
    }

    /// A short, human line for the card: the strongest available context.
    var supportingLine: String? {
        if let matchExplanation, !matchExplanation.isEmpty { return matchExplanation }
        if let headline, !headline.isEmpty { return headline }
        return location
    }

}

struct MobileDiscoveryPage: Codable, Equatable, Sendable {
    let results: [MobileDiscoveryResult]
    let totalCount: Int
    let offset: Int
    let pageSize: Int
    let hasMore: Bool
    let sortMode: MobileDiscoverySortMode
    let scope: MobileDiscoveryScope
}

struct MobileDiscoveryProfile: Codable, Equatable, Sendable {
    let summary: MobileDiscoveryResult
    let introduction: String?
    let lifeStages: [String]
    let connectionTypes: [String]
    let contentVisibleToCurrentActor: Bool
    let followerCount: Int
    let followingCount: Int
    let postCount: Int
    let reelCount: Int
    let storyCount: Int
}
