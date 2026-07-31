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
    let followsCurrentActor: Bool
    let connectionStatus: MobileDiscoveryConnectionStatus
    let connectionID: UUID?
    let canRequestConnection: Bool
    let canFollow: Bool

    private enum CodingKeys: String, CodingKey {
        case followedByCurrentActor, followsCurrentActor, connectionStatus, canRequestConnection, canFollow
        case connectionID = "connectionId"
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

    var id: UUID { clientProfileID }

    private enum CodingKeys: String, CodingKey {
        case identity, displayName, headline, location, goals, interests, circleCodes
        case compatibilityScore, matchExplanation, relationship, avatar
        case clientProfileID = "clientProfileId"
    }

    /// A short, human line for the card: the strongest available context.
    var supportingLine: String? {
        if let matchExplanation, !matchExplanation.isEmpty { return matchExplanation }
        if let headline, !headline.isEmpty { return headline }
        return location
    }

    func replacing(relationship: MobileDiscoveryRelationship) -> MobileDiscoveryResult {
        MobileDiscoveryResult(
            clientProfileID: clientProfileID,
            identity: identity,
            displayName: displayName,
            headline: headline,
            location: location,
            goals: goals,
            interests: interests,
            circleCodes: circleCodes,
            compatibilityScore: compatibilityScore,
            matchExplanation: matchExplanation,
            relationship: relationship,
            avatar: avatar)
    }
}

extension MobileDiscoveryRelationship {
    func replacing(followedByCurrentActor: Bool) -> MobileDiscoveryRelationship {
        MobileDiscoveryRelationship(
            followedByCurrentActor: followedByCurrentActor,
            followsCurrentActor: followsCurrentActor,
            connectionStatus: connectionStatus,
            connectionID: connectionID,
            canRequestConnection: canRequestConnection,
            canFollow: canFollow)
    }

    func replacing(connectionStatus: MobileDiscoveryConnectionStatus) -> MobileDiscoveryRelationship {
        MobileDiscoveryRelationship(
            followedByCurrentActor: followedByCurrentActor,
            followsCurrentActor: followsCurrentActor,
            connectionStatus: connectionStatus,
            connectionID: connectionID,
            canRequestConnection: connectionStatus == .none && canRequestConnection,
            canFollow: canFollow)
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
