import Foundation

struct MobileSocialSnapshot: Codable, Equatable, Sendable {
    let stories: [MobileSocialPost]
    let posts: [MobileSocialPost]
    let activity: [MobileSocialActivity]
    let activityCount: Int
    let currentProfileMetrics: MobileSocialProfileMetrics
    let creatorInsights: MobileSocialCreatorInsights
}

/// A presentation collection for one logical Story owner. Story posts remain
/// independently persisted on the server so expiration, analytics, and viewer
/// tracking continue to be evaluated for each item.
struct MobileSocialStoryCollection: Equatable, Identifiable, Sendable {
    let author: MobileSocialAuthor
    let items: [MobileSocialPost]

    var id: String {
        "\(author.identity.participantType.rawValue):\(author.identity.userID)"
    }

    static func grouped(from stories: [MobileSocialPost]) -> [Self] {
        var ownerOrder: [LogicalParticipantIdentity] = []
        var authors: [LogicalParticipantIdentity: MobileSocialAuthor] = [:]
        var itemsByOwner: [LogicalParticipantIdentity: [MobileSocialPost]] = [:]

        for story in stories where story.contentType == MobileSocialContentType.story.rawValue {
            let owner = story.author.identity
            if itemsByOwner[owner] == nil {
                ownerOrder.append(owner)
                authors[owner] = story.author
            }
            itemsByOwner[owner, default: []].append(story)
        }

        return ownerOrder.compactMap { owner in
            guard let author = authors[owner], let items = itemsByOwner[owner] else {
                return nil
            }

            return MobileSocialStoryCollection(
                author: author,
                items: items.sorted { $0.postedUTC < $1.postedUTC })
        }
    }
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

extension MobileSocialPost {
    func replacing(
        reactionCount: Int? = nil,
        commentCount: Int? = nil,
        reactedByCurrentActor: Bool? = nil,
        followedByCurrentActor: Bool? = nil,
        savedByCurrentActor: Bool? = nil,
        repostedByCurrentActor: Bool? = nil,
        metrics: MobileSocialPostMetrics? = nil,
        comments: [MobileSocialComment]? = nil
    ) -> MobileSocialPost {
        MobileSocialPost(
            id: id,
            author: author,
            contentType: contentType,
            body: body,
            postedUTC: postedUTC,
            expiresUTC: expiresUTC,
            reactionCount: reactionCount ?? self.reactionCount,
            commentCount: commentCount ?? self.commentCount,
            reactedByCurrentActor: reactedByCurrentActor ?? self.reactedByCurrentActor,
            followedByCurrentActor: followedByCurrentActor ?? self.followedByCurrentActor,
            savedByCurrentActor: savedByCurrentActor ?? self.savedByCurrentActor,
            repostedByCurrentActor: repostedByCurrentActor ?? self.repostedByCurrentActor,
            metrics: metrics ?? self.metrics,
            music: music,
            media: media,
            comments: comments ?? self.comments)
    }
}

extension MobileSocialPostMetrics {
    func adjusting(
        reactionCountBy: Int = 0,
        commentCountBy: Int = 0,
        repostCountBy: Int = 0,
        saveCountBy: Int = 0
    ) -> MobileSocialPostMetrics {
        MobileSocialPostMetrics(
            viewCount: viewCount,
            uniqueViewerCount: uniqueViewerCount,
            reactionCount: max(0, reactionCount + reactionCountBy),
            commentCount: max(0, commentCount + commentCountBy),
            replyCount: replyCount,
            repostCount: max(0, repostCount + repostCountBy),
            saveCount: max(0, saveCount + saveCountBy),
            shareCount: shareCount,
            profileVisitCount: profileVisitCount,
            followsGenerated: followsGenerated,
            averageWatchDurationSeconds: averageWatchDurationSeconds,
            averageWatchCompletionPercentage: averageWatchCompletionPercentage,
            storyExitCount: storyExitCount,
            storyTapForwardCount: storyTapForwardCount,
            storyTapBackwardCount: storyTapBackwardCount)
    }
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

struct MobileSocialMusicTrack: Codable, Equatable, Identifiable, Sendable {
    let providerID: String
    let providerTrackID: String
    let trackTitle: String
    let artistName: String
    let trackDurationSeconds: Decimal
    let previewURL: URL?

    var id: String { "\(providerID):\(providerTrackID)" }

    private enum CodingKeys: String, CodingKey {
        case providerID = "providerId"
        case providerTrackID = "providerTrackId"
        case trackTitle, artistName, trackDurationSeconds
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

    var isVideo: Bool {
        mediaKind.caseInsensitiveCompare("Video") == .orderedSame
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

struct MobileUpdateSocialPost: Codable, Sendable {
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

/// The creation surface has one explicit progression.  Media, captions, and
/// server publication are never inferred from unrelated view booleans.
enum LegendSocialCreationStage: Equatable {
    case library
    case preparingMedia
    case camera
    case metadata
    case music
    case handedOff
    case failed(String)
}

/// A visible, server-backed publication lifecycle.  The client owns only the
/// temporary presentation state; the created post always comes from the
/// protected social API before it is inserted into the feed.
enum MobileSocialPublicationStage: Equatable, Sendable {
    case preparing
    case uploading
    case processing
    case published
    case failed

    var title: String {
        switch self {
        case .preparing: "Preparing update"
        case .uploading: "Uploading update"
        case .processing: "Publishing update"
        case .published: "Update shared"
        case .failed: "Upload needs attention"
        }
    }

    var systemImage: String {
        switch self {
        case .preparing: "wand.and.stars"
        case .uploading: "arrow.up.circle.fill"
        case .processing: "gearshape.2.fill"
        case .published: "checkmark.circle.fill"
        case .failed: "exclamationmark.triangle.fill"
        }
    }
}

struct MobileSocialPublication: Equatable, Identifiable, Sendable {
    let id: UUID
    let contentType: MobileSocialContentType
    var stage: MobileSocialPublicationStage
    var failureMessage: String?
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

struct MobileRecordSocialView: Codable, Sendable {
    let watchDurationSeconds: Decimal?
    let watchCompletionPercentage: Decimal?
    let storyInteractionType: String?
}

struct MobileToggleSocialFollow: Codable, Sendable {
    let followedUserID: String
    let followedParticipantType: ParticipantType
    let sourcePostID: UUID?

    private enum CodingKeys: String, CodingKey {
        case followedUserID = "followedUserId"
        case followedParticipantType
        case sourcePostID = "sourcePostId"
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
            "Share a video moment with your authorized Legend network."
        }
    }

    var maximumMediaItems: Int {
        switch self {
        case .post:
            10
        case .story, .reel:
            1
        }
    }

    var acceptsImages: Bool {
        self != .reel
    }

    var acceptsVideos: Bool {
        true
    }

    var requiresVideo: Bool {
        self == .reel
    }

    var mediaSelectionTitle: String {
        switch self {
        case .post:
            "Add photos or video"
        case .story:
            "Add a photo or video"
        case .reel:
            "Add a video"
        }
    }

    var mediaSelectionHint: String {
        switch self {
        case .post:
            "Choose up to 10 photos or videos from your library, or capture a new moment."
        case .story:
            "Share one visual moment that disappears after 24 hours."
        case .reel:
            "Choose one video from your library, or record one with your camera."
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
