import Foundation

protocol MobileSocialAPI: Sendable {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot
    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost]
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost

    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        accessToken: String
    ) async throws -> MobileSocialPost
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost
    func deletePost(postID: UUID, accessToken: String) async throws
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack]
}

struct MobileUnavailableSocialAPI: MobileSocialAPI {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot { throw MobileAPIError.unauthorized(correlationID: nil) }
    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] { throw MobileAPIError.unauthorized(correlationID: nil) }
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        accessToken: String
    ) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
    func deletePost(postID: UUID, accessToken: String) async throws { throw MobileAPIError.unauthorized(correlationID: nil) }
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment { throw MobileAPIError.unauthorized(correlationID: nil) }
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult { throw MobileAPIError.unauthorized(correlationID: nil) }
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { throw MobileAPIError.unauthorized(correlationID: nil) }
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { throw MobileAPIError.unauthorized(correlationID: nil) }
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { throw MobileAPIError.unauthorized(correlationID: nil) }
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics { throw MobileAPIError.unauthorized(correlationID: nil) }
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight { throw MobileAPIError.unauthorized(correlationID: nil) }
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] { throw MobileAPIError.unauthorized(correlationID: nil) }
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

    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] {
        try await client.get(
            "/api/v1/mobile/social/profile/posts",
            accessToken: accessToken,
            headers: participantHeader,
            response: [MobileSocialPost].self)
    }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost {
        try await client.post("/api/v1/mobile/social/posts", body: request, accessToken: accessToken, headers: participantHeader, response: MobileSocialPost.self)
    }

    func updatePost(
        postID: UUID,
        request: MobileUpdateSocialPost,
        accessToken: String
    ) async throws -> MobileSocialPost {
        try await client.put(
            "/api/v1/mobile/social/posts/\(postID.uuidString)",
            body: request,
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileSocialPost.self)
    }

    func deletePost(postID: UUID, accessToken: String) async throws {
        try await client.delete(
            "/api/v1/mobile/social/posts/\(postID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        accessToken: String
    ) async throws -> MobileSocialPost {

        var fields = [
            "contentType": type.rawValue,
            "body": body,
            "accessibilityText": accessibilityText ?? ""
        ]
        if let music {
            fields["musicProviderId"] = music.providerID
            fields["musicTrackId"] = music.providerTrackID
            fields["musicTrimStartSeconds"] = NSDecimalNumber(decimal: music.trimStartSeconds).stringValue
            fields["musicTrimEndSeconds"] = NSDecimalNumber(decimal: music.trimEndSeconds).stringValue
            fields["musicVolume"] = NSDecimalNumber(decimal: music.musicVolume).stringValue
            fields["originalAudioVolume"] = NSDecimalNumber(decimal: music.originalAudioVolume).stringValue
        }

        return try await client.postMultipart(
            "/api/v1/mobile/social/posts/media",
            accessToken: accessToken,
            fields: fields,
            files: files,
            headers: participantHeader,
            response: MobileSocialPost.self
        )
    }

    func mediaData(assetID: UUID, accessToken: String) async throws -> Data {
        try await client.getData(
            "/api/v1/mobile/social/media/\(assetID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader
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

    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/save", body: EmptyMobileRequest(), accessToken: accessToken, headers: participantHeader, response: MobileSocialShareState.self)
    }

    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/repost", body: EmptyMobileRequest(), accessToken: accessToken, headers: participantHeader, response: MobileSocialShareState.self)
    }

    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/share", body: EmptyMobileRequest(), accessToken: accessToken, headers: participantHeader, response: MobileSocialShareState.self)
    }

    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics {
        try await client.post("/api/v1/mobile/social/posts/\(postID.uuidString)/view", body: request, accessToken: accessToken, headers: participantHeader, response: MobileSocialPostMetrics.self)
    }

    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight {
        try await client.get(
            "/api/v1/mobile/social/posts/\(postID.uuidString)/insights",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileSocialPostInsight.self)
    }

    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] {
        try await client.get(
            "/api/v1/mobile/social/music/search",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "query", value: query)],
            headers: participantHeader,
            response: [MobileSocialMusicTrack].self)
    }
}

private struct EmptyMobileRequest: Codable, Sendable {}

@MainActor
final class MobileSocialStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileSocialSnapshot> = .idle
    @Published private(set) var profileContentState: MobileDataLoadState<[MobileSocialPost]> = .idle
    @Published private(set) var actionFailure: UserFacingFailure?
    @Published private(set) var isRefreshing = false
    @Published private(set) var isRefreshingProfilePosts = false
    @Published private(set) var refreshFailure: UserFacingFailure?
    @Published private(set) var profileRefreshFailure: UserFacingFailure?
    @Published private(set) var publication: MobileSocialPublication?

    private let api: any MobileSocialAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private let mediaCache = NSCache<NSUUID, NSData>()
    private var mediaFileCache: [UUID: URL] = [:]
    private var mediaLoadTasks: [UUID: Task<Data?, Never>] = [:]
    private var pendingPublicationRequest: MobileSocialPublishRequest?
    private var feedLoadTask: Task<MobileStoreLoadResult, Never>?
    private var profilePostsLoadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileSocialAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
        mediaCache.countLimit = 48
        mediaCache.totalCostLimit = 32 * 1_024 * 1_024
    }

    func load() {
        guard feedLoadTask == nil else { return }
        if !hasFeedValue {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadProfilePosts() {
        guard profilePostsLoadTask == nil else { return }
        if !hasProfilePostsValue {
            profileContentState = .loading
        }
        Task { _ = await loadProfilePostsIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        hasFeedValue ? .loaded : await requestFeed(preservingCachedValue: false)
    }

    func refresh() async -> MobileStoreLoadResult {
        await requestFeed(preservingCachedValue: hasFeedValue)
    }

    func loadProfilePostsIfNeeded() async -> MobileStoreLoadResult {
        hasProfilePostsValue ? .loaded : await requestProfilePosts(preservingCachedValue: false)
    }

    func refreshProfilePosts() async -> MobileStoreLoadResult {
        await requestProfilePosts(preservingCachedValue: hasProfilePostsValue)
    }

    @discardableResult
    func publish(_ request: MobileSocialPublishRequest) async -> Bool {
        guard validatePublication(request) else { return false }
        do {
            let post = try await publishToServer(request)
            applyPublishedPost(post)
            return true
        } catch {
            actionFailure = failure(
                for: error,
                title: "Could not share your update")
            return false
        }
    }

    /// Starts an upload without trapping the user in the creation flow.  The
    /// source request remains in this store until the server confirms it or
    /// the user retries a recoverable failure.
    @discardableResult
    func beginPublication(_ request: MobileSocialPublishRequest) -> Bool {
        guard publication == nil, validatePublication(request) else { return false }

        let identifier = UUID()
        pendingPublicationRequest = request
        publication = MobileSocialPublication(
            id: identifier,
            contentType: request.contentType,
            stage: .preparing,
            failureMessage: nil)

        Task { await runPublication(identifier: identifier, request: request) }
        return true
    }

    func retryPublication() {
        guard let publication,
              publication.stage == .failed,
              let request = pendingPublicationRequest else {
            return
        }

        self.publication = MobileSocialPublication(
            id: publication.id,
            contentType: request.contentType,
            stage: .preparing,
            failureMessage: nil)
        Task { await runPublication(identifier: publication.id, request: request) }
    }

    func dismissPublication() {
        guard publication?.stage == .published else { return }
        publication = nil
    }

    @discardableResult
    func updatePost(postID: UUID, body: String) async -> Bool {
        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            let post = try await api.updatePost(
                postID: postID,
                request: MobileUpdateSocialPost(body: body),
                accessToken: token)
            replace(post)
            return true
        } catch {
            actionFailure = failure(for: error, title: "Could not save your update")
            return false
        }
    }

    @discardableResult
    func deletePost(postID: UUID) async -> Bool {
        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            try await api.deletePost(postID: postID, accessToken: token)
            remove(postID)
            return true
        } catch {
            actionFailure = failure(for: error, title: "Could not delete your update")
            return false
        }
    }

    func mediaData(for assetID: UUID, forceRefresh: Bool = false) async -> Data? {
        if forceRefresh {
            mediaCache.removeObject(forKey: assetID as NSUUID)
        } else if let cached = mediaCache.object(forKey: assetID as NSUUID) {
            return cached as Data
        }

        if let existingTask = mediaLoadTasks[assetID] {
            return await existingTask.value
        }

        let task = Task { [weak self] () -> Data? in
            guard let self else { return nil }
            do {
                let token = try await self.accessTokenProvider()
                let data = try await self.api.mediaData(
                    assetID: assetID,
                    accessToken: token)
                self.mediaCache.setObject(
                    data as NSData,
                    forKey: assetID as NSUUID,
                    cost: data.count)
                return data
            } catch {
                let apiError = error as? MobileAPIError
                self.diagnostics.record(
                    category: .networking,
                    summary: "A protected social image could not be loaded.",
                    correlationID: apiError?.correlationID)
                return nil
            }
        }
        mediaLoadTasks[assetID] = task
        let data = await task.value
        mediaLoadTasks.removeValue(forKey: assetID)
        return data
    }

    func mediaFile(for media: MobileSocialMedia) async -> URL? {
        if let cached = mediaFileCache[media.id],
           FileManager.default.fileExists(atPath: cached.path) {
            return cached
        }

        guard let data = await mediaData(for: media.id) else { return nil }
        let fileExtension = media.mimeType.split(separator: "/").last
            .map(String.init) ?? "media"
        let fileURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-social-\(media.id.uuidString)")
            .appendingPathExtension(fileExtension)
        do {
            try data.write(to: fileURL, options: .atomic)
            mediaFileCache[media.id] = fileURL
            return fileURL
        } catch {
            diagnostics.record(
                category: .networking,
                summary: "A protected social video could not be prepared.",
                correlationID: nil)
            return nil
        }
    }


    func toggleReaction(postID: UUID) {
        perform(title: "Could not update appreciation") { token in
            let post = try await self.api.toggleReaction(postID: postID, accessToken: token)
            self.replace(post)
        }
    }

    func addComment(postID: UUID, body: String, parentCommentID: UUID? = nil) {
        perform(title: "Could not add comment") { token in
            let comment = try await self.api.addComment(postID: postID, request: MobileCreateSocialComment(body: body, parentCommentID: parentCommentID), accessToken: token)
            self.append(comment, to: postID)
        }
    }

    func toggleSave(postID: UUID) {
        perform(title: "Could not update saved status") { token in
            _ = try await self.api.toggleSave(postID: postID, accessToken: token)
            _ = await self.refresh()
        }
    }

    func toggleRepost(postID: UUID) {
        perform(title: "Could not update repost") { token in
            _ = try await self.api.toggleRepost(postID: postID, accessToken: token)
            _ = await self.refresh()
        }
    }

    func recordShare(postID: UUID) {
        perform(title: "Could not record share") { token in
            _ = try await self.api.recordShare(postID: postID, accessToken: token)
            _ = await self.refresh()
        }
    }

    func recordView(
        postID: UUID,
        watchDurationSeconds: Decimal? = nil,
        watchCompletionPercentage: Decimal? = nil,
        storyInteractionType: String? = nil
    ) {
        Task {
            do {
                let token = try await accessTokenProvider()
                _ = try await api.recordView(
                    postID: postID,
                    request: MobileRecordSocialView(
                        watchDurationSeconds: watchDurationSeconds,
                        watchCompletionPercentage: watchCompletionPercentage,
                        storyInteractionType: storyInteractionType),
                    accessToken: token)
            } catch {
                let apiError = error as? MobileAPIError
                diagnostics.record(
                    category: .networking,
                    summary: "A social view could not be recorded.",
                    correlationID: apiError?.correlationID)
            }
        }
    }

    func searchMusic(_ query: String) async -> [MobileSocialMusicTrack] {
        let normalized = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return [] }

        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            return try await api.searchMusic(query: normalized, accessToken: token)
        } catch {
            actionFailure = failure(for: error, title: "Music search unavailable")
            return []
        }
    }

    func postInsights(postID: UUID) async -> MobileSocialPostInsight? {
        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            return try await api.postInsights(postID: postID, accessToken: token)
        } catch {
            actionFailure = failure(for: error, title: "Post insights unavailable")
            return nil
        }
    }

    func toggleFollow(author: MobileSocialAuthor, sourcePostID: UUID?) {
        perform(title: "Could not update your connection") { token in
            let result = try await self.api.toggleFollow(
                MobileToggleSocialFollow(
                    followedUserID: author.identity.userID,
                    followedParticipantType: author.identity.participantType,
                    sourcePostID: sourcePostID),
                accessToken: token)
            self.updateFollow(author: author, isFollowing: result.isFollowing)
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    private func validatePublication(_ request: MobileSocialPublishRequest) -> Bool {
        let body = request.body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty || !request.files.isEmpty else {
            actionFailure = UserFacingFailure(
                title: "Could not share your update",
                message: "Add an update or attach supported media before publishing.",
                correlationID: nil)
            return false
        }

        actionFailure = nil
        return true
    }

    private func publishToServer(
        _ request: MobileSocialPublishRequest
    ) async throws -> MobileSocialPost {
        let body = request.body.trimmingCharacters(in: .whitespacesAndNewlines)
        let token = try await accessTokenProvider()

        if request.files.isEmpty {
            return try await api.createPost(
                MobileCreateSocialPost(
                    contentType: request.contentType.rawValue,
                    body: body),
                accessToken: token)
        }

        return try await api.createMediaPost(
            type: request.contentType,
            body: body,
            files: request.files,
            accessibilityText: request.accessibilityText,
            music: request.music,
            accessToken: token)
    }

    private func runPublication(
        identifier: UUID,
        request: MobileSocialPublishRequest
    ) async {
        guard publication?.id == identifier else { return }
        publication?.stage = request.files.isEmpty ? .processing : .uploading

        do {
            let post = try await publishToServer(request)
            guard publication?.id == identifier else { return }
            applyPublishedPost(post)
            discardStagedFiles(in: request)
            pendingPublicationRequest = nil
            publication?.stage = .published
        } catch {
            guard publication?.id == identifier else { return }
            let presentation = failure(
                for: error,
                title: "Could not share your update")
            actionFailure = presentation
            publication?.stage = .failed
            publication?.failureMessage = presentation.message
        }
    }

    private func applyPublishedPost(_ post: MobileSocialPost) {
        if hasFeedValue {
            insert(post)
        } else {
            Task { _ = await refresh() }
        }
    }

    private func discardStagedFiles(in request: MobileSocialPublishRequest) {
        for file in request.files {
            guard case let .file(url) = file.source else { continue }
            try? FileManager.default.removeItem(at: url)
        }
    }

    private var hasFeedValue: Bool {
        if case .loaded = state { return true }
        return false
    }

    private var hasProfilePostsValue: Bool {
        if case .loaded = profileContentState { return true }
        return false
    }

    private func requestFeed(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        if let feedLoadTask {
            return await feedLoadTask.value
        }

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Legend feed unavailable",
                    message: "The social store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeFeedRequest(preservingCachedValue: preservingCachedValue)
        }
        feedLoadTask = task
        let result = await task.value
        feedLoadTask = nil
        return result
    }

    private func requestProfilePosts(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        if let profilePostsLoadTask {
            return await profilePostsLoadTask.value
        }

        if preservingCachedValue {
            isRefreshingProfilePosts = true
            profileRefreshFailure = nil
        } else {
            profileContentState = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Profile updates unavailable",
                    message: "The social store is no longer available.",
                    correlationID: nil))
            }
            return await self.executeProfilePostsRequest(preservingCachedValue: preservingCachedValue)
        }
        profilePostsLoadTask = task
        let result = await task.value
        profilePostsLoadTask = nil
        return result
    }

    private func executeFeedRequest(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let token = try await accessTokenProvider()
            state = .loaded(try await api.feed(accessToken: token))
            refreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, title: "Legend feed unavailable")
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func executeProfilePostsRequest(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        defer { isRefreshingProfilePosts = false }
        do {
            let token = try await accessTokenProvider()
            profileContentState = .loaded(try await api.currentProfilePosts(accessToken: token))
            profileRefreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, title: "Profile updates unavailable")
            if preservingCachedValue {
                profileRefreshFailure = presentation
            } else {
                profileContentState = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
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
        guard case .loaded(var snapshot) = state else { return }
        if post.contentType == MobileSocialContentType.story.rawValue {
            snapshot = MobileSocialSnapshot(stories: [post] + snapshot.stories, posts: snapshot.posts, activity: snapshot.activity, activityCount: snapshot.activityCount, currentProfileMetrics: snapshot.currentProfileMetrics, creatorInsights: snapshot.creatorInsights)
        } else {
            snapshot = MobileSocialSnapshot(stories: snapshot.stories, posts: [post] + snapshot.posts, activity: snapshot.activity, activityCount: snapshot.activityCount, currentProfileMetrics: snapshot.currentProfileMetrics, creatorInsights: snapshot.creatorInsights)
        }
        state = .loaded(snapshot)
        insertProfilePost(post)
    }

    private func replace(_ post: MobileSocialPost) {
        if case .loaded(let snapshot) = state {
            let stories = snapshot.stories.map { $0.id == post.id ? post : $0 }
            let posts = snapshot.posts.map { $0.id == post.id ? post : $0 }
            state = .loaded(MobileSocialSnapshot(stories: stories, posts: posts, activity: snapshot.activity, activityCount: snapshot.activityCount, currentProfileMetrics: snapshot.currentProfileMetrics, creatorInsights: snapshot.creatorInsights))
        }
        replaceProfilePost(post)
    }

    private func remove(_ postID: UUID) {
        if case .loaded(let snapshot) = state {
            state = .loaded(MobileSocialSnapshot(
                stories: snapshot.stories.filter { $0.id != postID },
                posts: snapshot.posts.filter { $0.id != postID },
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: snapshot.currentProfileMetrics,
                creatorInsights: snapshot.creatorInsights))
        }

        if case .loaded(let posts) = profileContentState {
            profileContentState = .loaded(posts.filter { $0.id != postID })
        }
    }

    private func insertProfilePost(_ post: MobileSocialPost) {
        guard case .loaded(let posts) = profileContentState else { return }
        profileContentState = .loaded(
            ([post] + posts.filter { $0.id != post.id })
                .sorted { $0.postedUTC > $1.postedUTC })
    }

    private func replaceProfilePost(_ post: MobileSocialPost) {
        guard case .loaded(let posts) = profileContentState else { return }
        profileContentState = .loaded(posts.map { $0.id == post.id ? post : $0 })
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
                savedByCurrentActor: post.savedByCurrentActor,
                repostedByCurrentActor: post.repostedByCurrentActor,
                metrics: post.metrics,
                music: post.music,
                media: post.media,
                comments: Array((post.comments + [comment]).suffix(4)))
        }
        state = .loaded(MobileSocialSnapshot(
            stories: snapshot.stories.map(update),
            posts: snapshot.posts.map(update),
            activity: snapshot.activity,
            activityCount: snapshot.activityCount,
            currentProfileMetrics: snapshot.currentProfileMetrics,
            creatorInsights: snapshot.creatorInsights))
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
                savedByCurrentActor: post.savedByCurrentActor,
                repostedByCurrentActor: post.repostedByCurrentActor,
                metrics: post.metrics,
                music: post.music,
                media: post.media,
                comments: post.comments)
        }
        state = .loaded(MobileSocialSnapshot(
            stories: snapshot.stories.map(update),
            posts: snapshot.posts.map(update),
            activity: snapshot.activity,
            activityCount: snapshot.activityCount,
            currentProfileMetrics: snapshot.currentProfileMetrics,
            creatorInsights: snapshot.creatorInsights))
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(category: .networking, summary: "A native social request could not be completed.", correlationID: apiError?.correlationID)
        return UserFacingFailure(title: title, message: error.localizedDescription, correlationID: apiError?.correlationID)
    }
}
