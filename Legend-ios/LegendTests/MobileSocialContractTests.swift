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
              "comments": []
            }
          ],
          "activity": [],
          "activityCount": 1
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
        XCTAssertTrue(client.followedByCurrentActor)
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
            comments: [])
        let store = MobileSocialStore(
            api: StubSocialAPI(post: post),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.load()
        try await Task.sleep(for: .milliseconds(50))
        store.toggleFollow(author: author)
        try await Task.sleep(for: .milliseconds(50))

        guard case .loaded(let snapshot) = store.state else {
            return XCTFail("Expected social store to load")
        }
        XCTAssertTrue(snapshot.posts[0].followedByCurrentActor)
    }
}

private struct StubSocialAPI: MobileSocialAPI {
    let post: MobileSocialPost

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        MobileSocialSnapshot(stories: [], posts: [post], activity: [], activityCount: 0)
    }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { post }
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost { post }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment {
        MobileSocialComment(id: UUID(), author: post.author, body: request.body, createdUTC: .now)
    }
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult {
        MobileSocialFollowResult(isFollowing: true)
    }
}
