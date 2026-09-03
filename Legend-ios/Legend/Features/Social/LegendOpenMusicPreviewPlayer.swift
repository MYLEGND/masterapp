import AVFoundation
import Combine
import Foundation

/// The single audio-session authority for all social playback. Hacs and music
/// previews share the same category so one feature cannot silently replace the
/// other's route or leave the device in an inconsistent audio state.
@MainActor
enum LegendSocialAudioSession {
    enum Owner: Hashable {
        case musicPreview
        case hac(UUID)
    }

    private static var activeOwners = Set<Owner>()
    private static var isConfigured = false
    private static var isActive = false
    static let activeHacDidChange = Notification.Name(
        "LegendSocialAudioSession.activeHacDidChange")

    /// Seeds one process-wide playback route. Hac paging deliberately keeps
    /// this route active while it changes players, avoiding a hardware audio
    /// restart at every vertical transition.
    static func prepareForPlayback() throws {
        let audioSession = AVAudioSession.sharedInstance()
        if !isConfigured {
            try audioSession.setCategory(
                .playback,
                mode: .moviePlayback,
                options: [.mixWithOthers])
            isConfigured = true
        }
        if !isActive {
            try audioSession.setActive(true)
            isActive = true
        }
    }

    static func beginPlayback(for owner: Owner) throws {
        try prepareForPlayback()

        if case let .hac(mediaID) = owner {
            // A vertical feed can keep adjacent views alive while scrolling.
            // Ownership—not view lifetime—decides which Hac is audible.
            activeOwners = Set(activeOwners.filter { existingOwner in
                guard case .hac = existingOwner else { return true }
                return existingOwner == owner
            })
            NotificationCenter.default.post(
                name: activeHacDidChange,
                object: nil,
                userInfo: ["mediaID": mediaID.uuidString])
        }

        activeOwners.insert(owner)
    }

    static func endPlayback(for owner: Owner) {
        activeOwners.remove(owner)
        guard activeOwners.isEmpty else { return }
        try? AVAudioSession.sharedInstance().setActive(
            false,
            options: .notifyOthersOnDeactivation)
        isActive = false
    }
}

/// Streams a selected Legend catalog track without downloading or retaining the
/// media file. AVFoundation is the native iOS audio framework, so this keeps the
/// player lightweight and avoids a second, competing playback stack.
@MainActor
final class LegendOpenMusicPreviewPlayer: ObservableObject {
    enum PlaybackState: Equatable {
        case idle
        case loading(trackID: String)
        case playing(trackID: String)
        case paused(trackID: String)
        case failed(trackID: String?, message: String)
    }

    @Published private(set) var state: PlaybackState = .idle

    private var player: AVPlayer?
    private var currentTrackID: String?
    private var activeLoadID: UUID?
    private var playbackEndedObserver: NSObjectProtocol?
    private var playbackFailedObserver: NSObjectProtocol?

    /// Loads the selected direct MP3 stream asynchronously, then begins playback.
    func play(_ track: MobileSocialMusicTrack) async {
        do {
            if currentTrackID != track.id || isFailed {
                try await load(track)
            }

            guard currentTrackID == track.id, let player else { return }
            player.play()
            state = .playing(trackID: track.id)
        } catch is CancellationError {
            // A newer selection or an explicit stop superseded this load.
        } catch {
            fail(trackID: track.id, error: error)
        }
    }

    /// Toggles only the requested track. Selecting another track stops the prior
    /// stream before it is asynchronously prepared.
    func toggle(_ track: MobileSocialMusicTrack) async {
        if currentTrackID == track.id, isPlaying {
            pause()
            return
        }

        await play(track)
    }

    func pause() {
        guard let currentTrackID else { return }
        player?.pause()
        state = .paused(trackID: currentTrackID)
    }

    /// Stops playback, invalidates any pending asynchronous load, and releases
    /// the AVPlayer item so the stream is not retained in memory.
    func stop() {
        activeLoadID = nil
        removeObservers()
        player?.pause()
        player?.replaceCurrentItem(with: nil)
        player = nil
        currentTrackID = nil
        LegendSocialAudioSession.endPlayback(for: .musicPreview)
        state = .idle
    }

    private var isPlaying: Bool {
        if case .playing = state {
            return true
        }
        return false
    }

    private var isFailed: Bool {
        if case .failed = state {
            return true
        }
        return false
    }

    private func load(_ track: MobileSocialMusicTrack) async throws {
        // `audioURL` is the backend's audio_url value. Only HTTPS stream URLs
        // from the curated catalog are accepted by this player.
        guard let streamURL = track.audioURL,
              streamURL.scheme?.lowercased() == "https" else {
            throw LegendOpenMusicPreviewError.invalidStreamURL
        }

        stop()
        try configureAudioSession()

        let loadID = UUID()
        activeLoadID = loadID
        state = .loading(trackID: track.id)

        // AVURLAsset loads the remote MP3 metadata asynchronously. No audio is
        // copied into the app's database, documents directory, or cache by us.
        let audioAsset = AVURLAsset(url: streamURL)
        let isPlayable = try await audioAsset.load(.isPlayable)
        guard activeLoadID == loadID else {
            throw CancellationError()
        }
        guard isPlayable else {
            throw LegendOpenMusicPreviewError.unplayableStream
        }

        let item = AVPlayerItem(asset: audioAsset)
        player = AVPlayer(playerItem: item)
        currentTrackID = track.id
        observe(item: item, trackID: track.id)
    }

    private func observe(item: AVPlayerItem, trackID: String) {
        playbackEndedObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime,
            object: item,
            queue: .main) { [weak self] _ in
                Task { @MainActor in
                    self?.didFinish(trackID: trackID)
                }
            }

        playbackFailedObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime,
            object: item,
            queue: .main) { [weak self] notification in
                let error = notification.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error
                Task { @MainActor in
                    self?.fail(trackID: trackID, error: error ?? LegendOpenMusicPreviewError.unplayableStream)
                }
            }
    }

    private func didFinish(trackID: String) {
        guard currentTrackID == trackID else { return }
        player?.seek(to: .zero)
        state = .paused(trackID: trackID)
    }

    private func fail(trackID: String?, error: Error) {
        player?.pause()
        LegendSocialAudioSession.endPlayback(for: .musicPreview)
        state = .failed(trackID: trackID, message: LegendLocalized("This music preview could not be played. Please try another track."))
    }

    private func configureAudioSession() throws {
        try LegendSocialAudioSession.beginPlayback(for: .musicPreview)
    }

    private func removeObservers() {
        if let playbackEndedObserver {
            NotificationCenter.default.removeObserver(playbackEndedObserver)
            self.playbackEndedObserver = nil
        }
        if let playbackFailedObserver {
            NotificationCenter.default.removeObserver(playbackFailedObserver)
            self.playbackFailedObserver = nil
        }
    }
}

private enum LegendOpenMusicPreviewError: LocalizedError {
    case invalidStreamURL
    case unplayableStream

    var errorDescription: String? {
        switch self {
        case .invalidStreamURL:
            return LegendLocalized("This music stream has an invalid URL.")
        case .unplayableStream:
            return LegendLocalized("This music stream is not playable.")
        }
    }
}
