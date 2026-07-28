import Photos
import XCTest
@testable import Legend

@MainActor
final class MobileSocialContractTests: XCTestCase {
    func testSocialSnapshotDecodesTypedAuthorsWithIndependentProfileImages() throws {
        let data = Data("""
        {
          "stories": [],
          "posts": [
            {
              "id": "00000000-0000-0000-0000-000000000101",
              "author": {
                "identity": { "userId": "shared-user", "participantType": "Agent" },
                "profileId": "00000000-0000-0000-0000-000000000001",
                "displayName": "Agent Identity",
                "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "YWdlbnQ=" }
              },
              "contentType": "Post",
              "body": "Agent update",
              "postedUtc": "2026-07-26T12:00:00Z",
              "expiresUtc": null,
              "reactionCount": 1,
              "commentCount": 0,
              "reactedByCurrentActor": false,
              "followedByCurrentActor": false,
              "savedByCurrentActor": false,
              "repostedByCurrentActor": false,
              "metrics": { "viewCount": 4, "uniqueViewerCount": 3, "reactionCount": 1, "commentCount": 0, "replyCount": 0, "repostCount": 0, "saveCount": 0, "shareCount": 0, "profileVisitCount": 0, "followsGenerated": 0, "averageWatchDurationSeconds": null, "averageWatchCompletionPercentage": null, "storyExitCount": 0, "storyTapForwardCount": 0, "storyTapBackwardCount": 0 },
              "music": null,
              "media": [{
                "id": "00000000-0000-0000-0000-000000000111",
                "displayOrder": 0,
                "mediaKind": "Image",
                "mimeType": "image/png",
                "fileSizeBytes": 12,
                "width": 1,
                "height": 1,
                "aspectRatio": 1.0,
                "durationSeconds": null,
                "processingState": "Ready",
                "accessibilityText": "A secure Legend image"
              }],
              "comments": []
            },
            {
              "id": "00000000-0000-0000-0000-000000000102",
              "author": {
                "identity": { "userId": "shared-user", "participantType": "Client" },
                "profileId": "00000000-0000-0000-0000-000000000002",
                "displayName": "Client Identity",
                "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "Y2xpZW50" }
              },
              "contentType": "Post",
              "body": "Client update",
              "postedUtc": "2026-07-26T12:01:00Z",
              "expiresUtc": null,
              "reactionCount": 0,
              "commentCount": 0,
              "reactedByCurrentActor": true,
              "followedByCurrentActor": true,
              "savedByCurrentActor": true,
              "repostedByCurrentActor": false,
              "metrics": { "viewCount": 2, "uniqueViewerCount": 2, "reactionCount": 0, "commentCount": 0, "replyCount": 0, "repostCount": 0, "saveCount": 1, "shareCount": 0, "profileVisitCount": 0, "followsGenerated": 0, "averageWatchDurationSeconds": null, "averageWatchCompletionPercentage": null, "storyExitCount": 0, "storyTapForwardCount": 0, "storyTapBackwardCount": 0 },
              "music": null,
              "media": [],
              "comments": []
            }
          ],
          "activity": [],
          "activityCount": 1,
          "currentProfileMetrics": {
            "profile": { "identity": { "userId": "shared-user", "participantType": "Agent" }, "profileId": "00000000-0000-0000-0000-000000000001", "displayName": "Agent Identity", "avatar": null },
            "postCount": 1, "videoCount": 0, "storyCount": 0, "followerCount": 1, "followingCount": 0, "totalReactionCount": 1, "totalContentViewCount": 4, "totalReachCount": 3, "privateProfileVisitCount": 0
          },
          "creatorInsights": {
            "generatedUtc": "2026-07-26T12:02:00Z", "totalViews": 4, "totalReach": 3, "followerCount": 1, "followingCount": 0, "followersGained": 1, "profileVisits": 0, "totalReactions": 1, "totalComments": 0, "totalReplies": 0, "totalShares": 0, "totalReposts": 0, "totalSaves": 0, "engagementRatePercentage": 33.3, "topPosts": [], "topVideos": [], "topStories": []
          }
        }
        """.utf8)

        let snapshot = try JSONDecoder.mobile.decode(MobileSocialSnapshot.self, from: data)
        let agent = try XCTUnwrap(snapshot.posts.first)
        let client = try XCTUnwrap(snapshot.posts.last)

        XCTAssertEqual(agent.author.identity.userID, client.author.identity.userID)
        XCTAssertNotEqual(agent.author.identity, client.author.identity)
        XCTAssertEqual(agent.author.identity.participantType, .agent)
        XCTAssertEqual(client.author.identity.participantType, .client)
        XCTAssertEqual(agent.author.profileID, "00000000-0000-0000-0000-000000000001")
        XCTAssertEqual(client.author.profileID, "00000000-0000-0000-0000-000000000002")
        XCTAssertEqual(agent.author.avatar?.imageData, Data("agent".utf8))
        XCTAssertEqual(client.author.avatar?.imageData, Data("client".utf8))
        XCTAssertTrue(agent.media.first?.isImage ?? false)
        XCTAssertEqual(agent.media.first?.accessibilityText, "A secure Legend image")
        XCTAssertTrue(client.followedByCurrentActor)
        XCTAssertTrue(client.savedByCurrentActor)
        XCTAssertEqual(agent.metrics.uniqueViewerCount, 3)
    }

    func testSocialStorePreservesServerReturnedFollowState() async throws {
        let author = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(userID: "client-one", participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One",
            avatar: nil)
        let post = MobileSocialPost(
            id: UUID(),
            author: author,
            contentType: MobileSocialContentType.post.rawValue,
            body: "Build the plan.",
            postedUTC: .now,
            expiresUTC: nil,
            reactionCount: 0,
            commentCount: 0,
            reactedByCurrentActor: false,
            followedByCurrentActor: false,
            savedByCurrentActor: false,
            repostedByCurrentActor: false,
            metrics: testSocialMetrics,
            music: nil,
            media: [],
            comments: [])
        let api = RecordingSocialAPI(post: post)
        let store = MobileSocialStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.load()
        try await Task.sleep(for: .milliseconds(50))
        store.toggleFollow(author: author, sourcePostID: post.id)
        try await Task.sleep(for: .milliseconds(50))

        guard case .loaded(let snapshot) = store.state else {
            return XCTFail("Expected social store to load")
        }
        XCTAssertTrue(snapshot.posts[0].followedByCurrentActor)
        let followRequest = await api.lastFollowRequest()
        XCTAssertEqual(followRequest?.sourcePostID, post.id)
    }

    func testCreationRoutesExposeOnlyTheAuthoritativeMenuAndSupportedTypes() {
        XCTAssertEqual(
            MobileSocialContentType.allCases.map(\.rawValue),
            ["Post", "Story", "Reel"])
        XCTAssertNotEqual(
            LegendSocialCreationRoute.menu.id,
            LegendSocialCreationRoute.composer(.post).id)
        XCTAssertNotEqual(
            LegendSocialCreationRoute.composer(.post).id,
            LegendSocialCreationRoute.composer(.story).id)
    }

    func testPhotoLibraryAuthorizationMapsEachNativeState() {
        XCTAssertEqual(
            LegendPhotoLibraryAuthorization(.notDetermined),
            .notDetermined)
        XCTAssertEqual(
            LegendPhotoLibraryAuthorization(.authorized),
            .authorized)
        XCTAssertEqual(
            LegendPhotoLibraryAuthorization(.limited),
            .limited)
        XCTAssertEqual(
            LegendPhotoLibraryAuthorization(.denied),
            .denied)
        XCTAssertEqual(
            LegendPhotoLibraryAuthorization(.restricted),
            .restricted)
    }

    func testMediaPublishForwardsTheSelectedSupportedContentType() async throws {
        let author = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(
                userID: "client-one",
                participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One",
            avatar: nil)
        let post = MobileSocialPost(
            id: UUID(),
            author: author,
            contentType: MobileSocialContentType.reel.rawValue,
            body: "A focused reel.",
            postedUTC: .now,
            expiresUTC: nil,
            reactionCount: 0,
            commentCount: 0,
            reactedByCurrentActor: false,
            followedByCurrentActor: false,
            savedByCurrentActor: false,
            repostedByCurrentActor: false,
            metrics: testSocialMetrics,
            music: nil,
            media: [],
            comments: [])
        let api = RecordingSocialAPI(post: post)
        let store = MobileSocialStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        let published = await store.publish(MobileSocialPublishRequest(
            contentType: .reel,
            body: "A focused reel.",
            files: [MultipartFormFile(
                fieldName: "files",
                fileName: "legend-reel.mp4",
                mimeType: "video/mp4",
                data: Data([0, 1, 2, 3]))],
            accessibilityText: "A Legend reel",
            music: nil))

        XCTAssertTrue(published)
        let contentTypes = await api.mediaContentTypes()
        XCTAssertEqual(contentTypes, [.reel])
    }

    func testMusicSelectionPreservesProviderVerifiedTrimAndMixMetadata() throws {
        let selection = MobileSocialMusicSelection(
            providerID: "licensed-provider",
            providerTrackID: "track-42",
            trimStartSeconds: 12.5,
            trimEndSeconds: 27.5,
            musicVolume: 0.8,
            originalAudioVolume: 0.25)

        let encoded = try JSONEncoder().encode(selection)
        let decoded = try JSONDecoder().decode(MobileSocialMusicSelection.self, from: encoded)

        XCTAssertEqual(decoded.providerID, "licensed-provider")
        XCTAssertEqual(decoded.providerTrackID, "track-42")
        XCTAssertEqual(decoded.trimStartSeconds, 12.5)
        XCTAssertEqual(decoded.trimEndSeconds, 27.5)
        XCTAssertEqual(decoded.musicVolume, 0.8)
        XCTAssertEqual(decoded.originalAudioVolume, 0.25)
    }

    func testVideoViewMeasurementIsForwardedWithoutClientMetricCalculation() async throws {
        let author = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(userID: "client-one", participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One",
            avatar: nil)
        let post = MobileSocialPost(
            id: UUID(),
            author: author,
            contentType: MobileSocialContentType.reel.rawValue,
            body: "A measured reel.",
            postedUTC: .now,
            expiresUTC: nil,
            reactionCount: 0,
            commentCount: 0,
            reactedByCurrentActor: false,
            followedByCurrentActor: false,
            savedByCurrentActor: false,
            repostedByCurrentActor: false,
            metrics: testSocialMetrics,
            music: nil,
            media: [],
            comments: [])
        let api = RecordingSocialAPI(post: post)
        let store = MobileSocialStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.recordView(
            postID: post.id,
            watchDurationSeconds: 14.25,
            watchCompletionPercentage: 62.5)
        try await Task.sleep(for: .milliseconds(50))

        let measurement = await api.lastViewRequest()
        XCTAssertEqual(measurement?.postID, post.id)
        XCTAssertEqual(measurement?.request.watchDurationSeconds, 14.25)
        XCTAssertEqual(measurement?.request.watchCompletionPercentage, 62.5)
    }
}

private struct StubSocialAPI: MobileSocialAPI {
    let post: MobileSocialPost

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        testSnapshot(post: post)
    }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { post }
    func createMediaPost(type: MobileSocialContentType, body: String, files: [MultipartFormFile], accessibilityText: String?, music: MobileSocialMusicSelection?, accessToken: String) async throws -> MobileSocialPost { post }
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data { Data() }
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost { post }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment {
        MobileSocialComment(id: UUID(), author: post.author, parentCommentID: request.parentCommentID, body: request.body, createdUTC: .now)
    }
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult {
        MobileSocialFollowResult(isFollowing: true)
    }
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics { testSocialMetrics }
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight { testPostInsight(postID: postID) }
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] { [] }
}

private actor RecordingSocialAPI: MobileSocialAPI {
    private let post: MobileSocialPost
    private var recordedMediaContentTypes: [MobileSocialContentType] = []
    private var recordedFollowRequest: MobileToggleSocialFollow?
    private var recordedViewRequest: (postID: UUID, request: MobileRecordSocialView)?

    init(post: MobileSocialPost) {
        self.post = post
    }

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        testSnapshot(post: post)
    }

    func createPost(
        _ request: MobileCreateSocialPost,
        accessToken: String
    ) async throws -> MobileSocialPost {
        post
    }

    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        accessToken: String
    ) async throws -> MobileSocialPost {
        recordedMediaContentTypes.append(type)
        return post
    }

    func mediaData(assetID: UUID, accessToken: String) async throws -> Data {
        Data()
    }

    func toggleReaction(
        postID: UUID,
        accessToken: String
    ) async throws -> MobileSocialPost {
        post
    }

    func addComment(
        postID: UUID,
        request: MobileCreateSocialComment,
        accessToken: String
    ) async throws -> MobileSocialComment {
        MobileSocialComment(
            id: UUID(),
            author: post.author,
            parentCommentID: request.parentCommentID,
            body: request.body,
            createdUTC: .now)
    }

    func toggleFollow(
        _ request: MobileToggleSocialFollow,
        accessToken: String
    ) async throws -> MobileSocialFollowResult {
        recordedFollowRequest = request
        return MobileSocialFollowResult(isFollowing: true)
    }

    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { MobileSocialShareState(isActive: true) }
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics {
        recordedViewRequest = (postID, request)
        return testSocialMetrics
    }
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight { testPostInsight(postID: postID) }
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] { [] }

    func mediaContentTypes() -> [MobileSocialContentType] {
        recordedMediaContentTypes
    }

    func lastFollowRequest() -> MobileToggleSocialFollow? {
        recordedFollowRequest
    }

    func lastViewRequest() -> (postID: UUID, request: MobileRecordSocialView)? {
        recordedViewRequest
    }
}

private let testSocialMetrics = MobileSocialPostMetrics(
    viewCount: 0,
    uniqueViewerCount: 0,
    reactionCount: 0,
    commentCount: 0,
    replyCount: 0,
    repostCount: 0,
    saveCount: 0,
    shareCount: 0,
    profileVisitCount: 0,
    followsGenerated: 0,
    averageWatchDurationSeconds: nil,
    averageWatchCompletionPercentage: nil,
    storyExitCount: 0,
    storyTapForwardCount: 0,
    storyTapBackwardCount: 0)

private func testPostInsight(postID: UUID) -> MobileSocialPostInsight {
    MobileSocialPostInsight(
        postID: postID,
        contentType: MobileSocialContentType.post.rawValue,
        postedUTC: .now,
        metrics: testSocialMetrics,
        engagementRatePercentage: 0)
}

private func testSnapshot(post: MobileSocialPost) -> MobileSocialSnapshot {
    MobileSocialSnapshot(
        stories: [],
        posts: [post],
        activity: [],
        activityCount: 0,
        currentProfileMetrics: MobileSocialProfileMetrics(
            profile: post.author,
            postCount: 1,
            videoCount: 0,
            storyCount: 0,
            followerCount: 0,
            followingCount: 0,
            totalReactionCount: 0,
            totalContentViewCount: 0,
            totalReachCount: 0,
            privateProfileVisitCount: 0),
        creatorInsights: MobileSocialCreatorInsights(
            generatedUTC: .now,
            totalViews: 0,
            totalReach: 0,
            followerCount: 0,
            followingCount: 0,
            followersGained: 0,
            profileVisits: 0,
            totalReactions: 0,
            totalComments: 0,
            totalReplies: 0,
            totalShares: 0,
            totalReposts: 0,
            totalSaves: 0,
            engagementRatePercentage: 0,
            topPosts: [],
            topVideos: [],
            topStories: []))
}
