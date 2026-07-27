import Foundation

protocol MobileSocialAPI: Sendable {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost

    func createMediaPost(
        body: String,
        files: [MultipartFormFile],
        accessToken: String
    ) async throws -> MobileSocialPost
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult
}

struct MobileUnavailableSocialAPI: MobileSocialAPI {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot { throw MobileAPIError.unauthorized(correlationID: nil) }
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
    func createMediaPost(
        body: String,
        files: [MultipartFormFile],
        accessToken: String
    ) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment { throw MobileAPIError.unauthorized(correlationID: nil) }
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult { throw MobileAPIError.unauthorized(correlationID: nil) }
}

struct URLSessionMobileSocialAPI: MobileSocialAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        try await client.get("/api/v1/mobile/social/feed", accessToken: accessToken, headers: participantHeader, response: MobileSocialSnapshot.self)
    }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost {
        try await client.post("/api/v1/mobile/social/posts", body: request, accessToken: accessToken, headers: participantHeader, response: MobileSocialPost.self)
    }

    func createMediaPost(
        body: String,
        files: [MultipartFormFile],
        accessToken: String
    ) async throws -> MobileSocialPost {

        try await client.postMultipart(
            "/api/v1/mobile/social/posts/media",
            accessToken: accessToken,
            fields: [
                "body": body
            ],
            files: files,
            headers: participantHeader,
            response: MobileSocialPost.self
        )
    }


    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/reaction", body: EmptyMobileRequest(), accessToken: accessToken, headers: participantHeader, response: MobileSocialPost.self)
    }

    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/comments", body: request, accessToken: accessToken, headers: participantHeader, response: MobileSocialComment.self)
    }

    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult {
        try await client.post("/api/v1/mobile/social/follows/toggle", body: request, accessToken: accessToken, headers: participantHeader, response: MobileSocialFollowResult.self)
    }
}

private struct EmptyMobileRequest: Codable, Sendable {}

@MainActor
final class MobileSocialStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileSocialSnapshot> = .idle
    @Published private(set) var actionFailure: UserFacingFailure?

    private let api: any MobileSocialAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    init(
        api: any MobileSocialAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() {
        state = .loading
        Task {
            do {
                let token = try await accessTokenProvider()
                state = .loaded(try await api.feed(accessToken: token))
            } catch {
                state = .unavailable(failure(for: error, title: "Legend feed unavailable"))
            }
        }
    }

    func createPost(type: MobileSocialContentType, body: String) {
        perform(title: "Could not share your update") { token in
            let post = try await self.api.createPost(MobileCreateSocialPost(contentType: type.rawValue, body: body), accessToken: token)
            self.insert(post)
        }
    }

    func createMediaPost(
        body: String,
        files: [MultipartFormFile]
    ) {
        perform(title: "Could not share your update") { token in
            let post = try await self.api.createMediaPost(
                body: body,
                files: files,
                accessToken: token
            )
            self.insert(post)
        }
    }


    func toggleReaction(postID: UUID) {
        perform(title: "Could not update appreciation") { token in
            let post = try await self.api.toggleReaction(postID: postID, accessToken: token)
            self.replace(post)
        }
    }

    func addComment(postID: UUID, body: String) {
        perform(title: "Could not add comment") { token in
            let comment = try await self.api.addComment(postID: postID, request: MobileCreateSocialComment(body: body), accessToken: token)
            self.append(comment, to: postID)
        }
    }

    func toggleFollow(author: MobileSocialAuthor) {
        perform(title: "Could not update your connection") { token in
            let result = try await self.api.toggleFollow(
                MobileToggleSocialFollow(
                    followedUserID: author.identity.userID,
                    followedParticipantType: author.identity.participantType),
                accessToken: token)
            self.updateFollow(author: author, isFollowing: result.isFollowing)
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    private func perform(title: String, work: @escaping @MainActor (String) async throws -> Void) {
        actionFailure = nil
        Task {
            do {
                let token = try await accessTokenProvider()
                try await work(token)
            } catch {
                actionFailure = failure(for: error, title: title)
            }
        }
    }

    private func insert(_ post: MobileSocialPost) {
        guard case .loaded(var snapshot) = state else {
            load()
            return
        }
        if post.contentType == MobileSocialContentType.story.rawValue {
            snapshot = MobileSocialSnapshot(stories: [post] + snapshot.stories, posts: snapshot.posts, activity: snapshot.activity, activityCount: snapshot.activityCount)
        } else {
            snapshot = MobileSocialSnapshot(stories: snapshot.stories, posts: [post] + snapshot.posts, activity: snapshot.activity, activityCount: snapshot.activityCount)
        }
        state = .loaded(snapshot)
    }

    private func replace(_ post: MobileSocialPost) {
        guard case .loaded(let snapshot) = state else { return }
        let stories = snapshot.stories.map { $0.id == post.id ? post : $0 }
        let posts = snapshot.posts.map { $0.id == post.id ? post : $0 }
        state = .loaded(MobileSocialSnapshot(stories: stories, posts: posts, activity: snapshot.activity, activityCount: snapshot.activityCount))
    }

    private func append(_ comment: MobileSocialComment, to postID: UUID) {
        guard case .loaded(let snapshot) = state else { return }
        let update: (MobileSocialPost) -> MobileSocialPost = { post in
            guard post.id == postID else { return post }
            return MobileSocialPost(
                id: post.id,
                author: post.author,
                contentType: post.contentType,
                body: post.body,
                postedUTC: post.postedUTC,
                expiresUTC: post.expiresUTC,
                reactionCount: post.reactionCount,
                commentCount: post.commentCount + 1,
                reactedByCurrentActor: post.reactedByCurrentActor,
                followedByCurrentActor: post.followedByCurrentActor,
                comments: Array((post.comments + [comment]).suffix(4)))
        }
        state = .loaded(MobileSocialSnapshot(
            stories: snapshot.stories.map(update),
            posts: snapshot.posts.map(update),
            activity: snapshot.activity,
            activityCount: snapshot.activityCount))
    }

    private func updateFollow(author: MobileSocialAuthor, isFollowing: Bool) {
        guard case .loaded(let snapshot) = state else { return }
        let update: (MobileSocialPost) -> MobileSocialPost = { post in
            guard post.author.identity == author.identity else { return post }
            return MobileSocialPost(
                id: post.id,
                author: post.author,
                contentType: post.contentType,
                body: post.body,
                postedUTC: post.postedUTC,
                expiresUTC: post.expiresUTC,
                reactionCount: post.reactionCount,
                commentCount: post.commentCount,
                reactedByCurrentActor: post.reactedByCurrentActor,
                followedByCurrentActor: isFollowing,
                comments: post.comments)
        }
        state = .loaded(MobileSocialSnapshot(
            stories: snapshot.stories.map(update),
            posts: snapshot.posts.map(update),
            activity: snapshot.activity,
            activityCount: snapshot.activityCount))
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(category: .networking, summary: "A native social request could not be completed.", correlationID: apiError?.correlationID)
        return UserFacingFailure(title: title, message: error.localizedDescription, correlationID: apiError?.correlationID)
    }
}
