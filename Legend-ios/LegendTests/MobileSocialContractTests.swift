import Photos
import XCTest
@testable import Legend

@MainActor
final class MobileSocialContractTests: XCTestCase {
    func testProtectedQuickTimeVideoUsesMOVPlaybackExtension() {
        let quickTime = MobileSocialMedia(
            id: UUID(),
            displayOrder: 0,
            mediaKind: "Video",
            mimeType: "video/quicktime",
            fileSizeBytes: 1,
            width: nil,
            height: nil,
            aspectRatio: nil,
            durationSeconds: nil,
            processingState: "Ready",
            accessibilityText: nil)
        let mp4 = MobileSocialMedia(
            id: UUID(),
            displayOrder: 0,
            mediaKind: "Video",
            mimeType: "video/mp4",
            fileSizeBytes: 1,
            width: nil,
            height: nil,
            aspectRatio: nil,
            durationSeconds: nil,
            processingState: "Ready",
            accessibilityText: nil)

        XCTAssertEqual(quickTime.playbackFileExtension, "mov")
        XCTAssertEqual(mp4.playbackFileExtension, "mp4")
    }

    func testHacPlaybackRequiresReadyVideo() throws {
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
            contentType: MobileSocialContentType.hac.rawValue,
            body: "Preparing Hac.",
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
            media: [MobileSocialMedia(
                id: UUID(),
                displayOrder: 0,
                mediaKind: "Video",
                mimeType: "video/mp4",
                fileSizeBytes: 4,
                width: 1080,
                height: 1920,
                aspectRatio: 0.5625,
                durationSeconds: nil,
                processingState: "PendingProcessing",
                accessibilityText: "A Legend Hac")],
            comments: [])

        XCTAssertFalse(post.isVideoHac)
    }

    func testPostMetadataHidesParticipantRoleOutsideAgentContext() {
        XCTAssertEqual(
            LegendSocialPostMetadata.summary(
                contentType: MobileSocialContentType.post.rawValue,
                authorParticipantType: .client,
                viewerParticipantType: .client),
            MobileSocialContentType.post.rawValue)
        XCTAssertEqual(
            LegendSocialPostMetadata.summary(
                contentType: MobileSocialContentType.hac.rawValue,
                authorParticipantType: .client,
                viewerParticipantType: .agent),
            "Hac · Client")
    }

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
              "audience": "AuthorizedNetwork",
              "location": null,
              "commentsEnabled": true,
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
              "audience": "AuthorizedNetwork",
              "location": null,
              "commentsEnabled": true,
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
        XCTAssertTrue(snapshot.hacs.isEmpty)
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
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
        XCTAssertEqual(snapshot.currentProfileMetrics.followingCount, 1)
        let followRequest = await api.lastFollowRequest()
        XCTAssertEqual(followRequest?.sourcePostID, post.id)
    }

    func testSocialStoreIgnoresRepeatedReactionWhileTheFirstRequestIsRunning() async throws {
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
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
        let api = RecordingSocialAPI(
            post: post,
            reactionDelay: .milliseconds(150))
        let store = MobileSocialStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.load()
        try await Task.sleep(for: .milliseconds(50))
        store.toggleReaction(postID: post.id)
        store.toggleReaction(postID: post.id)
        try await Task.sleep(for: .milliseconds(250))

        let reactionCallCount = await api.reactionCallCount()
        XCTAssertEqual(
            reactionCallCount,
            1,
            "A repeated tap must not turn a single reaction into a second toggle.")
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

    func testCreationFormatsExposeTheirServerCompatibleMediaRules() {
        XCTAssertEqual(MobileSocialContentType.post.maximumMediaItems, 10)
        XCTAssertTrue(MobileSocialContentType.post.acceptsImages)
        XCTAssertTrue(MobileSocialContentType.post.acceptsVideos)
        XCTAssertFalse(MobileSocialContentType.post.requiresVideo)

        XCTAssertEqual(MobileSocialContentType.story.maximumMediaItems, 1)
        XCTAssertTrue(MobileSocialContentType.story.acceptsImages)
        XCTAssertTrue(MobileSocialContentType.story.acceptsVideos)
        XCTAssertFalse(MobileSocialContentType.story.requiresVideo)

        XCTAssertEqual(MobileSocialContentType.hac.maximumMediaItems, 1)
        XCTAssertFalse(MobileSocialContentType.hac.acceptsImages)
        XCTAssertTrue(MobileSocialContentType.hac.acceptsVideos)
        XCTAssertTrue(MobileSocialContentType.hac.requiresVideo)
    }

    func testHacPlaybackWindowOwnsActiveNextAndWarmCandidate() {
        XCTAssertEqual(LegendHacPlaybackWindow.maximumPlayerCount, 2)

        XCTAssertEqual(
            LegendHacPlaybackWindow.retainedIndexes(
                activeIndex: 2,
                count: 7
            ),
            [2, 3]
        )

        XCTAssertEqual(
            LegendHacPlaybackWindow.prefetchIndexes(
                activeIndex: 2,
                count: 7
            ),
            [4]
        )

        XCTAssertEqual(
            LegendHacPlaybackWindow.retainedIndexes(
                activeIndex: 0,
                count: 4
            ),
            [0, 1]
        )

        XCTAssertEqual(
            LegendHacPlaybackWindow.prefetchIndexes(
                activeIndex: 0,
                count: 4
            ),
            [2]
        )

        XCTAssertEqual(
            LegendHacPlaybackWindow.retainedIndexes(
                activeIndex: 3,
                count: 4
            ),
            [3]
        )

        XCTAssertEqual(
            LegendHacPlaybackWindow.prefetchIndexes(
                activeIndex: 3,
                count: 4
            ),
            []
        )
    }

    func testCreationFormatsKeepCanvasAndCaptureRulesInOneAuthority() {
        let post = MobileSocialContentType.post.format
        let story = MobileSocialContentType.story.format
        let hac = MobileSocialContentType.hac.format

        XCTAssertEqual(
            post.mediaAspectRatio,
            LegendSocialPostCanvas.portrait.rawValue)
        XCTAssertEqual(
            post.supportedCanvasAspectRatios,
            LegendSocialPostCanvas.allCases.map(\.rawValue))
        XCTAssertFalse(post.usesFixedCanvasAspectRatio)
        XCTAssertTrue(post.allowsTextOnlyPublication)
        XCTAssertEqual(post.maximumVideoDurationSeconds, 600)

        XCTAssertEqual(story.mediaAspectRatio, 9.0 / 16.0)
        XCTAssertTrue(story.usesFixedCanvasAspectRatio)
        XCTAssertFalse(story.allowsTextOnlyPublication)
        XCTAssertTrue(story.acceptsVideo(duration: 600))
        XCTAssertFalse(story.acceptsVideo(duration: 600.01))

        XCTAssertEqual(hac.mediaAspectRatio, 9.0 / 16.0)
        XCTAssertTrue(hac.usesFixedCanvasAspectRatio)
        XCTAssertFalse(hac.allowsTextOnlyPublication)
        XCTAssertEqual(hac.maximumVideoDurationSeconds, 600)
        XCTAssertTrue(hac.acceptsVideo(duration: 600))
        XCTAssertFalse(hac.acceptsVideo(duration: 600.01))
        XCTAssertEqual(
            LegendSocialVideoPreparation.PreparationError.exceedsDurationLimit.errorDescription,
            "Videos must be 10 minutes or less.")
    }

    func testStoryCollectionsUseTheCompleteLogicalOwnerIdentity() throws {
        let sharedUserID = "shared-user"
        let agent = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(
                userID: sharedUserID,
                participantType: .agent),
            profileID: "agent-profile",
            displayName: "Agent identity",
            avatar: nil)
        let client = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(
                userID: sharedUserID,
                participantType: .client),
            profileID: "client-profile",
            displayName: "Client identity",
            avatar: nil)

        let firstAgentStory = socialPost(
            author: agent,
            contentType: .story,
            postedUTC: Date(timeIntervalSince1970: 100))
        let secondAgentStory = socialPost(
            author: agent,
            contentType: .story,
            postedUTC: Date(timeIntervalSince1970: 200))
        let clientStory = socialPost(
            author: client,
            contentType: .story,
            postedUTC: Date(timeIntervalSince1970: 150))

        let collections = MobileSocialStoryCollection.grouped(
            from: [secondAgentStory, clientStory, firstAgentStory])

        XCTAssertEqual(collections.count, 2)
        XCTAssertEqual(collections[0].author.identity, agent.identity)
        XCTAssertEqual(collections[0].items.map(\.id), [firstAgentStory.id, secondAgentStory.id])
        XCTAssertEqual(collections[1].author.identity, client.identity)
        XCTAssertEqual(collections[1].items.map(\.id), [clientStory.id])
        XCTAssertNotEqual(collections[0].id, collections[1].id)
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
            contentType: MobileSocialContentType.hac.rawValue,
            body: "A focused Hac.",
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
            media: [MobileSocialMedia(
                id: UUID(),
                displayOrder: 0,
                mediaKind: "Video",
                mimeType: "video/mp4",
                fileSizeBytes: 4,
                width: 1080,
                height: 1920,
                aspectRatio: 0.5625,
                durationSeconds: 10,
                processingState: "Ready",
                accessibilityText: "A Legend Hac")],
            comments: [])
        let api = RecordingSocialAPI(post: post)
        let store = MobileSocialStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.load()
        for _ in 0..<200 where store.state == .loading {
            try await Task.sleep(for: .milliseconds(5))
        }

        let request = MobileSocialPublishRequest(
            contentType: .hac,
            body: "A focused Hac.",
            files: [MultipartFormFile(
                fieldName: "files",
                fileName: "legend-hac.mp4",
                mimeType: "video/mp4",
                data: Data([0, 1, 2, 3]))],
            accessibilityText: "A Legend Hac",
            music: nil,
            audience: .authorizedNetwork,
            location: nil,
            commentsEnabled: true)

        XCTAssertTrue(store.beginPublication(request))
        for _ in 0..<200 where store.publication?.stage != .published {
            try await Task.sleep(for: .milliseconds(5))
        }

        XCTAssertEqual(store.publication?.stage, .published)
        XCTAssertEqual(store.publication?.uploadProgress, 1)
        let contentTypes = await api.mediaContentTypes()
        XCTAssertEqual(contentTypes, [.hac])
        guard case .loaded(let snapshot) = store.state else {
            return XCTFail("Expected the Hac to be inserted after server confirmation")
        }
        XCTAssertEqual(snapshot.hacs.first?.id, post.id)
        XCTAssertTrue(snapshot.hacs.first?.isVideoHac ?? false)
    }

    func testHacPublicationRejectsNonVideoMediaBeforeUpload() throws {
        let author = MobileSocialAuthor(
            identity: try LogicalParticipantIdentity(
                userID: "client-one",
                participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One",
            avatar: nil)
        let post = socialPost(author: author, contentType: .post, postedUTC: .now)
        let store = MobileSocialStore(
            api: StubSocialAPI(post: post),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        XCTAssertFalse(store.beginPublication(MobileSocialPublishRequest(
            contentType: .hac,
            body: "An invalid Hac.",
            files: [MultipartFormFile(
                fieldName: "files",
                fileName: "not-a-video.jpg",
                mimeType: "image/jpeg",
                data: Data([0]))],
            accessibilityText: nil,
            music: nil,
            audience: .authorizedNetwork,
            location: nil,
            commentsEnabled: true)))
        XCTAssertNil(store.publication)
        XCTAssertEqual(
            store.actionFailure?.message,
            "A Hac requires exactly one video.")
    }

    /// Audience, location, and the comment switch are collected in the share sheet.
    /// They previously stopped at the view layer and never reached the server.
    @MainActor
    func testShareDetailsReachTheMediaPublishRequest() async throws {
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
            contentType: MobileSocialContentType.post.rawValue,
            body: "Detailed update.",
            audience: MobileSocialAudience.followers.rawValue,
            location: "Nashville, TN",
            commentsEnabled: false,
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
            contentType: .post,
            body: "Detailed update.",
            files: [MultipartFormFile(
                fieldName: "files",
                fileName: "legend-post.jpg",
                mimeType: "image/jpeg",
                data: Data([0, 1, 2, 3]))],
            accessibilityText: nil,
            music: nil,
            audience: .followers,
            location: "Nashville, TN",
            commentsEnabled: false))

        XCTAssertTrue(published)
        let details = await api.lastMediaPostDetails()
        XCTAssertEqual(details.audience, .followers)
        XCTAssertEqual(details.location, "Nashville, TN")
        XCTAssertEqual(details.commentsEnabled, false)
    }

    /// A failed upload used to hold the publication slot forever, which disabled the
    /// composer's Next and Share controls for the rest of the session.
    @MainActor
    func testAFailedPublicationDoesNotPermanentlyBlockTheComposer() async throws {
        let store = MobileSocialStore(
            api: MobileUnavailableSocialAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        let request = MobileSocialPublishRequest(
            contentType: .post,
            body: "This upload will fail.",
            files: [],
            accessibilityText: nil,
            music: nil,
            audience: .authorizedNetwork,
            location: nil,
            commentsEnabled: true)

        XCTAssertTrue(store.beginPublication(request))

        // Let the failing upload settle.
        for _ in 0..<200 where store.publication?.stage != .failed {
            try await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTAssertEqual(store.publication?.stage, .failed)

        // A failed publication is not "in flight", so the composer stays usable.
        XCTAssertFalse(store.isPublishing)

        // And a new publication can start without first clearing the old one by hand.
        XCTAssertTrue(store.beginPublication(request))

        store.discardPendingPublication()
        XCTAssertNil(store.publication)
        XCTAssertFalse(store.isPublishing)
    }

    func testProfileCatalogAndPostMutationsUseConfirmedServerResponses() async throws {
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
            contentType: MobileSocialContentType.post.rawValue,
            body: "Original move.",
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
        store.loadProfilePosts()
        try await Task.sleep(for: .milliseconds(50))
        guard case .loaded(let profilePosts) = store.profileContentState else {
            return XCTFail("Expected the server profile catalog")
        }
        XCTAssertEqual(profilePosts.map(\.id), [post.id])

        let didUpdate = await store.updatePost(postID: post.id, body: "Server-confirmed move.")
        XCTAssertTrue(didUpdate)
        guard case .loaded(let updatedPosts) = store.profileContentState else {
            return XCTFail("Expected the updated server post")
        }
        XCTAssertEqual(updatedPosts.first?.body, "Server-confirmed move.")
        let updateRequest = await api.lastUpdateRequest()
        XCTAssertEqual(updateRequest?.body, "Server-confirmed move.")

        let didDelete = await store.deletePost(postID: post.id)
        XCTAssertTrue(didDelete)
        guard case .loaded(let remainingPosts) = store.profileContentState else {
            return XCTFail("Expected the confirmed post removal")
        }
        XCTAssertTrue(remainingPosts.isEmpty)
        guard case .loaded(let snapshot) = store.state else {
            return XCTFail("Expected the social snapshot to remain loaded")
        }
        XCTAssertEqual(snapshot.currentProfileMetrics.postCount, 0)
        let deletedPostID = await api.lastDeletedPostID()
        XCTAssertEqual(deletedPostID, post.id)
    }

    func testMediaAndPreviewRefreshUseTheirProtectedPaths() async throws {
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
            contentType: MobileSocialContentType.post.rawValue,
            body: "An image post.",
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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
        let assetID = UUID()

        let first = await store.mediaData(for: assetID)
        let cached = await store.mediaData(for: assetID)
        let refreshed = await store.mediaData(
            for: assetID,
            forceRefresh: true)
        let preview = await store.previewData(for: assetID)
        let mediaDataCallCount = await api.mediaDataCallCount()
        let previewDataCallCount = await api.previewDataCallCount()

        XCTAssertEqual(first, Data())
        XCTAssertEqual(cached, Data())
        XCTAssertEqual(refreshed, Data())
        XCTAssertEqual(preview, Data([0xFF, 0xD8, 0xFF, 0xD9]))
        XCTAssertEqual(mediaDataCallCount, 2)
        XCTAssertEqual(previewDataCallCount, 1)
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

    func testOpenMusicTrackDecodesItsDirectAudioURL() throws {
        let data = Data("""
        [{
          "providerId": "legend-open-fma",
          "providerTrackId": "psalters-all-yeshua",
          "trackTitle": "All Yeshua",
          "artistName": "Psalters",
          "trackDurationSeconds": 321.22,
          "audioUrl": "https://archive.org/download/us_vs_us-11170/Psalters_-_09_-_All_Yeshua.mp3"
        }]
        """.utf8)

        let track = try XCTUnwrap(JSONDecoder().decode([MobileSocialMusicTrack].self, from: data).first)
        let audioURL = try XCTUnwrap(track.audioURL)

        XCTAssertEqual(track.providerID, "legend-open-fma")
        XCTAssertEqual(track.trackTitle, "All Yeshua")
        XCTAssertEqual(audioURL.scheme, "https")
        XCTAssertEqual(audioURL.pathExtension.lowercased(), "mp3")
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
            contentType: MobileSocialContentType.hac.rawValue,
            body: "A measured Hac.",
            audience: MobileSocialAudience.authorizedNetwork.rawValue,
            location: nil,
            commentsEnabled: true,
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

    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] { [post] }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { post }
    func createMediaPost(type: MobileSocialContentType, body: String, files: [MultipartFormFile], accessibilityText: String?, music: MobileSocialMusicSelection?, audience: MobileSocialAudience, location: String?, commentsEnabled: Bool, uploadProgress: @escaping @Sendable (Double) -> Void, accessToken: String) async throws -> MobileSocialPost {
        uploadProgress(1)
        return post
    }
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost { updatedSocialPost(post, body: request.body) }
    func deletePost(postID: UUID, accessToken: String) async throws {}
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
    private let reactionDelay: Duration?
    private var recordedMediaContentTypes: [MobileSocialContentType] = []
    private var recordedFollowRequest: MobileToggleSocialFollow?
    private var recordedViewRequest: (postID: UUID, request: MobileRecordSocialView)?
    private var recordedUpdateRequest: MobileUpdateSocialPost?
    private var recordedDeletedPostID: UUID?
    private var recordedMediaDataCallCount = 0
    private var recordedPreviewDataCallCount = 0
    private var recordedMediaAudience: MobileSocialAudience?
    private var recordedMediaLocation: String?
    private var recordedMediaCommentsEnabled: Bool?
    private var recordedReactionCallCount = 0

    init(post: MobileSocialPost, reactionDelay: Duration? = nil) {
        self.post = post
        self.reactionDelay = reactionDelay
    }

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        testSnapshot(post: post)
    }

    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] {
        [post]
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
        audience: MobileSocialAudience,
        location: String?,
        commentsEnabled: Bool,
        uploadProgress: @escaping @Sendable (Double) -> Void,
        accessToken: String
    ) async throws -> MobileSocialPost {
        recordedMediaContentTypes.append(type)
        recordedMediaAudience = audience
        recordedMediaLocation = location
        recordedMediaCommentsEnabled = commentsEnabled
        uploadProgress(1)
        return post
    }

    func updatePost(
        postID: UUID,
        request: MobileUpdateSocialPost,
        accessToken: String
    ) async throws -> MobileSocialPost {
        recordedUpdateRequest = request
        return updatedSocialPost(post, body: request.body)
    }

    func deletePost(postID: UUID, accessToken: String) async throws {
        recordedDeletedPostID = postID
    }

    func mediaData(assetID: UUID, accessToken: String) async throws -> Data {
        recordedMediaDataCallCount += 1
        return Data()
    }

    func mediaDataCallCount() -> Int { recordedMediaDataCallCount }

    func previewData(assetID: UUID, accessToken: String) async throws -> Data {
        recordedPreviewDataCallCount += 1
        return Data([0xFF, 0xD8, 0xFF, 0xD9])
    }

    func previewDataCallCount() -> Int { recordedPreviewDataCallCount }

    func toggleReaction(
        postID: UUID,
        accessToken: String
    ) async throws -> MobileSocialPost {
        recordedReactionCallCount += 1
        if let reactionDelay {
            try await Task.sleep(for: reactionDelay)
        }
        return post
    }

    func reactionCallCount() -> Int { recordedReactionCallCount }

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

    func lastMediaPostDetails() -> (
        audience: MobileSocialAudience?,
        location: String?,
        commentsEnabled: Bool?
    ) {
        (recordedMediaAudience, recordedMediaLocation, recordedMediaCommentsEnabled)
    }

    func lastFollowRequest() -> MobileToggleSocialFollow? {
        recordedFollowRequest
    }

    func lastViewRequest() -> (postID: UUID, request: MobileRecordSocialView)? {
        recordedViewRequest
    }

    func lastUpdateRequest() -> MobileUpdateSocialPost? {
        recordedUpdateRequest
    }

    func lastDeletedPostID() -> UUID? {
        recordedDeletedPostID
    }
}

private func updatedSocialPost(
    _ post: MobileSocialPost,
    body: String
) -> MobileSocialPost {
    MobileSocialPost(
        id: post.id,
        author: post.author,
        contentType: post.contentType,
        body: body,
        audience: MobileSocialAudience.authorizedNetwork.rawValue,
        location: nil,
        commentsEnabled: true,
        postedUTC: post.postedUTC,
        expiresUTC: post.expiresUTC,
        reactionCount: post.reactionCount,
        commentCount: post.commentCount,
        reactedByCurrentActor: post.reactedByCurrentActor,
        followedByCurrentActor: post.followedByCurrentActor,
        savedByCurrentActor: post.savedByCurrentActor,
        repostedByCurrentActor: post.repostedByCurrentActor,
        metrics: post.metrics,
        music: post.music,
        media: post.media,
        comments: post.comments)
}

private func socialPost(
    author: MobileSocialAuthor,
    contentType: MobileSocialContentType,
    postedUTC: Date
) -> MobileSocialPost {
    MobileSocialPost(
        id: UUID(),
        author: author,
        contentType: contentType.rawValue,
        body: "A focused Legend update.",
        audience: MobileSocialAudience.authorizedNetwork.rawValue,
        location: nil,
        commentsEnabled: true,
        postedUTC: postedUTC,
        expiresUTC: contentType == .story ? postedUTC.addingTimeInterval(86_400) : nil,
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
