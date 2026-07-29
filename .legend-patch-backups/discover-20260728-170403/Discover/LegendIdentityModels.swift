import Foundation

/// Canonical public identity used by Legend community features.
///
/// This identity is independent from CRM records, professional agent-client
/// relationships, and messaging conversation membership.
struct LegendIdentity: Codable, Equatable, Identifiable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let username: String
    let displayName: String
    let avatar: ProfileAvatar?
    let bio: String?
    let isVerified: Bool
    let isPrivate: Bool
    let isDiscoverable: Bool
    let followerCount: Int
    let followingCount: Int
    let postCount: Int

    var id: LogicalParticipantIdentity {
        identity
    }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case username
        case displayName
        case avatar
        case bio
        case isVerified
        case isPrivate
        case isDiscoverable
        case followerCount
        case followingCount
        case postCount
    }
}

/// A member returned by Legend discovery or people search.
struct LegendIdentitySearchResult: Codable, Equatable, Identifiable, Sendable {
    let profile: LegendIdentity
    let isFollowing: Bool
    let followsCurrentActor: Bool
    let hasPendingFollowRequest: Bool
    let mutualConnectionCount: Int

    var id: LogicalParticipantIdentity {
        profile.identity
    }
}

/// Server-ranked discovery payload.
///
/// Discovery sections remain separate so ranking can evolve without coupling
/// the contract to messaging recipients.
struct LegendDiscoverSnapshot: Codable, Equatable, Sendable {
    let suggestedPeople: [LegendIdentitySearchResult]
    let recentlyActivePeople: [LegendIdentitySearchResult]
}
