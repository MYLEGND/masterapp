import AVFoundation
import Foundation

/// The authenticated HTTP source for one protected social video.  It is not a
/// second cache or a public URL: the native resource loader owns it solely to
/// issue authenticated byte-range requests to the existing media endpoint.
struct MobileSocialMediaStream: Sendable {
    let url: URL
    let headers: [String: String]
}

/// Native creation policy shared by the picker and the file preparer. It is
/// deliberately non-UI isolated because the picker reads it before any video
/// export begins.
enum LegendSocialVideoUploadPolicy {
    static var maximumDurationSeconds: TimeInterval {
        guard let duration = LegendSharedDesign
            .socialFormat("post")
            .maximumVideoDurationSeconds else {
            preconditionFailure("Missing LEGEND social video duration policy.")
        }
        return duration
    }
}

/// The one mobile-video preparation path for Legend social media. It produces
/// a network-optimized H.264/AAC MP4 before the file reaches the upload queue,
/// so Hacs do not depend on the original device's HEVC, MOV, HDR, or editing
/// container support at playback time.
@MainActor
enum LegendSocialVideoPreparation {
    private static let maximumUploadBytes = 100 * 1_024 * 1_024
    private static let preferredExportPresets = [
        AVAssetExportPreset1920x1080,
        AVAssetExportPreset1280x720,
        AVAssetExportPresetMediumQuality
    ]

    enum PreparationError: LocalizedError {
        case unreadableSource
        case noVideoTrack
        case noCompatibleExport
        case exportFailed
        case exceedsUploadLimit
        case exceedsDurationLimit

        var errorDescription: String? {
            switch self {
            case .unreadableSource:
                "This video could not be read on this device. Choose another video and try again."
            case .noVideoTrack:
                "This selection does not contain a playable video track."
            case .noCompatibleExport:
                "Legend could not prepare this video for reliable playback."
            case .exportFailed:
                "Legend could not finish preparing this video. Please try again."
            case .exceedsUploadLimit:
                "This video is still too large after optimization. Choose a shorter video and try again."
            case .exceedsDurationLimit:
                "Videos must be 10 minutes or less."
            }
        }
    }

    /// Uses the device media pipeline to create the portable H.264/AAC MP4 that
    /// every Hac uploader sends. The output is optimized for network playback
    /// and is verified before the composer is allowed to publish it.
    static func prepareForPublication(from sourceURL: URL) async throws -> URL {
        let asset = AVURLAsset(url: sourceURL)
        try await validate(asset: asset, enforcingPublicationDuration: true)
        let sourceHasAudio = try await hasAudioTrack(in: asset)

        for preset in preferredExportPresets {
            let isCompatible = await AVAssetExportSession.compatibility(
                ofExportPreset: preset,
                with: asset,
                outputFileType: .mp4)
            guard isCompatible else { continue }

            let outputURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("legend-social-video-\(UUID().uuidString)")
                .appendingPathExtension("mp4")

            do {
                try await export(
                    asset: asset,
                    preset: preset,
                    outputURL: outputURL)

                let fileSize = try outputURL.resourceValues(
                    forKeys: [.fileSizeKey]).fileSize ?? 0
                guard fileSize > 0 else {
                    try? FileManager.default.removeItem(at: outputURL)
                    continue
                }

                guard fileSize <= maximumUploadBytes else {
                    try? FileManager.default.removeItem(at: outputURL)
                    continue
                }

                let preparedVideoIsPlayable = await isPlayableVideo(at: outputURL)
                guard preparedVideoIsPlayable else {
                    try? FileManager.default.removeItem(at: outputURL)
                    continue
                }

                // A Hac may intentionally be silent, but a source that has an
                // audio track must never lose it during normalization.
                let preparedVideoHasAudio = await hasAudioTrack(at: outputURL)
                guard !sourceHasAudio || preparedVideoHasAudio else {
                    try? FileManager.default.removeItem(at: outputURL)
                    continue
                }

                return outputURL
            } catch is CancellationError {
                try? FileManager.default.removeItem(at: outputURL)
                throw CancellationError()
            } catch {
                try? FileManager.default.removeItem(at: outputURL)
            }
        }

        if let sourceSize = try? sourceURL.resourceValues(
            forKeys: [.fileSizeKey]).fileSize,
           sourceSize > maximumUploadBytes {
            throw PreparationError.exceedsUploadLimit
        }
        throw PreparationError.exportFailed
    }

    /// Renders the creator's real video edit decision through the same native
    /// H.264/AAC preparation authority used for every social video.  A source
    /// with no trim or audio change is deliberately reused; creating another
    /// lossy export would add cost without changing what the member published.
    static func prepareEditedForPublication(
        from sourceURL: URL,
        trimStartSeconds: Double,
        trimEndSeconds: Double?,
        muteOriginalAudio: Bool
    ) async throws -> URL {
        let source = AVURLAsset(url: sourceURL)
        try await validate(asset: source, enforcingPublicationDuration: true)
        let sourceHasAudio = try await hasAudioTrack(in: source)

        let duration = try await source.load(.duration).seconds
        let start = min(max(0, trimStartSeconds), max(0, duration - 0.01))
        let end = min(max(start + 0.01, trimEndSeconds ?? duration), duration)

        guard end > start else {
            throw PreparationError.unreadableSource
        }

        let needsTrim = start > 0.01 || end < duration - 0.01
        guard needsTrim || muteOriginalAudio else {
            return sourceURL
        }

        let videoTracks = try await source.loadTracks(withMediaType: .video)
        let composition = AVMutableComposition()
        guard let sourceVideo = videoTracks.first,
              let compositionVideo = composition.addMutableTrack(
                withMediaType: .video,
                preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw PreparationError.noVideoTrack
        }

        let range = CMTimeRange(
            start: CMTime(seconds: start, preferredTimescale: 600),
            end: CMTime(seconds: end, preferredTimescale: 600))
        try compositionVideo.insertTimeRange(range, of: sourceVideo, at: .zero)
        compositionVideo.preferredTransform = try await sourceVideo.load(.preferredTransform)

        if !muteOriginalAudio,
           let sourceAudio = try await source.loadTracks(withMediaType: .audio).first,
           let compositionAudio = composition.addMutableTrack(
               withMediaType: .audio,
               preferredTrackID: kCMPersistentTrackID_Invalid) {
            try compositionAudio.insertTimeRange(range, of: sourceAudio, at: .zero)
        }

        for preset in preferredExportPresets {
            let compatible = await AVAssetExportSession.compatibility(
                ofExportPreset: preset,
                with: composition,
                outputFileType: .mp4)
            guard compatible else { continue }

            let outputURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("legend-social-edited-video-\(UUID().uuidString)")
                .appendingPathExtension("mp4")

            do {
                try await export(asset: composition, preset: preset, outputURL: outputURL)
                let fileSize = try outputURL.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                guard fileSize > 0,
                      fileSize <= maximumUploadBytes,
                      await isPlayableVideo(at: outputURL) else {
                    try? FileManager.default.removeItem(at: outputURL)
                    continue
                }

                if !muteOriginalAudio, sourceHasAudio {
                    let outputHasAudio = await hasAudioTrack(at: outputURL)
                    guard outputHasAudio else {
                        try? FileManager.default.removeItem(at: outputURL)
                        continue
                    }
                }
                return outputURL
            } catch is CancellationError {
                try? FileManager.default.removeItem(at: outputURL)
                throw CancellationError()
            } catch {
                try? FileManager.default.removeItem(at: outputURL)
            }
        }

        throw PreparationError.exportFailed
    }

    /// The same playback check is used for the newly exported file and for a
    /// protected video received from the API. A bad media object is surfaced as
    /// an actionable error instead of a silent black AVPlayer view.
    static func isPlayableVideo(at url: URL) async -> Bool {
        let asset = AVURLAsset(url: url)
        do {
            try await validate(asset: asset, enforcingPublicationDuration: false)
            return true
        } catch {
            return false
        }
    }

    private static func validate(
        asset: AVURLAsset,
        enforcingPublicationDuration: Bool
    ) async throws {
        guard try await asset.load(.isPlayable) else {
            throw PreparationError.unreadableSource
        }

        let duration = try await asset.load(.duration).seconds
        guard duration.isFinite, duration > 0 else {
            throw PreparationError.unreadableSource
        }

        if enforcingPublicationDuration,
           duration > LegendSocialVideoUploadPolicy.maximumDurationSeconds {
            throw PreparationError.exceedsDurationLimit
        }

        let tracks = try await asset.loadTracks(withMediaType: .video)
        guard !tracks.isEmpty else {
            throw PreparationError.noVideoTrack
        }
    }

    private static func hasAudioTrack(in asset: AVURLAsset) async throws -> Bool {
        !(try await asset.loadTracks(withMediaType: .audio)).isEmpty
    }

    private static func hasAudioTrack(at url: URL) async -> Bool {
        do {
            return try await hasAudioTrack(in: AVURLAsset(url: url))
        } catch {
            return false
        }
    }

    private static func export(
        asset: AVAsset,
        preset: String,
        outputURL: URL
    ) async throws {
        if #available(iOS 18.0, *) {
            guard let session = AVAssetExportSession(
                asset: asset,
                presetName: preset) else {
                throw PreparationError.noCompatibleExport
            }
            try await session.export(to: outputURL, as: .mp4)
            return
        }

        // Keep iOS 17 support without sharing AVAssetExportSession across
        // concurrency domains. Newer systems use AVFoundation's native async
        // export API above; this bridge only exists for the supported OS floor.
        guard let legacyExport = LegacyHacVideoExport(
            asset: asset,
            preset: preset) else {
            throw PreparationError.noCompatibleExport
        }
        try await legacyExport.export(to: outputURL)
    }
}

/// AVAssetExportSession is intentionally non-Sendable. This actor-isolated
/// bridge confines the legacy iOS 17 API to the main actor while the export is
/// in flight, avoiding unsafe captures in Swift 6's @Sendable callbacks.
@MainActor
private final class LegacyHacVideoExport {
    private let session: AVAssetExportSession

    init?(asset: AVAsset, preset: String) {
        guard let session = AVAssetExportSession(
            asset: asset,
            presetName: preset),
            session.supportedFileTypes.contains(.mp4) else {
            return nil
        }
        self.session = session
    }

    func export(to outputURL: URL) async throws {
        session.outputURL = outputURL
        session.outputFileType = .mp4
        session.shouldOptimizeForNetworkUse = true

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<Void, Error>) in
                session.exportAsynchronously { [weak self] in
                    Task { @MainActor [weak self] in
                        guard let self else {
                            continuation.resume(throwing: CancellationError())
                            return
                        }

                        switch self.session.status {
                        case .completed:
                            continuation.resume()
                        case .cancelled:
                            continuation.resume(throwing: CancellationError())
                        case .failed:
                            continuation.resume(
                                throwing: self.session.error
                                    ?? LegendSocialVideoPreparation.PreparationError.exportFailed)
                        default:
                            continuation.resume(
                                throwing: LegendSocialVideoPreparation.PreparationError.exportFailed)
                        }
                    }
                }
            }
        } onCancel: { [weak self] in
            Task { @MainActor [weak self] in
                self?.session.cancelExport()
            }
        }
    }
}

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
    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        previewImage: MultipartFormFile?,
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
    func previewData(assetID: UUID, accessToken: String) async throws -> Data
    func downloadMedia(assetID: UUID, accessToken: String) async throws -> URL
    func mediaStream(assetID: UUID, accessToken: String) async throws -> MobileSocialMediaStream?
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
    /// Backwards-compatible API seam for a creator-selected Hac poster. Test
    /// doubles that only model the original endpoint continue to work, while
    /// the production implementation submits the image as a separate form part.
    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        previewImage: MultipartFormFile?,
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        audience: MobileSocialAudience,
        location: String?,
        commentsEnabled: Bool,
        uploadProgress: @escaping @Sendable (Double) -> Void,
        accessToken: String
    ) async throws -> MobileSocialPost {
        try await createMediaPost(
            type: type,
            body: body,
            files: files,
            accessibilityText: accessibilityText,
            music: music,
            audience: audience,
            location: location,
            commentsEnabled: commentsEnabled,
            uploadProgress: uploadProgress,
            accessToken: accessToken)
    }

    /// API doubles can keep providing the primary media bytes. Production uses
    /// the protected poster endpoint when the server reports one is available.
    func previewData(assetID: UUID, accessToken: String) async throws -> Data {
        try await mediaData(assetID: assetID, accessToken: accessToken)
    }

    /// API doubles can keep supplying bytes. The production API overrides this
    /// with URLSession's file-backed download path for video playback.
    func downloadMedia(assetID: UUID, accessToken: String) async throws -> URL {
        let data = try await mediaData(assetID: assetID, accessToken: accessToken)
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-social-download-\(assetID.uuidString)")
        try data.write(to: url, options: .atomic)
        return url
    }

    /// Test and offline implementations retain the file-backed fallback.  The
    /// production client supplies a stream source so Hac playback does not wait
    /// for a complete protected MP4 download.
    func mediaStream(
        assetID: UUID,
        accessToken: String
    ) async throws -> MobileSocialMediaStream? {
        nil
    }

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
        try await createMediaPost(
            type: type,
            body: body,
            files: files,
            previewImage: nil,
            accessibilityText: accessibilityText,
            music: music,
            audience: audience,
            location: location,
            commentsEnabled: commentsEnabled,
            uploadProgress: uploadProgress,
            accessToken: accessToken)
    }

    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        previewImage: MultipartFormFile?,
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
        try await createMediaPost(
            type: type,
            body: body,
            files: files,
            previewImage: nil,
            accessibilityText: accessibilityText,
            music: music,
            audience: audience,
            location: location,
            commentsEnabled: commentsEnabled,
            uploadProgress: uploadProgress,
            accessToken: accessToken)
    }

    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        previewImage: MultipartFormFile?,
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
            files: files + (previewImage.map { [$0] } ?? []),
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

    func previewData(assetID: UUID, accessToken: String) async throws -> Data {
        try await client.getData(
            "/api/v1/mobile/social/media/\(assetID.uuidString)/preview",
            accessToken: accessToken,
            headers: participantHeader
        )
    }

    func downloadMedia(assetID: UUID, accessToken: String) async throws -> URL {
        try await client.downloadFile(
            "/api/v1/mobile/social/media/\(assetID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
    }

    func mediaStream(
        assetID: UUID,
        accessToken: String
    ) async throws -> MobileSocialMediaStream? {
        try client.protectedMediaStream(
            "/api/v1/mobile/social/media/\(assetID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader)
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

private actor LegendSocialPreviewRequestGate {
    private let maximumConcurrentRequests: Int
    private var activeRequests = 0
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(maximumConcurrentRequests: Int) {
        self.maximumConcurrentRequests = max(1, maximumConcurrentRequests)
    }

    func acquire() async {
        if activeRequests < maximumConcurrentRequests {
            activeRequests += 1
            return
        }

        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func release() {
        if waiters.isEmpty {
            activeRequests = max(0, activeRequests - 1)
            return
        }

        waiters.removeFirst().resume()
    }
}

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
    private let previewCache = NSCache<NSUUID, NSData>()
    private let previewRequestGate = LegendSocialPreviewRequestGate(maximumConcurrentRequests: 3)
    private var mediaFileCache: [UUID: CachedMediaFile] = [:]
    private var mediaStreamCache: [UUID: MobileSocialMediaStream] = [:]
    private var mediaLoadTasks: [UUID: Task<Data?, Never>] = [:]
    private var previewLoadTasks: [UUID: Task<Data?, Never>] = [:]
    private var mediaFileLoadTasks: [UUID: Task<URL?, Never>] = [:]
    private var mediaStreamLoadTasks: [UUID: Task<MobileSocialMediaStream?, Never>] = [:]
    private var inFlightMutationKeys: Set<String> = []
    private var recordedBasicViewPostIDs: Set<UUID> = []
    private var maximumRecordedWatchSecondsByPostID: [UUID: Decimal] = [:]
    private var completedViewPostIDs: Set<UUID> = []
    private var lastRecordedViewTelemetryAtByPostID: [UUID: Date] = [:]
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
        previewCache.countLimit = 72
        previewCache.totalCostLimit = 12 * 1_024 * 1_024

        // The feed shows last-known posts immediately rather than a spinner, then
        // refreshes underneath.
        if let cached = persistence.read() {
            state = .loaded(cached)
        }
    }

    deinit {
        mediaLoadTasks.values.forEach { $0.cancel() }
        previewLoadTasks.values.forEach { $0.cancel() }
        mediaFileLoadTasks.values.forEach { $0.cancel() }
        mediaStreamLoadTasks.values.forEach { $0.cancel() }
        for cached in mediaFileCache.values {
            try? FileManager.default.removeItem(at: cached.url)
        }
    }

    func load() {
        guard feedLoadTask == nil, !hasFeedValue else { return }
        state = .loading
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

    /// Retrieves a server-selected Hac poster without opening or playing the
    /// underlying video. This is the shared preview source for home and profile
    /// grids, so their thumbnails never create their own video players.
    func previewData(for assetID: UUID, forceRefresh: Bool = false) async -> Data? {
        if forceRefresh {
            previewCache.removeObject(forKey: assetID as NSUUID)
        } else if let cached = previewCache.object(forKey: assetID as NSUUID) {
            return cached as Data
        }

        if let existingTask = previewLoadTasks[assetID] {
            return await existingTask.value
        }

        let task = Task { [weak self] () -> Data? in
            guard let self else { return nil }

            await self.previewRequestGate.acquire()
            defer {
                Task { await self.previewRequestGate.release() }
            }

            guard !Task.isCancelled else { return nil }

            do {
                let token = try await self.accessTokenProvider()
                let data = try await self.api.previewData(
                    assetID: assetID,
                    accessToken: token)
                self.previewCache.setObject(
                    data as NSData,
                    forKey: assetID as NSUUID,
                    cost: data.count)
                return data
            } catch {
                return nil
            }
        }
        previewLoadTasks[assetID] = task
        let data = await task.value
        previewLoadTasks.removeValue(forKey: assetID)
        return data
    }

    func mediaFile(
        for media: MobileSocialMedia,
        forceRefresh: Bool = false
    ) async -> URL? {
        if forceRefresh {
            mediaCache.removeObject(forKey: media.id as NSUUID)
            removeCachedMediaFile(for: media.id)
            mediaFailures.removeValue(forKey: media.id)
        }

        if var cached = mediaFileCache[media.id],
           FileManager.default.fileExists(atPath: cached.url.path) {
            cached.lastAccessed = .now
            mediaFileCache[media.id] = cached
            return cached.url
        }

        mediaFileCache.removeValue(forKey: media.id)

        if let existingTask = mediaFileLoadTasks[media.id] {
            return await existingTask.value
        }

        let task = Task { [weak self] () -> URL? in
            guard let self else { return nil }
            return await self.materializeMediaFile(media)
        }
        mediaFileLoadTasks[media.id] = task
        let fileURL = await task.value
        mediaFileLoadTasks.removeValue(forKey: media.id)
        return fileURL
    }

    /// Returns the one protected source used by the shared Hac playback
    /// coordinator.  AVFoundation consumes this through range requests; it
    /// does not materialize the complete MP4 before it can render frame one.
    func mediaStream(
        for media: MobileSocialMedia,
        forceRefresh: Bool = false
    ) async -> MobileSocialMediaStream? {
        if forceRefresh {
            mediaStreamCache.removeValue(forKey: media.id)
            mediaFailures.removeValue(forKey: media.id)
        } else if let cached = mediaStreamCache[media.id] {
            return cached
        }

        if let existingTask = mediaStreamLoadTasks[media.id] {
            return await existingTask.value
        }

        let task = Task { [weak self] () -> MobileSocialMediaStream? in
            guard let self else { return nil }
            do {
                let token = try await self.accessTokenProvider()
                guard let stream = try await self.api.mediaStream(
                    assetID: media.id,
                    accessToken: token) else {
                    return nil
                }
                self.mediaStreamCache[media.id] = stream
                self.mediaFailures.removeValue(forKey: media.id)
                return stream
            } catch {
                // Preserve the established local-file fallback for an API double
                // or a deployment that has not yet enabled stream sources.
                return nil
            }
        }
        mediaStreamLoadTasks[media.id] = task
        let stream = await task.value
        mediaStreamLoadTasks.removeValue(forKey: media.id)
        return stream
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
        if storyInteractionType == nil {
            if watchDurationSeconds == nil && watchCompletionPercentage == nil {
                guard recordedBasicViewPostIDs.insert(postID).inserted else { return }
            } else {
                let watchSeconds = max(0, watchDurationSeconds ?? 0)
                let completion = max(0, watchCompletionPercentage ?? 0)
                let completed = completion >= 100

                if completedViewPostIDs.contains(postID) { return }

                let previousMaximum = maximumRecordedWatchSecondsByPostID[postID] ?? -1
                guard completed || watchSeconds > previousMaximum else { return }

                maximumRecordedWatchSecondsByPostID[postID] = watchSeconds
                if completed {
                    completedViewPostIDs.insert(postID)
                }
            }
        }

        // SocialPostViewer is the durable max-based metrics authority.
        // Preserve Story interactions and completion while suppressing
        // sub-second progressive playback writes for the same post.
        let isStoryInteraction = storyInteractionType != nil
        let isCompletion = (watchCompletionPercentage ?? 0) >= 100
        let isMeasuredPlayback = watchDurationSeconds != nil ||
            watchCompletionPercentage != nil
        let minimumViewTelemetryInterval: TimeInterval = 2.0

        if isMeasuredPlayback && !isStoryInteraction && !isCompletion {
            let now = Date()
            if let last = lastRecordedViewTelemetryAtByPostID[postID],
               now.timeIntervalSince(last) < minimumViewTelemetryInterval {
                return
            }
            lastRecordedViewTelemetryAtByPostID[postID] = now
        }

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
            previewImage: request.previewImage,
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
            discardStagedFiles(in: request)
            pendingPublicationRequest = nil
            publication?.uploadProgress = 1

            guard post.media.allSatisfy({ $0.processingState == "Ready" }) else {
                // The API has accepted the post, but the established server
                // worker still owns video conversion. Do not show a false
                // "shared" result or insert a not-yet-playable Hac into the
                // feed; observe the same authoritative profile projection
                // until that worker marks the media ready.
                publication?.stage = .processing
                await awaitMediaReadiness(for: post.id, publicationID: identifier)
                return
            }

            completePublication(post, identifier: identifier)
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

    private func awaitMediaReadiness(
        for postID: UUID,
        publicationID: UUID
    ) async {
        while publication?.id == publicationID,
              publication?.stage == .processing,
              !Task.isCancelled {
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled,
                  publication?.id == publicationID,
                  publication?.stage == .processing else {
                return
            }

            do {
                let token = try await accessTokenProvider()
                let posts = try await api.currentProfilePosts(accessToken: token)
                guard let refreshed = posts.first(where: { $0.id == postID }) else {
                    continue
                }

                if refreshed.media.allSatisfy({ $0.processingState == "Ready" }) {
                    completePublication(refreshed, identifier: publicationID)
                    return
                }

                if refreshed.media.contains(where: { $0.processingState == "Failed" }) {
                    actionFailure = UserFacingFailure(
                        title: "Video processing needs attention",
                        message: "Legend could not finish preparing this video. Choose the video again and try publishing.",
                        correlationID: nil)
                    publication?.stage = .failed
                    publication?.failureMessage = actionFailure?.message
                    return
                }
            } catch {
                // The post is already persisted. A temporary refresh failure
                // must not be presented as a failed upload or erase the real
                // server processing status; the next observation retries.
            }
        }
    }

    private func completePublication(
        _ post: MobileSocialPost,
        identifier: UUID
    ) {
        guard publication?.id == identifier else { return }
        applyPublishedPost(post)
        publication?.stage = .published
        publication?.uploadProgress = 1
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
                creatorInsights: snapshot.creatorInsights,
                promotedGroups: snapshot.promotedGroups)
        } else if post.contentType == MobileSocialContentType.hac.rawValue {
            snapshot = MobileSocialSnapshot(
                stories: snapshot.stories,
                posts: snapshot.posts,
                hacs: [post] + snapshot.hacs,
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: profileMetrics,
                creatorInsights: snapshot.creatorInsights,
                promotedGroups: snapshot.promotedGroups)
        } else {
            snapshot = MobileSocialSnapshot(
                stories: snapshot.stories,
                posts: [post] + snapshot.posts,
                hacs: snapshot.hacs,
                activity: snapshot.activity,
                activityCount: snapshot.activityCount,
                currentProfileMetrics: profileMetrics,
                creatorInsights: snapshot.creatorInsights,
                promotedGroups: snapshot.promotedGroups)
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
                creatorInsights: snapshot.creatorInsights,
                promotedGroups: snapshot.promotedGroups))
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
            creatorInsights: snapshot.creatorInsights,
            promotedGroups: snapshot.promotedGroups
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
                creatorInsights: snapshot.creatorInsights,
                promotedGroups: snapshot.promotedGroups))
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

    private func materializeMediaFile(_ media: MobileSocialMedia) async -> URL? {
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-social-\(media.id.uuidString)")
            .appendingPathExtension(media.playbackFileExtension)
        try? FileManager.default.removeItem(at: destination)

        do {
            let token = try await accessTokenProvider()
            let downloadedURL: URL
            let byteCount: Int

            if media.isVideo {
                downloadedURL = try await api.downloadMedia(
                    assetID: media.id,
                    accessToken: token)
                byteCount = try downloadedURL.resourceValues(
                    forKeys: [.fileSizeKey]).fileSize ?? 0
            } else {
                let data = try await api.mediaData(
                    assetID: media.id,
                    accessToken: token)
                try data.write(to: destination, options: .atomic)
                downloadedURL = destination
                byteCount = data.count
            }

            guard byteCount > 0 else {
                try? FileManager.default.removeItem(at: downloadedURL)
                throw LegendSocialVideoPreparation.PreparationError.unreadableSource
            }

            if downloadedURL != destination {
                try FileManager.default.moveItem(at: downloadedURL, to: destination)
            }

            if media.isVideo,
               !(await LegendSocialVideoPreparation.isPlayableVideo(at: destination)) {
                try? FileManager.default.removeItem(at: destination)
                mediaFailures[media.id] = UserFacingFailure(
                    title: "Video unavailable",
                    message: "Legend could not verify this video for playback. Ask the creator to publish it again.",
                    correlationID: nil)
                return nil
            }

            mediaFileCache[media.id] = CachedMediaFile(
                url: destination,
                byteCount: byteCount,
                lastAccessed: .now)
            trimMediaFileCache(keeping: media.id)
            mediaFailures.removeValue(forKey: media.id)
            return destination
        } catch {
            try? FileManager.default.removeItem(at: destination)
            let presentation = mediaFailurePresentation(for: error)
            diagnostics.record(
                category: .networking,
                summary: "A protected social video could not be prepared.",
                correlationID: presentation.correlationID)
            mediaFailures[media.id] = presentation
            return nil
        }
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
