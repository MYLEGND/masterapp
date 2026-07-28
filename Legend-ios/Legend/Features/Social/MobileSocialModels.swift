import Foundation

struct MobileSocialSnapshot: Codable, Equatable, Sendable {
    let stories: [MobileSocialPost]
    let posts: [MobileSocialPost]
    let activity: [MobileSocialActivity]
    let activityCount: Int
    let currentProfileMetrics: MobileSocialProfileMetrics
    let creatorInsights: MobileSocialCreatorInsights
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
    let parentCommentID: UUID?
    let body: String
    let createdUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, author, body
        case parentCommentID = "parentCommentId"
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
    let savedByCurrentActor: Bool
    let repostedByCurrentActor: Bool
    let metrics: MobileSocialPostMetrics
    let music: MobileSocialMusic?
    let media: [MobileSocialMedia]
    let comments: [MobileSocialComment]

    private enum CodingKeys: String, CodingKey {
        case id, author, contentType, body, reactionCount, commentCount, reactedByCurrentActor, followedByCurrentActor, savedByCurrentActor, repostedByCurrentActor, metrics, music, media, comments
        case postedUTC = "postedUtc"
        case expiresUTC = "expiresUtc"
    }
}

struct MobileSocialPostMetrics: Codable, Equatable, Sendable {
    let viewCount: Int
    let uniqueViewerCount: Int
    let reactionCount: Int
    let commentCount: Int
    let replyCount: Int
    let repostCount: Int
    let saveCount: Int
    let shareCount: Int
    let profileVisitCount: Int
    let followsGenerated: Int
    let averageWatchDurationSeconds: Decimal?
    let averageWatchCompletionPercentage: Decimal?
    let storyExitCount: Int
    let storyTapForwardCount: Int
    let storyTapBackwardCount: Int
}

struct MobileSocialMusic: Codable, Equatable, Sendable {
    let providerID: String
    let providerTrackID: String
    let trackTitle: String
    let artistName: String
    let trackDurationSeconds: Decimal
    let previewURL: URL?
    let trimStartSeconds: Decimal?
    let trimEndSeconds: Decimal?
    let musicVolume: Decimal?
    let originalAudioVolume: Decimal?

    private enum CodingKeys: String, CodingKey {
        case providerID = "providerId"
        case providerTrackID = "providerTrackId"
        case trackTitle, artistName, trackDurationSeconds, trimStartSeconds, trimEndSeconds, musicVolume, originalAudioVolume
        case previewURL = "previewUrl"
    }
}

struct MobileSocialPostInsight: Codable, Equatable, Identifiable, Sendable {
    let postID: UUID
    let contentType: String
    let postedUTC: Date
    let metrics: MobileSocialPostMetrics
    let engagementRatePercentage: Decimal

    var id: UUID { postID }

    private enum CodingKeys: String, CodingKey {
        case postID = "postId"
        case contentType, metrics, engagementRatePercentage
        case postedUTC = "postedUtc"
    }
}

struct MobileSocialCreatorInsights: Codable, Equatable, Sendable {
    let generatedUTC: Date
    let totalViews: Int
    let totalReach: Int
    let followerCount: Int
    let followingCount: Int
    let followersGained: Int
    let profileVisits: Int
    let totalReactions: Int
    let totalComments: Int
    let totalReplies: Int
    let totalShares: Int
    let totalReposts: Int
    let totalSaves: Int
    let engagementRatePercentage: Decimal
    let topPosts: [MobileSocialPostInsight]
    let topVideos: [MobileSocialPostInsight]
    let topStories: [MobileSocialPostInsight]

    private enum CodingKeys: String, CodingKey {
        case totalViews, totalReach, followerCount, followingCount, followersGained, profileVisits, totalReactions, totalComments, totalReplies, totalShares, totalReposts, totalSaves, engagementRatePercentage, topPosts, topVideos, topStories
        case generatedUTC = "generatedUtc"
    }
}

struct MobileSocialProfileMetrics: Codable, Equatable, Sendable {
    let profile: MobileSocialAuthor
    let postCount: Int
    let videoCount: Int
    let storyCount: Int
    let followerCount: Int
    let followingCount: Int
    let totalReactionCount: Int
    let totalContentViewCount: Int
    let totalReachCount: Int
    let privateProfileVisitCount: Int?
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
    let parentCommentID: UUID?

    private enum CodingKeys: String, CodingKey {
        case body
        case parentCommentID = "parentCommentId"
    }
}

struct MobileSocialPublishRequest: Sendable {
    let contentType: MobileSocialContentType
    let body: String
    let files: [MultipartFormFile]
    let accessibilityText: String?
    let music: MobileSocialMusicSelection?
}

struct MobileSocialMusicSelection: Codable, Equatable, Sendable {
    let providerID: String
    let providerTrackID: String
    let trimStartSeconds: Decimal
    let trimEndSeconds: Decimal
    let musicVolume: Decimal
    let originalAudioVolume: Decimal

    private enum CodingKeys: String, CodingKey {
        case providerID = "providerId"
        case providerTrackID = "providerTrackId"
        case trimStartSeconds, trimEndSeconds, musicVolume, originalAudioVolume
    }
}

struct MobileSocialShareState: Codable, Equatable, Sendable {
    let isActive: Bool
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

    var creationTitle: String {
        switch self {
        case .post: "Share a post"
        case .story: "Create a story"
        case .reel: "Create a reel"
        }
    }

    var creationPrompt: String {
        switch self {
        case .post:
            "Share a focused update with your authorized Legend network."
        case .story:
            "Share a 24-hour moment with your authorized Legend network."
        case .reel:
            "Share a photo or video moment with your authorized Legend network."
        }
    }

    var systemImage: String {
        switch self {
        case .post: "square.and.pencil"
        case .story: "circle.dashed.inset.filled"
        case .reel: "play.rectangle.fill"
        }
    }
}

enum LegendSocialCreationRoute: Identifiable {
    case menu
    case composer(MobileSocialContentType)

    var id: String {
        switch self {
        case .menu:
            "create-menu"
        case .composer(let type):
            "composer-\(type.rawValue)"
        }
    }
}
