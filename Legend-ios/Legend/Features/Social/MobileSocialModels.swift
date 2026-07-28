import Foundation

struct MobileSocialSnapshot: Codable, Equatable, Sendable {
    let stories: [MobileSocialPost]
    let posts: [MobileSocialPost]
    let activity: [MobileSocialActivity]
    let activityCount: Int
}

struct MobileSocialAuthor: Codable, Equatable, Sendable {
    let identity: LogicalParticipantIdentity
    let profileID: String
    let displayName: String
    let avatar: ProfileAvatar?

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName, avatar
    }
}

struct MobileSocialComment: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let author: MobileSocialAuthor
    let body: String
    let createdUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, author, body
        case createdUTC = "createdUtc"
    }
}

struct MobileSocialPost: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let author: MobileSocialAuthor
    let contentType: String
    let body: String
    let postedUTC: Date
    let expiresUTC: Date?
    let reactionCount: Int
    let commentCount: Int
    let reactedByCurrentActor: Bool
    let followedByCurrentActor: Bool
    let media: [MobileSocialMedia]
    let comments: [MobileSocialComment]

    private enum CodingKeys: String, CodingKey {
        case id, author, contentType, body, reactionCount, commentCount, reactedByCurrentActor, followedByCurrentActor, media, comments
        case postedUTC = "postedUtc"
        case expiresUTC = "expiresUtc"
    }
}

struct MobileSocialMedia: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let displayOrder: Int
    let mediaKind: String
    let mimeType: String
    let fileSizeBytes: Int64
    let width: Int?
    let height: Int?
    let aspectRatio: Decimal?
    let durationSeconds: Decimal?
    let processingState: String
    let accessibilityText: String?

    var isImage: Bool {
        mediaKind.caseInsensitiveCompare("Image") == .orderedSame
    }
}

struct MobileSocialActivity: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let kind: String
    let actor: MobileSocialAuthor
    let postID: UUID?
    let occurredUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, kind, actor
        case postID = "postId"
        case occurredUTC = "occurredUtc"
    }

    var summary: String {
        switch kind {
        case "reaction":
            return "appreciated your update"
        case "comment":
            return "commented on your update"
        case "follow":
            return "followed your Legend profile"
        default:
            return "interacted with your update"
        }
    }

    var systemImage: String {
        switch kind {
        case "reaction":
            return "heart.fill"
        case "comment":
            return "bubble.right.fill"
        case "follow":
            return "person.badge.plus"
        default:
            return "bell.fill"
        }
    }
}

struct MobileCreateSocialPost: Codable, Sendable {
    let contentType: String
    let body: String
}

struct MobileCreateSocialComment: Codable, Sendable {
    let body: String
}

struct LegendSocialImageAttachment: Sendable {
    let file: MultipartFormFile
    let accessibilityText: String?
}

struct MobileToggleSocialFollow: Codable, Sendable {
    let followedUserID: String
    let followedParticipantType: ParticipantType

    private enum CodingKeys: String, CodingKey {
        case followedUserID = "followedUserId"
        case followedParticipantType
    }
}

struct MobileSocialFollowResult: Codable, Equatable, Sendable {
    let isFollowing: Bool
}

enum MobileSocialContentType: String, CaseIterable, Identifiable, Sendable {
    case post = "Post"
    case story = "Story"
    case reel = "Reel"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .post: "Post"
        case .story: "Story"
        case .reel: "Reel"
        }
    }
}
