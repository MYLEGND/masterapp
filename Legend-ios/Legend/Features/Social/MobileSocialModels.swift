import Foundation

struct MobileSocialSnapshot: Codable, Equatable, Sendable {
    let stories: [MobileSocialPost]
    let posts: [MobileSocialPost]
    let hacs: [MobileSocialPost]
    let activity: [MobileSocialActivity]
    let activityCount: Int
    let currentProfileMetrics: MobileSocialProfileMetrics
    let creatorInsights: MobileSocialCreatorInsights

    private enum CodingKeys: String, CodingKey {
        case stories, posts, hacs, activity, activityCount, currentProfileMetrics, creatorInsights
    }

    init(
        stories: [MobileSocialPost],
        posts: [MobileSocialPost],
        hacs: [MobileSocialPost] = [],
        activity: [MobileSocialActivity],
        activityCount: Int,
        currentProfileMetrics: MobileSocialProfileMetrics,
        creatorInsights: MobileSocialCreatorInsights
    ) {
        self.stories = stories
        self.posts = posts
        self.hacs = hacs
        self.activity = activity
        self.activityCount = activityCount
        self.currentProfileMetrics = currentProfileMetrics
        self.creatorInsights = creatorInsights
    }

    /// Older on-device feed caches predate the dedicated HACS collection. Decode
    /// those caches as an empty FYP, then replace them with the server-ranked result
    /// on the next refresh instead of failing the complete social cache.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        stories = try container.decode([MobileSocialPost].self, forKey: .stories)
        posts = try container.decode([MobileSocialPost].self, forKey: .posts)
        hacs = try container.decodeIfPresent([MobileSocialPost].self, forKey: .hacs) ?? []
        activity = try container.decode([MobileSocialActivity].self, forKey: .activity)
        activityCount = try container.decode(Int.self, forKey: .activityCount)
        currentProfileMetrics = try container.decode(
            MobileSocialProfileMetrics.self,
            forKey: .currentProfileMetrics)
        creatorInsights = try container.decode(
            MobileSocialCreatorInsights.self,
            forKey: .creatorInsights)
    }
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
    let username: String?
    let bio: String?
    let website: String?
    let location: String?
    let publicEmail: String?
    let publicPhone: String?
    let isPrivate: Bool?
    let isVerified: Bool?
    let roleLabel: String?

    init(
        identity: LogicalParticipantIdentity,
        profileID: String,
        displayName: String,
        avatar: ProfileAvatar?,
        username: String? = nil,
        bio: String? = nil,
        website: String? = nil,
        location: String? = nil,
        publicEmail: String? = nil,
        isPrivate: Bool? = nil,
        isVerified: Bool? = nil,
        roleLabel: String? = nil,
        publicPhone: String? = nil
    ) {
        self.identity = identity
        self.profileID = profileID
        self.displayName = displayName
        self.avatar = avatar
        self.username = username
        self.bio = bio
        self.website = website
        self.location = location
        self.publicEmail = publicEmail
        self.publicPhone = publicPhone
        self.isPrivate = isPrivate
        self.isVerified = isVerified
        self.roleLabel = roleLabel
    }

    private enum CodingKeys: String, CodingKey {
        case identity
        case profileID = "profileId"
        case displayName, avatar, username, bio, website, location, publicEmail, publicPhone, isPrivate, isVerified, roleLabel
    }
}

/// One typed destination for a member's real Legend profile. Keeping this
/// route next to the server DTO prevents Discover, feed, and follow lists from
/// inventing separate profile representations.
struct LegendPublicProfileRoute: Identifiable, Hashable {
    let profile: MobileSocialAuthor
    let isFollowing: Bool
    let isFollowRequestPending: Bool
    let journeyConnectionID: UUID? = nil

    var id: String {
        "\(profile.identity.participantType.rawValue):\(profile.identity.userID)"
    }

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.id == rhs.id &&
            lhs.isFollowing == rhs.isFollowing &&
            lhs.isFollowRequestPending == rhs.isFollowRequestPending &&
            lhs.journeyConnectionID == rhs.journeyConnectionID
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
        hasher.combine(isFollowing)
        hasher.combine(isFollowRequestPending)
        hasher.combine(journeyConnectionID)
    }
}

/// The two relationship lists available from a Legend profile. Their raw values
/// are transport selectors; the app owns the member-facing labels.
enum MobileSocialFollowListKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case follows
    case followers

    var id: String { rawValue }

    var title: String {
        switch self {
        case .follows:
            return "Following"
        case .followers:
            return "Followers"
        }
    }

    var emptyMessage: String {
        switch self {
        case .follows:
            return "When you follow people in Legend, they will appear here."
        case .followers:
            return "People who follow you in Legend will appear here."
        }
    }
}

/// A person in the current profile's Follows or Followers list. The identifier
/// is the server's typed identity, so an agent and client with the same user ID
/// are still distinct people in the interface.
struct MobileSocialFollowListEntry: Codable, Equatable, Identifiable, Sendable {
    let profile: MobileSocialAuthor
    let followedByCurrentActor: Bool

    var id: LogicalParticipantIdentity { profile.identity }
}

struct MobileSocialFollowRequest: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let profile: MobileSocialAuthor
    let requestedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, profile
        case requestedUTC = "requestedUtc"
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
    let audience: String
    let location: String?
    let commentsEnabled: Bool
    let postedUTC: Date
    let expiresUTC: Date?
    let reactionCount: Int
    let commentCount: Int
    let reactedByCurrentActor: Bool
    let followedByCurrentActor: Bool
    let followRequestPending: Bool?
    let savedByCurrentActor: Bool
    let repostedByCurrentActor: Bool
    let metrics: MobileSocialPostMetrics
    let music: MobileSocialMusic?
    let media: [MobileSocialMedia]
    let comments: [MobileSocialComment]

    private enum CodingKeys: String, CodingKey {
        case id, author, contentType, body, audience, location, commentsEnabled, reactionCount, commentCount, reactedByCurrentActor, followedByCurrentActor, followRequestPending, savedByCurrentActor, repostedByCurrentActor, metrics, music, media, comments
        case postedUTC = "postedUtc"
        case expiresUTC = "expiresUtc"
    }

    init(
        id: UUID,
        author: MobileSocialAuthor,
        contentType: String,
        body: String,
        audience: String,
        location: String?,
        commentsEnabled: Bool,
        postedUTC: Date,
        expiresUTC: Date?,
        reactionCount: Int,
        commentCount: Int,
        reactedByCurrentActor: Bool,
        followedByCurrentActor: Bool,
        followRequestPending: Bool? = nil,
        savedByCurrentActor: Bool,
        repostedByCurrentActor: Bool,
        metrics: MobileSocialPostMetrics,
        music: MobileSocialMusic?,
        media: [MobileSocialMedia],
        comments: [MobileSocialComment]
    ) {
        self.id = id
        self.author = author
        self.contentType = contentType
        self.body = body
        self.audience = audience
        self.location = location
        self.commentsEnabled = commentsEnabled
        self.postedUTC = postedUTC
        self.expiresUTC = expiresUTC
        self.reactionCount = reactionCount
        self.commentCount = commentCount
        self.reactedByCurrentActor = reactedByCurrentActor
        self.followedByCurrentActor = followedByCurrentActor
        self.followRequestPending = followRequestPending
        self.savedByCurrentActor = savedByCurrentActor
        self.repostedByCurrentActor = repostedByCurrentActor
        self.metrics = metrics
        self.music = music
        self.media = media
        self.comments = comments
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
    var displayContentType: String {
        MobileSocialContentType.displayName(for: contentType)
    }

    /// A defensive client-side contract check. The server owns HACS selection and
    /// ranking, but a malformed or stale payload must never render an image in the
    /// vertical-video surface.
    var isVideoHac: Bool {
        contentType == MobileSocialContentType.hac.rawValue &&
            media.count == 1 &&
            media.allSatisfy(\.isVideo)
    }

    func replacing(
        reactionCount: Int? = nil,
        commentCount: Int? = nil,
        reactedByCurrentActor: Bool? = nil,
        followedByCurrentActor: Bool? = nil,
        followRequestPending: Bool? = nil,
        savedByCurrentActor: Bool? = nil,
        repostedByCurrentActor: Bool? = nil,
        metrics: MobileSocialPostMetrics? = nil,
        comments: [MobileSocialComment]? = nil
    ) -> MobileSocialPost {
        let resolvedReactionCount: Int = reactionCount ?? self.reactionCount
        let resolvedCommentCount: Int = commentCount ?? self.commentCount
        let resolvedReactedState: Bool =
            reactedByCurrentActor ?? self.reactedByCurrentActor
        let resolvedFollowedState: Bool =
            followedByCurrentActor ?? self.followedByCurrentActor
        let resolvedFollowRequestState: Bool? =
            followRequestPending ?? self.followRequestPending
        let resolvedSavedState: Bool =
            savedByCurrentActor ?? self.savedByCurrentActor
        let resolvedRepostedState: Bool =
            repostedByCurrentActor ?? self.repostedByCurrentActor
        let resolvedMetrics: MobileSocialPostMetrics = metrics ?? self.metrics
        let resolvedComments: [MobileSocialComment] = comments ?? self.comments

        return MobileSocialPost(
            id: id,
            author: author,
            contentType: contentType,
            body: body,
            audience: audience,
            location: location,
            commentsEnabled: commentsEnabled,
            postedUTC: postedUTC,
            expiresUTC: expiresUTC,
            reactionCount: resolvedReactionCount,
            commentCount: resolvedCommentCount,
            reactedByCurrentActor: resolvedReactedState,
            followedByCurrentActor: resolvedFollowedState,
            followRequestPending: resolvedFollowRequestState,
            savedByCurrentActor: resolvedSavedState,
            repostedByCurrentActor: resolvedRepostedState,
            metrics: resolvedMetrics,
            music: music,
            media: media,
            comments: resolvedComments)
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
    let audioURL: URL?
    let trimStartSeconds: Decimal?
    let trimEndSeconds: Decimal?
    let musicVolume: Decimal?
    let originalAudioVolume: Decimal?

    private enum CodingKeys: String, CodingKey {
        case providerID = "providerId"
        case providerTrackID = "providerTrackId"
        case trackTitle, artistName, trackDurationSeconds, trimStartSeconds, trimEndSeconds, musicVolume, originalAudioVolume
        case audioURL = "audioUrl"
    }
}

struct MobileSocialMusicTrack: Codable, Equatable, Identifiable, Sendable {
    let providerID: String
    let providerTrackID: String
    let trackTitle: String
    let artistName: String
    let trackDurationSeconds: Decimal
    let audioURL: URL?

    var id: String { "\(providerID):\(providerTrackID)" }

    private enum CodingKeys: String, CodingKey {
        case providerID = "providerId"
        case providerTrackID = "providerTrackId"
        case trackTitle, artistName, trackDurationSeconds
        case audioURL = "audioUrl"
    }
}

struct MobileSocialPostInsight: Codable, Equatable, Identifiable, Sendable {
    let postID: UUID
    let contentType: String
    let postedUTC: Date
    let metrics: MobileSocialPostMetrics
    let engagementRatePercentage: Decimal

    var id: UUID { postID }

    var displayContentType: String {
        MobileSocialContentType.displayName(for: contentType)
    }

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

extension MobileSocialProfileMetrics {
    func adjusting(
        postCountBy: Int = 0,
        videoCountBy: Int = 0,
        storyCountBy: Int = 0,
        followerCountBy: Int = 0,
        followingCountBy: Int = 0
    ) -> MobileSocialProfileMetrics {
        MobileSocialProfileMetrics(
            profile: profile,
            postCount: max(0, postCount + postCountBy),
            videoCount: max(0, videoCount + videoCountBy),
            storyCount: max(0, storyCount + storyCountBy),
            followerCount: max(0, followerCount + followerCountBy),
            followingCount: max(0, followingCount + followingCountBy),
            totalReactionCount: totalReactionCount,
            totalContentViewCount: totalContentViewCount,
            totalReachCount: totalReachCount,
            privateProfileVisitCount: privateProfileVisitCount
        )
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
    /// The server owns whether a protected creator-selected Hac poster exists.
    /// It is intentionally metadata only; the image is fetched through the
    /// same authorized social-media boundary as the video.
    let hasPreviewImage: Bool

    private enum CodingKeys: String, CodingKey {
        case id, displayOrder, mediaKind, mimeType, fileSizeBytes, width, height
        case aspectRatio, durationSeconds, processingState, accessibilityText
        case hasPreviewImage
    }

    init(
        id: UUID,
        displayOrder: Int,
        mediaKind: String,
        mimeType: String,
        fileSizeBytes: Int64,
        width: Int?,
        height: Int?,
        aspectRatio: Decimal?,
        durationSeconds: Decimal?,
        processingState: String,
        accessibilityText: String?,
        hasPreviewImage: Bool = false
    ) {
        self.id = id
        self.displayOrder = displayOrder
        self.mediaKind = mediaKind
        self.mimeType = mimeType
        self.fileSizeBytes = fileSizeBytes
        self.width = width
        self.height = height
        self.aspectRatio = aspectRatio
        self.durationSeconds = durationSeconds
        self.processingState = processingState
        self.accessibilityText = accessibilityText
        self.hasPreviewImage = hasPreviewImage
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(UUID.self, forKey: .id),
            displayOrder: try values.decode(Int.self, forKey: .displayOrder),
            mediaKind: try values.decode(String.self, forKey: .mediaKind),
            mimeType: try values.decode(String.self, forKey: .mimeType),
            fileSizeBytes: try values.decode(Int64.self, forKey: .fileSizeBytes),
            width: try values.decodeIfPresent(Int.self, forKey: .width),
            height: try values.decodeIfPresent(Int.self, forKey: .height),
            aspectRatio: try values.decodeIfPresent(Decimal.self, forKey: .aspectRatio),
            durationSeconds: try values.decodeIfPresent(Decimal.self, forKey: .durationSeconds),
            processingState: try values.decode(String.self, forKey: .processingState),
            accessibilityText: try values.decodeIfPresent(String.self, forKey: .accessibilityText),
            hasPreviewImage: try values.decodeIfPresent(Bool.self, forKey: .hasPreviewImage) ?? false)
    }

    var isImage: Bool {
        mediaKind.caseInsensitiveCompare("Image") == .orderedSame
    }

    var isVideo: Bool {
        mediaKind.caseInsensitiveCompare("Video") == .orderedSame
    }

    /// MIME subtypes are not filesystem extensions: `video/quicktime` must be
    /// materialized as `.mov`, not `.quicktime`, before AVFoundation opens it.
    /// This is the single extension authority for protected media playback.
    var playbackFileExtension: String {
        switch mimeType.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "video/mp4":
            "mp4"
        case "video/quicktime":
            "mov"
        case "video/webm":
            "webm"
        case "image/jpeg":
            "jpg"
        case "image/png":
            "png"
        case "image/webp":
            "webp"
        case "image/heic":
            "heic"
        case "image/heif":
            "heif"
        default:
            isVideo ? "mp4" : "media"
        }
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
    let audience: String
    let location: String?
    let commentsEnabled: Bool
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

/// Publishing always uses the account-level privacy policy. This transport value
/// is retained for existing create-post callers; the server normalizes it.
enum MobileSocialAudience: String, CaseIterable, Identifiable, Sendable {
    case authorizedNetwork = "AuthorizedNetwork"
    case followers = "Followers"
    case mutualConnections = "MutualConnections"

    var id: String { rawValue }
}

struct MobileSocialPublishRequest: Sendable {
    let contentType: MobileSocialContentType
    let body: String
    let files: [MultipartFormFile]
    let accessibilityText: String?
    let music: MobileSocialMusicSelection?
    let audience: MobileSocialAudience
    let location: String?
    let commentsEnabled: Bool
    /// JPEG still selected by the creator for a Hac. It is not counted as post
    /// media and is sent in the `preview` multipart field.
    let previewImage: MultipartFormFile?

    init(
        contentType: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        audience: MobileSocialAudience,
        location: String?,
        commentsEnabled: Bool,
        previewImage: MultipartFormFile? = nil
    ) {
        self.contentType = contentType
        self.body = body
        self.files = files
        self.accessibilityText = accessibilityText
        self.music = music
        self.audience = audience
        self.location = location
        self.commentsEnabled = commentsEnabled
        self.previewImage = previewImage
    }
}

/// The creation surface has one explicit progression.  Media, captions, and
/// server publication are never inferred from unrelated view booleans.
enum LegendSocialCreationStage: Equatable {
    case library
    case preparingMedia
    case camera
    case metadata
    case share
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
    /// The exact fraction of outbound multipart bytes confirmed by URLSession.
    /// It remains at 1 while the server validates and commits the new post.
    var uploadProgress: Double
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
    let isPending: Bool?

    init(
        isFollowing: Bool,
        isPending: Bool? = nil
    ) {
        self.isFollowing = isFollowing
        self.isPending = isPending
    }

    var hasPendingRequest: Bool { isPending ?? false }
}

struct MobileSocialFollowRequestDecision: Codable, Sendable {
    let approve: Bool
}

/// The creation and playback contract for one Legend social format.
///
/// This is deliberately presentation-framework agnostic so the library picker,
/// editor canvas, camera, feed card, and validation layer use the same rules.
/// `Reel` remains the persisted API value for backwards compatibility; the
/// product vocabulary exposed to members is always **Hac**.
struct LegendSocialContentFormat: Equatable, Sendable {
    let maximumMediaItems: Int
    let allowsTextOnlyPublication: Bool
    let acceptsImages: Bool
    let acceptsVideos: Bool
    let maximumVideoDurationSeconds: Double?
    let mediaAspectRatio: Double
    let featuredPreviewWidth: Double
    let featuredPreviewHeight: Double
    let companionPreviewWidth: Double
    let companionPreviewHeight: Double
    let emptyPreviewHeight: Double
    let editorMaximumWidth: Double
    /// Stories and Hacs use an intentional 9:16 canvas. Posts retain the
    /// source media's measured aspect ratio in their feed card.
    let usesFixedCanvasAspectRatio: Bool

    var requiresVideo: Bool {
        acceptsVideos && !acceptsImages
    }

    func acceptsVideo(duration: TimeInterval) -> Bool {
        guard acceptsVideos else { return false }
        guard let maximumVideoDurationSeconds else { return true }
        return duration <= maximumVideoDurationSeconds
    }

    var maximumVideoDurationDescription: String? {
        guard let maximumVideoDurationSeconds else { return nil }
        if maximumVideoDurationSeconds == LegendSocialVideoUploadPolicy.maximumDurationSeconds {
            return "10 minutes or less"
        }
        return "up to \(Int(maximumVideoDurationSeconds)) seconds"
    }
}

enum MobileSocialContentType: String, CaseIterable, Identifiable, Sendable {
    case post = "Post"
    case story = "Story"
    // The raw value remains server-compatible with existing persisted content.
    case hac = "Reel"

    var id: String { rawValue }

    static func displayName(for rawValue: String) -> String {
        Self(rawValue: rawValue)?.displayName ?? rawValue
    }

    var displayName: String {
        switch self {
        case .post: "Post"
        case .story: "Story"
        case .hac: "Hac"
        }
    }

    var newContentTitle: String {
        switch self {
        case .post: "New post"
        case .story: "New story"
        case .hac: "New Hac"
        }
    }

    var editingTitle: String {
        switch self {
        case .post: "Edit post"
        case .story: "Edit story"
        case .hac: "Edit Hac"
        }
    }

    var creationPrompt: String {
        switch self {
        case .post:
            "Share a focused update with your Legend network."
        case .story:
            "Share a 24-hour moment with your Legend network."
        case .hac:
            "Share a practical insight with your Legend network."
        }
    }

    /// The only iOS format authority. Do not recreate any of these values in
    /// individual views: doing so is how pickers, canvases, and playback cards
    /// drift into incompatible layouts.
    var format: LegendSocialContentFormat {
        switch self {
        case .post:
            LegendSocialContentFormat(
                maximumMediaItems: 10,
                allowsTextOnlyPublication: true,
                acceptsImages: true,
                acceptsVideos: true,
                maximumVideoDurationSeconds: LegendSocialVideoUploadPolicy.maximumDurationSeconds,
                mediaAspectRatio: 1,
                featuredPreviewWidth: 286,
                featuredPreviewHeight: 286,
                companionPreviewWidth: 132,
                companionPreviewHeight: 132,
                emptyPreviewHeight: 286,
                editorMaximumWidth: 390,
                usesFixedCanvasAspectRatio: false)

        case .story:
            LegendSocialContentFormat(
                maximumMediaItems: 1,
                allowsTextOnlyPublication: false,
                acceptsImages: true,
                acceptsVideos: true,
                maximumVideoDurationSeconds: LegendSocialVideoUploadPolicy.maximumDurationSeconds,
                mediaAspectRatio: 9.0 / 16.0,
                featuredPreviewWidth: 248,
                featuredPreviewHeight: 440,
                companionPreviewWidth: 124,
                companionPreviewHeight: 220,
                emptyPreviewHeight: 440,
                editorMaximumWidth: 350,
                usesFixedCanvasAspectRatio: true)

        case .hac:
            LegendSocialContentFormat(
                maximumMediaItems: 1,
                allowsTextOnlyPublication: false,
                acceptsImages: false,
                acceptsVideos: true,
                maximumVideoDurationSeconds: LegendSocialVideoUploadPolicy.maximumDurationSeconds,
                mediaAspectRatio: 9.0 / 16.0,
                featuredPreviewWidth: 248,
                featuredPreviewHeight: 440,
                companionPreviewWidth: 124,
                companionPreviewHeight: 220,
                emptyPreviewHeight: 440,
                editorMaximumWidth: 350,
                usesFixedCanvasAspectRatio: true)
        }
    }

    var maximumMediaItems: Int {
        format.maximumMediaItems
    }

    var acceptsImages: Bool {
        format.acceptsImages
    }

    var acceptsVideos: Bool {
        format.acceptsVideos
    }

    var requiresVideo: Bool {
        format.requiresVideo
    }

    var mediaSelectionTitle: String {
        switch self {
        case .post:
            "Add photos or video"
        case .story:
            "Add a photo or video"
        case .hac:
            "Add a video"
        }
    }

    var mediaSelectionHint: String {
        let maximumItems = format.maximumMediaItems
        let duration = format.maximumVideoDurationDescription ?? "the supported duration"

        switch self {
        case .post:
            return "Choose up to \(maximumItems) photos or videos. Videos can be \(duration)."
        case .story:
            return "Share one visual moment that disappears after 24 hours. Videos can be \(duration)."
        case .hac:
            return "Choose one vertical video \(duration) for your Hac, or record one with your camera."
        }
    }

    var systemImage: String {
        switch self {
        case .post: "square.and.pencil"
        case .story: "circle.dashed.inset.filled"
        case .hac: "play.rectangle.fill"
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
