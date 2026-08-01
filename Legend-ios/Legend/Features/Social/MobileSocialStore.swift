import Foundation

protocol MobileSocialAPI: Sendable {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot
    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost]
    func publicProfilePosts(for profile: MobileSocialAuthor, accessToken: String) async throws -> [MobileSocialPost]
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost

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
    ) async throws -> MobileSocialPost
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost
    func deletePost(postID: UUID, accessToken: String) async throws
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult
    func currentProfileFollowList(kind: MobileSocialFollowListKind, accessToken: String) async throws -> [MobileSocialFollowListEntry]
    func incomingFollowRequests(accessToken: String) async throws -> [MobileSocialFollowRequest]
    func decideFollowRequest(id: UUID, approve: Bool, accessToken: String) async throws -> MobileSocialFollowResult
    func profileMetrics(for profile: MobileSocialAuthor, accessToken: String) async throws -> MobileSocialProfileMetrics
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack]
}

extension MobileSocialAPI {
    /// Optional for API doubles that do not exercise profile relationships. The
    /// production client below provides the real server-backed implementation.
    func currentProfileFollowList(
        kind: MobileSocialFollowListKind,
        accessToken: String
    ) async throws -> [MobileSocialFollowListEntry] {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func profileMetrics(
        for profile: MobileSocialAuthor,
        accessToken: String
    ) async throws -> MobileSocialProfileMetrics {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func publicProfilePosts(
        for profile: MobileSocialAuthor,
        accessToken: String
    ) async throws -> [MobileSocialPost] {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func incomingFollowRequests(accessToken: String) async throws -> [MobileSocialFollowRequest] {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func decideFollowRequest(id: UUID, approve: Bool, accessToken: String) async throws -> MobileSocialFollowResult {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableSocialAPI: MobileSocialAPI {
    func feed(accessToken: String) async throws -> MobileSocialSnapshot { throw MobileAPIError.unauthorized(correlationID: nil) }
    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] { throw MobileAPIError.unauthorized(correlationID: nil) }
    func publicProfilePosts(for profile: MobileSocialAuthor, accessToken: String) async throws -> [MobileSocialPost] { throw MobileAPIError.unauthorized(correlationID: nil) }
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { throw MobileAPIError.unauthorized(correlationID: nil) }
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
    func currentProfileFollowList(kind: MobileSocialFollowListKind, accessToken: String) async throws -> [MobileSocialFollowListEntry] { throw MobileAPIError.unauthorized(correlationID: nil) }
    func incomingFollowRequests(accessToken: String) async throws -> [MobileSocialFollowRequest] { throw MobileAPIError.unauthorized(correlationID: nil) }
    func decideFollowRequest(id: UUID, approve: Bool, accessToken: String) async throws -> MobileSocialFollowResult { throw MobileAPIError.unauthorized(correlationID: nil) }
    func profileMetrics(for profile: MobileSocialAuthor, accessToken: String) async throws -> MobileSocialProfileMetrics { throw MobileAPIError.unauthorized(correlationID: nil) }
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

    func publicProfilePosts(
        for profile: MobileSocialAuthor,
        accessToken: String
    ) async throws -> [MobileSocialPost] {
        try await client.get(
            "/api/v1/mobile/social/profiles/posts",
            accessToken: accessToken,
            queryItems: [
                URLQueryItem(name: "userId", value: profile.identity.userID),
                URLQueryItem(name: "participantType", value: profile.identity.participantType.rawValue),
                URLQueryItem(name: "profileId", value: profile.profileID)
            ],
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
        audience: MobileSocialAudience,
        location: String?,
        commentsEnabled: Bool,
        uploadProgress: @escaping @Sendable (Double) -> Void,
        accessToken: String
    ) async throws -> MobileSocialPost {

        var fields = [
            "contentType": type.rawValue,
            "body": body,
            "accessibilityText": accessibilityText ?? "",
            "audience": audience.rawValue,
            "location": location ?? "",
            "commentsEnabled": commentsEnabled ? "true" : "false"
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
            uploadProgress: uploadProgress,
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

    func currentProfileFollowList(
        kind: MobileSocialFollowListKind,
        accessToken: String
    ) async throws -> [MobileSocialFollowListEntry] {
        try await client.get(
            "/api/v1/mobile/social/profile/follows",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "list", value: kind.rawValue)],
            headers: participantHeader,
            response: [MobileSocialFollowListEntry].self)
    }

    func incomingFollowRequests(accessToken: String) async throws -> [MobileSocialFollowRequest] {
        try await client.get(
            "/api/v1/mobile/social/profile/follow-requests",
            accessToken: accessToken,
            headers: participantHeader,
            response: [MobileSocialFollowRequest].self)
    }

    func decideFollowRequest(id: UUID, approve: Bool, accessToken: String) async throws -> MobileSocialFollowResult {
        try await client.post(
            "/api/v1/mobile/social/profile/follow-requests/\(id.uuidString)/decision",
            body: MobileSocialFollowRequestDecision(approve: approve),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileSocialFollowResult.self)
    }

    func profileMetrics(
        for profile: MobileSocialAuthor,
        accessToken: String
    ) async throws -> MobileSocialProfileMetrics {
        try await client.get(
            "/api/v1/mobile/social/profiles/metrics",
            accessToken: accessToken,
            queryItems: [
                URLQueryItem(name: "userId", value: profile.identity.userID),
                URLQueryItem(name: "participantType", value: profile.identity.participantType.rawValue),
                URLQueryItem(name: "profileId", value: profile.profileID)
            ],
            headers: participantHeader,
            response: MobileSocialProfileMetrics.self)
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
    private struct CachedMediaFile {
        let url: URL
        let byteCount: Int
        var lastAccessed: Date
    }

    /// Materialized protected videos are short-lived playback aids, not a second
    /// offline media library. Keep their disk footprint bounded independently of
    /// the in-memory response cache below.
    private static let maximumCachedMediaFileCount = 8
    private static let maximumCachedMediaFileBytes = 128 * 1_024 * 1_024

    @Published private(set) var state: MobileDataLoadState<MobileSocialSnapshot> = .idle
    @Published private(set) var profileContentState: MobileDataLoadState<[MobileSocialPost]> = .idle
    @Published private(set) var actionFailure: UserFacingFailure?
    @Published private(set) var isRefreshing = false
    @Published private(set) var isRefreshingProfilePosts = false
    @Published private(set) var refreshFailure: UserFacingFailure?
    @Published private(set) var profileRefreshFailure: UserFacingFailure?
    @Published private(set) var publication: MobileSocialPublication?
    @Published private(set) var mediaFailures: [UUID: UserFacingFailure] = [:]

    private let api: any MobileSocialAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private let persistence: LegendStorePersistence<MobileSocialSnapshot>
    private let mediaCache = NSCache<NSUUID, NSData>()
    private var mediaFileCache: [UUID: CachedMediaFile] = [:]
    private var mediaLoadTasks: [UUID: Task<Data?, Never>] = [:]
    private var inFlightMutationKeys: Set<String> = []
    private var pendingPublicationRequest: MobileSocialPublishRequest?
    private var feedLoadTask: Task<MobileStoreLoadResult, Never>?
    private var profilePostsLoadTask: Task<MobileStoreLoadResult, Never>?

    init(
        api: any MobileSocialAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics,
        persistence: LegendStorePersistence<MobileSocialSnapshot> = .none()
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
        self.persistence = persistence
        mediaCache.countLimit = 48
        mediaCache.totalCostLimit = 32 * 1_024 * 1_024

        // The feed shows last-known posts immediately rather than a spinner, then
        // refreshes underneath.
        if let cached = persistence.read() {
            state = .loaded(cached)
        }
    }

    deinit {
        mediaLoadTasks.values.forEach { $0.cancel() }
        for cached in mediaFileCache.values {
            try? FileManager.default.removeItem(at: cached.url)
        }
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
        await requestFeed(preservingCachedValue: hasFeedValue)
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
        // A previously failed publication must not block a new one. Replace it.
        if publication?.stage == .failed {
            discardPendingPublication()
        }

        guard publication == nil, validatePublication(request) else { return false }

        let identifier = UUID()
        pendingPublicationRequest = request
        publication = MobileSocialPublication(
            id: identifier,
            contentType: request.contentType,
            stage: .preparing,
            uploadProgress: 0,
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
            uploadProgress: 0,
            failureMessage: nil)
        Task { await runPublication(identifier: publication.id, request: request) }
    }

    /// Clears a finished publication. A failed upload is also finished: leaving it
    /// parked indefinitely used to block `beginPublication` and disable the composer's
    /// Next and Share controls for the rest of the session with no way back.
    func dismissPublication() {
        switch publication?.stage {
        case .published:
            publication = nil
        case .failed:
            discardPendingPublication()
        default:
            break
        }
    }

    /// Abandons a failed upload and releases its staged files.
    func discardPendingPublication() {
        if let request = pendingPublicationRequest {
            discardStagedFiles(in: request)
        }
        pendingPublicationRequest = nil
        publication = nil
        actionFailure = nil
    }

    /// True when a publication currently owns the upload slot. A failed publication
    /// does not: the user can discard it and start a new one.
    var isPublishing: Bool {
        guard let publication else { return false }
        return publication.stage != .failed
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
            removeCachedMediaFile(for: assetID)
            mediaFailures.removeValue(forKey: assetID)
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
                self.mediaFailures.removeValue(forKey: assetID)
                return data
            } catch {
                self.mediaFailures[assetID] = self.mediaFailurePresentation(
                    for: error)
                return nil
            }
        }
        mediaLoadTasks[assetID] = task
        let data = await task.value
        mediaLoadTasks.removeValue(forKey: assetID)
        return data
    }

    func mediaFile(for media: MobileSocialMedia) async -> URL? {
        if var cached = mediaFileCache[media.id],
           FileManager.default.fileExists(atPath: cached.url.path) {
            cached.lastAccessed = .now
            mediaFileCache[media.id] = cached
            return cached.url
        }

        mediaFileCache.removeValue(forKey: media.id)

        guard let data = await mediaData(for: media.id) else { return nil }
        let fileExtension = media.mimeType.split(separator: "/").last
            .map(String.init) ?? "media"
        let fileURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-social-\(media.id.uuidString)")
            .appendingPathExtension(fileExtension)
        do {
            try data.write(to: fileURL, options: .atomic)
            mediaFileCache[media.id] = CachedMediaFile(
                url: fileURL,
                byteCount: data.count,
                lastAccessed: .now)
            trimMediaFileCache(keeping: media.id)
            return fileURL
        } catch {
            diagnostics.record(
                category: .networking,
                summary: "A protected social video could not be prepared.",
                correlationID: nil)
            mediaFailures[media.id] = UserFacingFailure(
                title: "Media temporarily unavailable",
                message: "The protected file could not be prepared. Please try again.",
                correlationID: nil)
            return nil
        }
    }

    func mediaFailure(for assetID: UUID) -> UserFacingFailure? {
        mediaFailures[assetID]
    }

    func recordUnreadableImage(assetID: UUID) {
        mediaFailures[assetID] = UserFacingFailure(
            title: "Media unavailable",
            message: "This file is not a supported image. Please try again or ask the author to replace it.",
            correlationID: nil)
    }


    func toggleReaction(postID: UUID) {
        perform(key: "reaction:\(postID.uuidString)", title: "Could not update appreciation") { token in
            let post = try await self.api.toggleReaction(postID: postID, accessToken: token)
            self.replace(post)
        }
    }

    func addComment(postID: UUID, body: String, parentCommentID: UUID? = nil) {
        let normalizedBody = body.trimmingCharacters(in: .whitespacesAndNewlines)
        let parentKey = parentCommentID?.uuidString ?? "root"
        perform(
            key: "comment:\(postID.uuidString):\(parentKey):\(normalizedBody)",
            title: "Could not add comment") { token in
            let comment = try await self.api.addComment(postID: postID, request: MobileCreateSocialComment(body: body, parentCommentID: parentCommentID), accessToken: token)
            self.append(comment, to: postID)
        }
    }

    func toggleSave(postID: UUID) {
        perform(key: "save:\(postID.uuidString)", title: "Could not update saved status") { token in
            let state = try await self.api.toggleSave(postID: postID, accessToken: token)
            self.mutate(postID: postID) { post in
                let delta = state.isActive == post.savedByCurrentActor
                    ? 0
                    : state.isActive ? 1 : -1
                return post.replacing(
                    savedByCurrentActor: state.isActive,
                    metrics: post.metrics.adjusting(saveCountBy: delta))
            }
        }
    }

    func toggleRepost(postID: UUID) {
        perform(key: "repost:\(postID.uuidString)", title: "Could not update repost") { token in
            let state = try await self.api.toggleRepost(postID: postID, accessToken: token)
            self.mutate(postID: postID) { post in
                let delta = state.isActive == post.repostedByCurrentActor
                    ? 0
                    : state.isActive ? 1 : -1
                return post.replacing(
                    repostedByCurrentActor: state.isActive,
                    metrics: post.metrics.adjusting(repostCountBy: delta))
            }
        }
    }

    func recordShare(postID: UUID) {
        perform(key: "share:\(postID.uuidString)", title: "Could not record share") { token in
            _ = try await self.api.recordShare(postID: postID, accessToken: token)
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
        let wasFollowing = follows(author)
        perform(
            key: followMutationKey(
                userID: author.identity.userID,
                participantType: author.identity.participantType),
            title: "Could not update your connection") { token in
            let result = try await self.api.toggleFollow(
                MobileToggleSocialFollow(
                    followedUserID: author.identity.userID,
                    followedParticipantType: author.identity.participantType,
                    sourcePostID: sourcePostID),
                accessToken: token)
            self.updateFollow(
                author: author,
                isFollowing: result.isFollowing,
                isPending: result.hasPendingRequest)
            guard result.isFollowing != wasFollowing else { return }
            self.adjustCurrentProfileMetrics(
                followingCountBy: result.isFollowing ? 1 : -1
            )
        }
    }

    /// Follows, requests, or removes a relationship for a participant who may
    /// not be in the loaded feed. The server response is the only source of
    /// truth for accepted versus pending state.
    ///
    /// Discover needs this because a discovered member usually has no post in the
    /// current feed, so the feed-driven `toggleFollow` has nothing to act on.
    func setFollow(
        userID: String,
        participantType: ParticipantType,
        isFollowing: Bool
    ) async -> MobileSocialFollowResult? {
        let mutationKey = followMutationKey(
            userID: userID,
            participantType: participantType)
        guard inFlightMutationKeys.insert(mutationKey).inserted else {
            return nil
        }
        defer { inFlightMutationKeys.remove(mutationKey) }

        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            let result = try await api.toggleFollow(
                MobileToggleSocialFollow(
                    followedUserID: userID,
                    followedParticipantType: participantType,
                    sourcePostID: nil),
                accessToken: token)

            // The server owns the outcome. If it disagrees with the requested
            // direction, its answer wins.
            transformPosts(
                matching: { $0.author.identity.userID == userID
                    && $0.author.identity.participantType == participantType },
                transform: {
                    $0.replacing(
                        followedByCurrentActor: result.isFollowing,
                        followRequestPending: result.hasPendingRequest)
                })

            let wasFollowing = !isFollowing
            if result.isFollowing != wasFollowing {
                adjustCurrentProfileMetrics(followingCountBy: result.isFollowing ? 1 : -1)
            }
            return result
        } catch {
            actionFailure = failure(for: error, title: "Could not update your connection")
            return nil
        }
    }

    /// Loads one complete, server-authoritative relationship list. The view owns
    /// this short-lived state so opening Follows and Followers in separate
    /// navigation paths can never overwrite one another.
    func followList(
        kind: MobileSocialFollowListKind
    ) async -> MobileDataLoadState<[MobileSocialFollowListEntry]> {
        do {
            let token = try await accessTokenProvider()
            return .loaded(try await api.currentProfileFollowList(
                kind: kind,
                accessToken: token))
        } catch {
            return .unavailable(failure(
                for: error,
                title: "\(kind.title) unavailable"))
        }
    }

    func incomingFollowRequests() async -> MobileDataLoadState<[MobileSocialFollowRequest]> {
        do {
            let token = try await accessTokenProvider()
            return .loaded(try await api.incomingFollowRequests(accessToken: token))
        } catch {
            return .unavailable(failure(for: error, title: "Follow requests unavailable"))
        }
    }

    func decideFollowRequest(id: UUID, approve: Bool) async -> Bool {
        actionFailure = nil
        do {
            let token = try await accessTokenProvider()
            _ = try await api.decideFollowRequest(id: id, approve: approve, accessToken: token)
            if approve {
                adjustCurrentProfileMetrics(followerCountBy: 1)
            }
            return true
        } catch {
            actionFailure = failure(for: error, title: "Could not update this follow request")
            return false
        }
    }

    /// Gets current counts for a profile opened from a relationship list. The
    /// server validates that the member is either authorized in the network or
    /// directly connected by a follow edge.
    func profileMetrics(
        for profile: MobileSocialAuthor
    ) async -> MobileDataLoadState<MobileSocialProfileMetrics> {
        do {
            let token = try await accessTokenProvider()
            return .loaded(try await api.profileMetrics(
                for: profile,
                accessToken: token))
        } catch {
            return .unavailable(failure(
                for: error,
                title: "Profile unavailable"))
        }
    }

    /// Loads the selected member's authorized posts from the same social
    /// authority that owns the feed. Public profiles never synthesize activity
    /// from discovery metadata.
    func publicProfilePosts(
        for profile: MobileSocialAuthor
    ) async -> MobileDataLoadState<[MobileSocialPost]> {
        do {
            let token = try await accessTokenProvider()
            return .loaded(try await api.publicProfilePosts(
                for: profile,
                accessToken: token))
        } catch {
            return .unavailable(failure(
                for: error,
                title: "Profile updates unavailable"))
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

        if request.contentType.requiresVideo,
           (request.files.count != 1 || request.files.contains(where: { !$0.isVideo })) {
            actionFailure = UserFacingFailure(
                title: "Could not share your Hac",
                message: "A Hac requires exactly one video.",
                correlationID: nil)
            return false
        }

        actionFailure = nil
        return true
    }

    private func publishToServer(
        _ request: MobileSocialPublishRequest,
        uploadProgress: @escaping @Sendable (Double) -> Void = { _ in }
    ) async throws -> MobileSocialPost {
        let body = request.body.trimmingCharacters(in: .whitespacesAndNewlines)
        let token = try await accessTokenProvider()

        if request.files.isEmpty {
            return try await api.createPost(
                MobileCreateSocialPost(
                    contentType: request.contentType.rawValue,
                    body: body,
                    audience: request.audience.rawValue,
                    location: request.location,
                    commentsEnabled: request.commentsEnabled),
                accessToken: token)
        }

        return try await api.createMediaPost(
            type: request.contentType,
            body: body,
            files: request.files,
            accessibilityText: request.accessibilityText,
            music: request.music,
            audience: request.audience,
            location: request.location,
            commentsEnabled: request.commentsEnabled,
            uploadProgress: uploadProgress,
            accessToken: token)
    }

    private func runPublication(
        identifier: UUID,
        request: MobileSocialPublishRequest
    ) async {
        guard publication?.id == identifier else { return }
        publication?.stage = request.files.isEmpty ? .processing : .uploading

        do {
            let post = try await publishToServer(request) { [weak self] progress in
                Task { @MainActor [weak self] in
                    self?.updatePublicationProgress(
                        progress,
                        identifier: identifier)
                }
            }
            guard publication?.id == identifier else { return }
            applyPublishedPost(post)
            discardStagedFiles(in: request)
            pendingPublicationRequest = nil
            publication?.stage = .published
            publication?.uploadProgress = 1
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

    private func updatePublicationProgress(
        _ value: Double,
        identifier: UUID
    ) {
        guard publication?.id == identifier else { return }
        let progress = min(max(value, 0), 1)
        let currentProgress = publication?.uploadProgress ?? 0
        guard progress >= currentProgress else { return }

        publication?.uploadProgress = progress
        if progress >= 1, publication?.stage == .uploading {
            publication?.stage = .processing
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

        // Cached content stays on screen while the refresh runs behind it.
        let preservingCachedValue = preservingCachedValue || hasFeedValue

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
            let snapshot = try await api.feed(accessToken: token)
            state = .loaded(snapshot)
            persistence.write(snapshot)
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

    private func perform(
        key: String,
        title: String,
        work: @escaping @MainActor (String) async throws -> Void
    ) {
        guard inFlightMutationKeys.insert(key).inserted else { return }
        actionFailure = nil
        Task {
            defer { inFlightMutationKeys.remove(key) }
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
        let profileMetrics = metrics(
            snapshot.currentProfileMetrics,
            adjustedFor: post,
            by: 1
        )
        if post.contentType == MobileSocialContentType.story.rawValue {
            snapshot = MobileSocialSnapshot(
                stories: [post] + snapshot.stories,
                posts: snapshot.posts,
                hacs: snapshot.hacs,
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: profileMetrics,
                creatorInsights: snapshot.creatorInsights)
        } else if post.contentType == MobileSocialContentType.hac.rawValue {
            snapshot = MobileSocialSnapshot(
                stories: snapshot.stories,
                posts: snapshot.posts,
                hacs: [post] + snapshot.hacs,
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: profileMetrics,
                creatorInsights: snapshot.creatorInsights)
        } else {
            snapshot = MobileSocialSnapshot(
                stories: snapshot.stories,
                posts: [post] + snapshot.posts,
                hacs: snapshot.hacs,
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: profileMetrics,
                creatorInsights: snapshot.creatorInsights)
        }
        state = .loaded(snapshot)
        insertProfilePost(post)
    }

    private func replace(_ post: MobileSocialPost) {
        mutate(postID: post.id) { _ in post }
    }

    private func remove(_ postID: UUID) {
        var mediaAssetIDs = Set<UUID>()
        if case .loaded(let snapshot) = state {
            let removedPost = snapshot.stories.first { $0.id == postID }
                ?? snapshot.posts.first { $0.id == postID }
                ?? snapshot.hacs.first { $0.id == postID }
            mediaAssetIDs.formUnion(removedPost?.media.map(\.id) ?? [])
            state = .loaded(MobileSocialSnapshot(
                stories: snapshot.stories.filter { $0.id != postID },
                posts: snapshot.posts.filter { $0.id != postID },
                hacs: snapshot.hacs.filter { $0.id != postID },
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: removedPost.map {
                    metrics(snapshot.currentProfileMetrics, adjustedFor: $0, by: -1)
                } ?? snapshot.currentProfileMetrics,
                creatorInsights: snapshot.creatorInsights))
        }

        if case .loaded(let posts) = profileContentState {
            mediaAssetIDs.formUnion(
                posts.first(where: { $0.id == postID })?.media.map(\.id) ?? [])
            profileContentState = .loaded(posts.filter { $0.id != postID })
        }

        for assetID in mediaAssetIDs {
            mediaCache.removeObject(forKey: assetID as NSUUID)
            removeCachedMediaFile(for: assetID)
            mediaFailures.removeValue(forKey: assetID)
        }
    }

    private func insertProfilePost(_ post: MobileSocialPost) {
        guard case .loaded(let posts) = profileContentState else { return }
        profileContentState = .loaded(
            ([post] + posts.filter { $0.id != post.id })
                .sorted { $0.postedUTC > $1.postedUTC })
    }

    private func append(_ comment: MobileSocialComment, to postID: UUID) {
        mutate(postID: postID) { post in
            post.replacing(
                commentCount: post.commentCount + 1,
                metrics: post.metrics.adjusting(commentCountBy: 1),
                comments: Array((post.comments + [comment]).suffix(4)))
        }
    }

    private func updateFollow(author: MobileSocialAuthor, isFollowing: Bool, isPending: Bool) {
        transformPosts(
            matching: { $0.author.identity == author.identity },
            transform: {
                $0.replacing(
                    followedByCurrentActor: isFollowing,
                    followRequestPending: isPending)
            })
    }

    private func follows(_ author: MobileSocialAuthor) -> Bool {
        guard case .loaded(let snapshot) = state else { return false }
        return (snapshot.stories + snapshot.posts + snapshot.hacs)
            .first { $0.author.identity == author.identity }?
            .followedByCurrentActor ?? false
    }

    private func adjustCurrentProfileMetrics(
        followingCountBy: Int = 0,
        followerCountBy: Int = 0
    ) {
        guard case .loaded(let snapshot) = state else { return }
        state = .loaded(MobileSocialSnapshot(
            stories: snapshot.stories,
            posts: snapshot.posts,
            hacs: snapshot.hacs,
            activity: snapshot.activity,
            activityCount: snapshot.activityCount,
            currentProfileMetrics: snapshot.currentProfileMetrics.adjusting(
                followerCountBy: followerCountBy,
                followingCountBy: followingCountBy
            ),
            creatorInsights: snapshot.creatorInsights
        ))
    }

    private func metrics(
        _ profileMetrics: MobileSocialProfileMetrics,
        adjustedFor post: MobileSocialPost,
        by change: Int
    ) -> MobileSocialProfileMetrics {
        switch post.contentType {
        case MobileSocialContentType.post.rawValue:
            return profileMetrics.adjusting(postCountBy: change)
        case MobileSocialContentType.hac.rawValue:
            return profileMetrics.adjusting(videoCountBy: change)
        case MobileSocialContentType.story.rawValue:
            return profileMetrics.adjusting(storyCountBy: change)
        default:
            return profileMetrics
        }
    }

    private func mutate(
        postID: UUID,
        transform: @escaping (MobileSocialPost) -> MobileSocialPost
    ) {
        transformPosts(matching: { $0.id == postID }, transform: transform)
    }

    private func transformPosts(
        matching predicate: @escaping (MobileSocialPost) -> Bool,
        transform: @escaping (MobileSocialPost) -> MobileSocialPost
    ) {
        if case .loaded(let snapshot) = state {
            let update: (MobileSocialPost) -> MobileSocialPost = {
                predicate($0) ? transform($0) : $0
            }
            state = .loaded(MobileSocialSnapshot(
                stories: snapshot.stories.map(update),
                posts: snapshot.posts.map(update),
                hacs: snapshot.hacs.map(update),
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: snapshot.currentProfileMetrics,
                creatorInsights: snapshot.creatorInsights))
        }

        if case .loaded(let posts) = profileContentState {
            profileContentState = .loaded(posts.map {
                predicate($0) ? transform($0) : $0
            })
        }
    }

    private func mediaFailurePresentation(for error: Error) -> UserFacingFailure {
        failure(for: error, title: "Media temporarily unavailable")
    }

    private func followMutationKey(
        userID: String,
        participantType: ParticipantType
    ) -> String {
        "follow:\(participantType.rawValue):\(userID.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())"
    }

    private func removeCachedMediaFile(for assetID: UUID) {
        guard let cached = mediaFileCache.removeValue(forKey: assetID) else {
            return
        }
        try? FileManager.default.removeItem(at: cached.url)
    }

    private func trimMediaFileCache(keeping assetID: UUID) {
        while mediaFileCache.count > Self.maximumCachedMediaFileCount ||
            mediaFileCache.values.reduce(0, { $0 + $1.byteCount }) > Self.maximumCachedMediaFileBytes {
            guard let victim = mediaFileCache
                .filter({ $0.key != assetID })
                .min(by: { $0.value.lastAccessed < $1.value.lastAccessed }) else {
                return
            }
            removeCachedMediaFile(for: victim.key)
        }
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(category: .networking, summary: "A native social request could not be completed.", correlationID: apiError?.correlationID)
        return UserFacingFailure(title: title, message: error.localizedDescription, correlationID: apiError?.correlationID)
    }
}
