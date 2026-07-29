import AVFoundation
import AVKit
import Photos
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
import UIKit

struct LegendSocialCreationSheet: View {
    @Binding var route: LegendSocialCreationRoute?
    @ObservedObject var social: MobileSocialStore

    var body: some View {
        Group {
            switch route {
            case .menu:
                LegendSocialCreationModeMenu(
                    select: { route = .composer($0) },
                    dismiss: { route = nil })

            case .composer(let type):
                LegendSocialComposer(
                    type: type,
                    social: social,
                    dismiss: { route = nil })

            case nil:
                EmptyView()
            }
        }
    }
}

private struct LegendSocialCreationModeMenu: View {
    let select: (MobileSocialContentType) -> Void
    let dismiss: () -> Void

    @State private var selectedType: MobileSocialContentType = .post

    var body: some View {
        NavigationStack {
            VStack(spacing: LegendNextSpacing.lg) {
                Spacer(minLength: LegendNextSpacing.md)

                Image(systemName: selectedType.systemImage)
                    .font(.system(size: 44, weight: .semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 96, height: 96)
                    .background(
                        LegendNextColor.navy,
                        in: Circle())
                    .accessibilityHidden(true)

                VStack(spacing: LegendNextSpacing.xs) {
                    Text(selectedType.creationTitle)
                        .font(.title2.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(selectedType.creationPrompt)
                        .font(LegendNextTypography.body)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, LegendNextSpacing.xl)

                Spacer()

                Button {
                    select(selectedType)
                } label: {
                    Label("Continue with \(selectedType.displayName)", systemImage: "arrow.right")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendButtonStyle(kind: .primary))
                .padding(.horizontal, LegendNextSpacing.md)
            }
            .padding(.vertical, LegendNextSpacing.md)
            .background(LegendNextColor.canvas)
            .navigationTitle("Create")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: dismiss)
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) {
                LegendSocialCreationModeRail(selection: $selectedType)
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }
}

private struct LegendSocialCreationModeRail: View {
    @Binding var selection: MobileSocialContentType

    var body: some View {
        HStack(spacing: 0) {
            ForEach(MobileSocialContentType.allCases) { candidate in
                Button {
                    withAnimation(.snappy) {
                        selection = candidate
                    }
                } label: {
                    Label(candidate.displayName, systemImage: candidate.systemImage)
                        .font(.subheadline.weight(.bold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, LegendNextSpacing.sm)
                        .foregroundStyle(candidate == selection ? LegendNextColor.navy : LegendNextColor.textSecondary)
                        .background(candidate == selection ? LegendNextColor.gold.opacity(0.22) : Color.clear)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Create \(candidate.displayName)")
                .accessibilityAddTraits(candidate == selection ? .isSelected : [])
            }
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
        .background(LegendNextColor.surfaceElevated)
    }
}

struct LegendSocialComposer: View {
    @ObservedObject var social: MobileSocialStore
    let dismiss: () -> Void

    @StateObject private var photoLibrary = LegendPhotoLibraryAccess()
    @State private var caption = ""
    @State private var type: MobileSocialContentType
    @State private var selectedMedia: [LegendSocialMediaDraft] = []
    @State private var accessibilityText = ""
    @State private var mediaSelectionError: String?
    @State private var stage: LegendSocialCreationStage = .library
    @State private var selectedMusic: LegendSocialMusicDraft?
    @State private var ownsMediaAfterDismissal = false

    init(
        type: MobileSocialContentType,
        social: MobileSocialStore,
        dismiss: @escaping () -> Void
    ) {
        _type = State(initialValue: type)
        _social = ObservedObject(wrappedValue: social)
        self.dismiss = dismiss
    }

    var body: some View {
        NavigationStack {
            Group {
                if stage == .metadata || stage == .music || stage == .handedOff {
                    metadataContent
                } else {
                    libraryContent
                }
            }
            .background(LegendNextColor.canvas)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(stage == .metadata ? "Back" : "Cancel") {
                        if stage == .metadata {
                            stage = .library
                        } else {
                            cancel()
                        }
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(primaryActionTitle) {
                        primaryAction()
                    }
                    .disabled(!canContinue)
                }
            }
        }
        .presentationDetents([.large])
        .presentationDragIndicator(.hidden)
        .sheet(isPresented: musicPickerPresented) {
            LegendSocialMusicSelectionSheet(
                social: social,
                selection: selectedMusic,
                save: {
                    selectedMusic = $0
                    stage = .metadata
                },
                cancel: { stage = .metadata })
        }
        .fullScreenCover(isPresented: cameraPresented) {
            LegendSocialCameraCapture(
                capturesVideo: type.requiresVideo,
                captured: { result in
                    addCapturedMedia(result)
                    stage = .library
                },
                cancelled: {
                    stage = .library
                })
            .ignoresSafeArea()
        }
        .onAppear {
            photoLibrary.refresh()
        }
        .onReceive(
            NotificationCenter.default.publisher(
                for: UIApplication.didBecomeActiveNotification)
        ) { _ in
            photoLibrary.refresh()
        }
        .onChange(of: type) { _, updatedType in
            validateSelection(for: updatedType)
        }
        .onDisappear {
            if !ownsMediaAfterDismissal {
                discardTemporaryMedia()
            }
        }
    }

    private var canPublish: Bool {
        if type.requiresVideo {
            return selectedMedia.contains(where: \.isVideo)
        }

        return !caption.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !selectedMedia.isEmpty
    }

    private var canContinue: Bool {
        switch stage {
        case .library, .preparingMedia, .camera:
            return hasValidSelection || type == .post
        case .metadata:
            return canPublish && social.publication == nil
        case .music, .handedOff, .failed:
            return false
        }
    }

    private var primaryActionTitle: String {
        stage == .metadata ? "Share" : "Next"
    }

    private var musicPickerPresented: Binding<Bool> {
        Binding(
            get: { stage == .music },
            set: { if !$0 { stage = .metadata } })
    }

    private var cameraPresented: Binding<Bool> {
        Binding(
            get: { stage == .camera },
            set: { if !$0 && stage == .camera { stage = .library } })
    }

    private var libraryContent: some View {
        VStack(spacing: 0) {
            selectionPreview
            photoLibraryStatus
            mediaGrid
            legendModeRail
        }
        .navigationTitle(type.displayName)
        .navigationBarTitleDisplayMode(.inline)
    }

    private var legendModeRail: some View {
        LegendSocialCreationModeRail(selection: $type)
    }

    @ViewBuilder
    private var selectionPreview: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(type.mediaSelectionTitle)
                        .font(.headline.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(type.mediaSelectionHint)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: LegendNextSpacing.sm)
                Button {
                    stage = .camera
                } label: {
                    Image(systemName: "camera.fill")
                        .font(.title3.weight(.bold))
                        .frame(width: 44, height: 44)
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.navy)
                .background(LegendNextColor.gold, in: Circle())
                .accessibilityLabel("Open camera for \(type.displayName.lowercased())")
            }

            if selectedMedia.isEmpty {
                RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous)
                    .fill(LegendNextColor.surfaceInset)
                    .frame(height: type == .reel || type == .story ? 168 : 128)
                    .overlay {
                        Label("Choose media below", systemImage: "photo.stack")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
            } else {
                ScrollView(.horizontal) {
                    LazyHStack(spacing: LegendNextSpacing.xs) {
                        ForEach(selectedMedia) { media in
                            LegendSocialMediaPreview(media: media) {
                                remove(media)
                            }
                            .frame(width: 128, height: 128)
                        }
                    }
                    .padding(.vertical, 2)
                }
                .scrollIndicators(.hidden)
            }
        }
        .padding(LegendNextSpacing.md)
    }

    private var mediaGrid: some View {
        ScrollView {
            LazyVGrid(
                columns: Array(
                    repeating: GridItem(.flexible(), spacing: 2),
                    count: 3),
                spacing: 2
            ) {
                ForEach(photoLibrary.visibleAssets) { asset in
                    LegendPhotoLibraryThumbnail(
                        asset: asset,
                        photoLibrary: photoLibrary,
                        isSelected: selectedAssetIdentifiers.contains(asset.id),
                        isEligible: type.accepts(asset),
                        select: { select(asset) })
                    .aspectRatio(1, contentMode: .fit)
                }
            }
            .padding(.horizontal, 2)

            if photoLibrary.canLoadMore {
                Button("Show more") {
                    photoLibrary.loadNextPage()
                }
                .buttonStyle(LegendButtonStyle(kind: .secondary))
                .padding(LegendNextSpacing.md)
            }
        }
        .scrollIndicators(.hidden)
    }

    @ViewBuilder
    private var photoLibraryStatus: some View {
        switch photoLibrary.status {
        case .authorized:
            photoLibraryNotice(
                "Full photo library access is enabled.",
                symbol: "checkmark.shield.fill",
                color: LegendNextColor.success)

        case .limited:
            HStack(spacing: LegendNextSpacing.sm) {
                photoLibraryNotice(
                    "Selected Photos access is enabled.",
                    symbol: "photo.on.rectangle.angled",
                    color: LegendNextColor.information)
                Spacer(minLength: LegendNextSpacing.xs)
                Button("Select More Photos") {
                    photoLibrary.presentLimitedLibraryPicker()
                }
                .font(.caption.weight(.semibold))
                .buttonStyle(.bordered)
                .accessibilityLabel("Select more photos for Legend")
            }

        case .notDetermined:
            HStack(spacing: LegendNextSpacing.sm) {
                photoLibraryNotice(
                    "Grant photo access to choose media from your library.",
                    symbol: "photo.badge.plus",
                    color: LegendNextColor.information)
                Spacer(minLength: LegendNextSpacing.xs)
                Button("Manage access") {
                    photoLibrary.requestAccess()
                }
                .font(.caption.weight(.semibold))
                .buttonStyle(.bordered)
            }

        case .denied:
            HStack(spacing: LegendNextSpacing.sm) {
                photoLibraryNotice(
                    "Photo access is off. Enable it in Settings to choose media.",
                    symbol: "photo.slash",
                    color: LegendNextColor.warning)
                Spacer(minLength: LegendNextSpacing.xs)
                Button("Open Settings") {
                    photoLibrary.openSettings()
                }
                .font(.caption.weight(.semibold))
                .buttonStyle(.bordered)
            }

        case .restricted:
            photoLibraryNotice(
                "Photo access is restricted on this device.",
                symbol: "lock.fill",
                color: LegendNextColor.warning)
        }
    }

    private func photoLibraryNotice(
        _ message: String,
        symbol: String,
        color: Color
    ) -> some View {
        Label(message, systemImage: symbol)
            .font(.caption)
            .foregroundStyle(color)
            .fixedSize(horizontal: false, vertical: true)
            .accessibilityLabel(message)
    }

    private var metadataContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                selectionPreview

                LegendNextSurface(style: .elevated) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        Text(type == .story ? "Story text" : "Caption")
                            .font(.headline.weight(.bold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                        TextEditor(text: $caption)
                            .font(LegendNextTypography.body)
                            .frame(minHeight: 118)
                            .accessibilityLabel("\(type.displayName) caption")
                    }
                }

                if !selectedMedia.isEmpty {
                    TextField(
                        "Describe this media for accessibility",
                        text: $accessibilityText,
                        axis: .vertical)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Media description")
                }

                musicSelection

                LegendNextSurface(style: .elevated) {
                    Label("Shared only with people authorized by Legend. Visibility is set by the server.", systemImage: "checkmark.shield.fill")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                if let failure = social.actionFailure {
                    Text(failure.message)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.danger)
                }
            }
            .padding(LegendNextSpacing.md)
        }
        .scrollIndicators(.hidden)
        .navigationTitle("Finish \(type.displayName)")
        .navigationBarTitleDisplayMode(.inline)
    }

    private var musicSelection: some View {
        Button {
            stage = .music
        } label: {
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: "music.note")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 40, height: 40)
                    .background(LegendNextColor.navy, in: Circle())
                VStack(alignment: .leading, spacing: 2) {
                    Text(selectedMusic?.track.trackTitle ?? "Add licensed music")
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(selectedMusic.map { "\($0.track.trackTitle) · \($0.track.artistName) · linked from Spotify" } ?? "Search Spotify and link a track to this post.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
                Image(systemName: "chevron.right")
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
            .padding(LegendNextSpacing.sm)
            .background(LegendNextColor.surfaceElevated, in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(selectedMedia.isEmpty)
    }

    private var selectedAssetIdentifiers: Set<String> {
        Set(selectedMedia.compactMap(\.sourceAssetIdentifier))
    }

    private var hasValidSelection: Bool {
        !selectedMedia.isEmpty &&
            selectedMedia.count <= type.maximumMediaItems &&
            selectedMedia.allSatisfy(type.accepts)
    }

    private func primaryAction() {
        switch stage {
        case .library:
            stage = .metadata
        case .metadata:
            publish()
        default:
            break
        }
    }

    private func select(_ asset: LegendPhotoLibraryAsset) {
        guard type.accepts(asset) else {
            mediaSelectionError = type.requiresVideo
                ? "Reels use one video. Choose a video to continue."
                : "This media is not available for the selected format."
            return
        }

        if let existing = selectedMedia.first(where: { $0.sourceAssetIdentifier == asset.id }) {
            remove(existing)
            return
        }

        stage = .preparingMedia
        mediaSelectionError = nil
        Task {
            do {
                let media = try await photoLibrary.loadDraft(for: asset)
                guard type.accepts(media) else {
                    media.discardTemporaryFile()
                    mediaSelectionError = "This media is not available for the selected format."
                    stage = .library
                    return
                }

                if type.maximumMediaItems == 1 {
                    let previous = selectedMedia
                    selectedMedia = [media]
                    previous.forEach { $0.discardTemporaryFile() }
                } else {
                    selectedMedia.append(media)
                }
            } catch {
                mediaSelectionError = "Legend could not prepare this media. The draft was kept intact."
            }
            stage = .library
        }
    }

    private func remove(_ media: LegendSocialMediaDraft) {
        selectedMedia.removeAll { $0.id == media.id }
        media.discardTemporaryFile()
    }

    private func addCapturedMedia(_ result: Result<LegendSocialMediaDraft, Error>) {
        switch result {
        case .success(let media):
            guard type.accepts(media) else {
                media.discardTemporaryFile()
                mediaSelectionError = type.requiresVideo
                    ? "A reel can only contain a video."
                    : "The captured media is not available for this update."
                return
            }
            let previous = selectedMedia
            selectedMedia = [media]
            previous.forEach { $0.discardTemporaryFile() }
            mediaSelectionError = nil

        case .failure:
            mediaSelectionError = "The captured media could not be prepared. Your draft was kept intact."
        }
    }

    private func publish() {
        let request = MobileSocialPublishRequest(
            contentType: type,
            body: caption,
            files: selectedMedia.map(\.multipartFile),
            accessibilityText: normalizedAccessibilityText,
            music: selectedMusic?.selection)

        if social.beginPublication(request) {
            ownsMediaAfterDismissal = true
            stage = .handedOff
            dismiss()
        }
    }

    private var normalizedAccessibilityText: String? {
        let value = accessibilityText.trimmingCharacters(
            in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private func discardTemporaryMedia() {
        selectedMedia.forEach { $0.discardTemporaryFile() }
    }

    private func validateSelection(for updatedType: MobileSocialContentType) {
        guard !selectedMedia.isEmpty else {
            mediaSelectionError = nil
            return
        }

        if !selectedMedia.allSatisfy(updatedType.accepts) {
            mediaSelectionError = "Choose media supported by \(updatedType.displayName). Your existing selection remains available when you switch back."
        } else if selectedMedia.count > updatedType.maximumMediaItems {
            mediaSelectionError = "\(updatedType.displayName) accepts up to \(updatedType.maximumMediaItems) item\(updatedType.maximumMediaItems == 1 ? "" : "s"). Your existing selection remains available when you switch back."
        } else {
            mediaSelectionError = nil
        }
    }

    private func cancel() {
        discardTemporaryMedia()
        dismiss()
    }
}

private struct LegendSocialMusicDraft: Equatable {
    let track: MobileSocialMusicTrack
    let selection: MobileSocialMusicSelection
}

private struct LegendSocialMusicSelectionSheet: View {
    @ObservedObject var social: MobileSocialStore
    let selection: LegendSocialMusicDraft?
    let save: (LegendSocialMusicDraft) -> Void
    let cancel: () -> Void

    @State private var query = ""
    @State private var tracks: [MobileSocialMusicTrack] = []
    @State private var selectedTrack: MobileSocialMusicTrack?
    @State private var trimStart = 0.0
    @State private var trimEnd = 0.0
    @State private var musicVolume = 1.0
    @State private var originalAudioVolume = 1.0
    @State private var isSearching = false
    @State private var previewPlayer: AVPlayer?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    Text("Search Spotify and link a track to this Legend post. Spotify audio remains on Spotify and is not mixed into the uploaded media.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)

                    HStack(spacing: LegendNextSpacing.xs) {
                        TextField("Search music", text: $query)
                            .textFieldStyle(.roundedBorder)
                            .submitLabel(.search)
                            .onSubmit { search() }
                            .accessibilityLabel("Search Spotify music")

                        Button("Search", action: search)
                            .buttonStyle(LegendButtonStyle(kind: .secondary))
                            .disabled(query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isSearching)
                    }

                    if isSearching {
                        HStack(spacing: LegendNextSpacing.xs) {
                            ProgressView()
                            Text("Searching Spotify…")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                        }
                    }

                    if !tracks.isEmpty {
                        VStack(spacing: LegendNextSpacing.xs) {
                            ForEach(tracks) { track in
                                Button {
                                    choose(track)
                                } label: {
                                    HStack(spacing: LegendNextSpacing.sm) {
                                        Image(systemName: selectedTrack?.id == track.id ? "checkmark.circle.fill" : "music.note")
                                            .foregroundStyle(selectedTrack?.id == track.id ? LegendNextColor.success : LegendNextColor.gold)
                                            .frame(width: 28)
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text(track.trackTitle)
                                                .font(.subheadline.weight(.semibold))
                                                .foregroundStyle(LegendNextColor.textPrimary)
                                                .lineLimit(1)
                                            Text("\(track.artistName) · \(duration(track.trackDurationSeconds))")
                                                .font(LegendNextTypography.supporting)
                                                .foregroundStyle(LegendNextColor.textSecondary)
                                                .lineLimit(1)
                                        }
                                        Spacer(minLength: LegendNextSpacing.xs)
                                        if track.previewURL != nil {
                                            Button {
                                                preview(track)
                                            } label: {
                                                Image(systemName: "play.circle")
                                                    .font(.title3)
                                            }
                                            .buttonStyle(.plain)
                                            .foregroundStyle(LegendNextColor.navy)
                                            .accessibilityLabel("Preview \(track.trackTitle)")
                                        }
                                    }
                                    .padding(LegendNextSpacing.sm)
                                    .background(
                                        selectedTrack?.id == track.id ? LegendNextColor.gold.opacity(0.12) : LegendNextColor.surfaceInset,
                                        in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }

                    if let selectedTrack {
                        mixingControls(for: selectedTrack)
                    }
                }
                .padding(LegendNextSpacing.md)
            }
            .scrollIndicators(.hidden)
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Add music")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        stopPreview()
                        cancel()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add") {
                        guard let selectedTrack else { return }
                        save(LegendSocialMusicDraft(
                            track: selectedTrack,
                            selection: MobileSocialMusicSelection(
                                providerID: selectedTrack.providerID,
                                providerTrackID: selectedTrack.providerTrackID,
                                trimStartSeconds: Decimal(trimStart),
                                trimEndSeconds: Decimal(trimEnd),
                                musicVolume: Decimal(musicVolume),
                                originalAudioVolume: Decimal(originalAudioVolume))))
                        stopPreview()
                        cancel()
                    }
                    .disabled(selectedTrack == nil || trimEnd <= trimStart)
                }
            }
        }
        .onAppear {
            if let selection {
                selectedTrack = selection.track
                trimStart = NSDecimalNumber(decimal: selection.selection.trimStartSeconds).doubleValue
                trimEnd = NSDecimalNumber(decimal: selection.selection.trimEndSeconds).doubleValue
                musicVolume = NSDecimalNumber(decimal: selection.selection.musicVolume).doubleValue
                originalAudioVolume = NSDecimalNumber(decimal: selection.selection.originalAudioVolume).doubleValue
            }
        }
        .onDisappear(perform: stopPreview)
    }

    private func mixingControls(for track: MobileSocialMusicTrack) -> some View {
        let totalDuration = max(NSDecimalNumber(decimal: track.trackDurationSeconds).doubleValue, 0.01)
        return LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                Text("Clip and audio mix")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                musicSlider("Clip begins", value: $trimStart, range: 0...max(0, trimEnd - 0.01), detail: duration(Decimal(trimStart)))
                musicSlider("Clip ends", value: $trimEnd, range: min(totalDuration, trimStart + 0.01)...totalDuration, detail: duration(Decimal(trimEnd)))
                musicSlider("Music volume", value: $musicVolume, range: 0...1, detail: percentage(musicVolume))
                musicSlider("Original audio", value: $originalAudioVolume, range: 0...1, detail: percentage(originalAudioVolume))
            }
        }
    }

    private func musicSlider(
        _ title: String,
        value: Binding<Double>,
        range: ClosedRange<Double>,
        detail: String
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            HStack {
                Text(title)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                Spacer()
                Text(detail)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(LegendNextColor.textPrimary)
            }
            Slider(value: value, in: range)
                .tint(LegendNextColor.gold)
        }
    }

    private func search() {
        let value = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, !isSearching else { return }
        isSearching = true
        Task {
            tracks = await social.searchMusic(value)
            isSearching = false
        }
    }

    private func choose(_ track: MobileSocialMusicTrack) {
        selectedTrack = track
        trimStart = 0
        trimEnd = max(NSDecimalNumber(decimal: track.trackDurationSeconds).doubleValue, 0.01)
        musicVolume = 1
        originalAudioVolume = 1
    }

    private func preview(_ track: MobileSocialMusicTrack) {
        guard let url = track.previewURL else { return }
        if previewPlayer?.currentItem?.asset as? AVURLAsset == AVURLAsset(url: url) {
            previewPlayer?.seek(to: .zero)
            previewPlayer?.play()
            return
        }

        let player = AVPlayer(url: url)
        previewPlayer = player
        player.play()
    }

    private func stopPreview() {
        previewPlayer?.pause()
        previewPlayer = nil
    }

    private func duration(_ seconds: Decimal) -> String {
        let total = Int(NSDecimalNumber(decimal: seconds).doubleValue.rounded())
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    private func percentage(_ value: Double) -> String {
        "\(Int((value * 100).rounded()))%"
    }
}

private struct LegendSocialMediaPreview: View {
    let media: LegendSocialMediaDraft
    let remove: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
            preview
                .frame(width: 92, height: 92)
                .clipShape(
                    RoundedRectangle(
                        cornerRadius: LegendNextRadius.control,
                        style: .continuous))

            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                Text(media.kindDescription)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
                Text(media.fileName)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)

                Button("Remove", role: .destructive, action: remove)
                    .font(.caption.weight(.semibold))
            }

            Spacer(minLength: 0)
        }
        .padding(LegendNextSpacing.xs)
        .background(
            Color(uiColor: .secondarySystemBackground),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Selected \(media.kindDescription): \(media.fileName)")
    }

    @ViewBuilder
    private var preview: some View {
        switch media {
        case .image(_, let data, _, _, _):
            if let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                mediaPlaceholder
            }

        case .video(_, let fileURL, _, _, _):
            VideoPlayer(player: AVPlayer(url: fileURL))
        }
    }

    private var mediaPlaceholder: some View {
        Image(systemName: "photo")
            .font(.title2)
            .foregroundStyle(LegendNextColor.textSecondary)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(uiColor: .tertiarySystemBackground))
    }
}

private enum LegendSocialMediaDraft: Identifiable {
    case image(UUID, Data, String, String, String?)
    case video(UUID, URL, String, String, String?)

    var id: UUID {
        switch self {
        case .image(let id, _, _, _, _), .video(let id, _, _, _, _):
            id
        }
    }

    var fileName: String {
        switch self {
        case .image(_, _, _, let fileName, _), .video(_, _, _, let fileName, _):
            fileName
        }
    }

    var kindDescription: String {
        switch self {
        case .image:
            "Photo"
        case .video:
            "Video"
        }
    }

    var isVideo: Bool {
        if case .video = self {
            return true
        }
        return false
    }

    var sourceAssetIdentifier: String? {
        switch self {
        case .image(_, _, _, _, let identifier), .video(_, _, _, _, let identifier):
            identifier
        }
    }

    var multipartFile: MultipartFormFile {
        switch self {
        case .image(_, let data, let mimeType, let fileName, _):
            MultipartFormFile(
                fieldName: "files",
                fileName: fileName,
                mimeType: mimeType,
                data: data)
        case .video(_, let fileURL, let mimeType, let fileName, _):
            MultipartFormFile(
                fieldName: "files",
                fileName: fileName,
                mimeType: mimeType,
                fileURL: fileURL)
        }
    }

    func discardTemporaryFile() {
        guard case .video(_, let fileURL, _, _, _) = self else { return }
        try? FileManager.default.removeItem(at: fileURL)
    }

    static func image(from image: UIImage) throws -> LegendSocialMediaDraft {
        guard let data = image.jpegData(compressionQuality: 0.9) else {
            throw LegendSocialMediaLoadingError.unavailable
        }
        return .image(UUID(), data, "image/jpeg", "legend-camera.jpg", nil)
    }

    static func video(from sourceURL: URL) throws -> LegendSocialMediaDraft {
        let representation = videoRepresentation(for: sourceURL)
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-camera-\(UUID().uuidString)")
            .appendingPathExtension(sourceURL.pathExtension)
        try FileManager.default.copyItem(at: sourceURL, to: destination)
        return .video(UUID(), destination, representation.mimeType, representation.fileName, nil)
    }

    static func image(
        data: Data,
        uniformTypeIdentifier: String?,
        sourceAssetIdentifier: String
    ) -> LegendSocialMediaDraft {
        let type = uniformTypeIdentifier.flatMap(UTType.init)
        let representation = imageRepresentation(for: type)
        return .image(
            UUID(),
            data,
            representation.mimeType,
            representation.fileName,
            sourceAssetIdentifier)
    }

    static func video(
        from sourceURL: URL,
        sourceAssetIdentifier: String
    ) throws -> LegendSocialMediaDraft {
        let representation = videoRepresentation(for: sourceURL)
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-library-\(UUID().uuidString)")
            .appendingPathExtension(sourceURL.pathExtension)
        try FileManager.default.copyItem(at: sourceURL, to: destination)
        return .video(
            UUID(),
            destination,
            representation.mimeType,
            representation.fileName,
            sourceAssetIdentifier)
    }

    private static func imageRepresentation(
        for type: UTType?
    ) -> (mimeType: String, fileName: String) {
        if type?.conforms(to: .png) == true { return ("image/png", "legend-image.png") }
        if type?.conforms(to: .heic) == true { return ("image/heic", "legend-image.heic") }
        if type?.conforms(to: .heif) == true { return ("image/heif", "legend-image.heif") }
        if type?.conforms(to: .webP) == true { return ("image/webp", "legend-image.webp") }
        return ("image/jpeg", "legend-image.jpg")
    }

    private static func videoRepresentation(
        for fileURL: URL
    ) -> (mimeType: String, fileName: String) {
        let extensionValue = fileURL.pathExtension.lowercased()
        switch extensionValue {
        case "mov": return ("video/quicktime", "legend-video.mov")
        case "webm": return ("video/webm", "legend-video.webm")
        default: return ("video/mp4", "legend-video.mp4")
        }
    }
}

private enum LegendSocialMediaLoadingError: Error {
    case unavailable
    case unsupported
}

private struct LegendSocialCameraCapture: UIViewControllerRepresentable {
    let capturesVideo: Bool
    let captured: (Result<LegendSocialMediaDraft, Error>) -> Void
    let cancelled: () -> Void

    func makeUIViewController(context: Context) -> LegendSocialCameraViewController {
        LegendSocialCameraViewController(
            capturesVideo: capturesVideo,
            captured: captured,
            cancelled: cancelled)
    }

    func updateUIViewController(
        _ controller: LegendSocialCameraViewController,
        context: Context
    ) {}
}

private enum LegendSocialCameraError: LocalizedError {
    case unavailable
    case permissionDenied
    case captureFailed

    var errorDescription: String? {
        switch self {
        case .unavailable:
            "Camera capture is unavailable on this device."
        case .permissionDenied:
            "Camera access is required to capture a new update."
        case .captureFailed:
            "Legend could not complete that capture."
        }
    }
}

private final class LegendSocialCameraViewController: UIViewController {
    private let capturesVideo: Bool
    private let captured: (Result<LegendSocialMediaDraft, Error>) -> Void
    private let cancelled: () -> Void

    private let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "com.mylegnd.legend.social.camera")
    private let photoOutput = AVCapturePhotoOutput()
    private let movieOutput = AVCaptureMovieFileOutput()
    private let previewLayer = AVCaptureVideoPreviewLayer()
    private let captureButton = UIButton(type: .custom)
    private let cameraButton = UIButton(type: .system)
    private let flashButton = UIButton(type: .system)
    private let closeButton = UIButton(type: .system)
    private let libraryButton = UIButton(type: .system)
    private let statusLabel = UILabel()

    private var videoInput: AVCaptureDeviceInput?
    private var configured = false
    private var completed = false
    private var isCancellingRecording = false
    private var flashEnabled = false
    private var recordingURL: URL?

    init(
        capturesVideo: Bool,
        captured: @escaping (Result<LegendSocialMediaDraft, Error>) -> Void,
        cancelled: @escaping () -> Void
    ) {
        self.capturesVideo = capturesVideo
        self.captured = captured
        self.cancelled = cancelled
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        previewLayer.videoGravity = .resizeAspectFill
        view.layer.insertSublayer(previewLayer, at: 0)
        configureControls()
        requestAccessAndConfigureSession()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer.frame = view.bounds
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        startSessionIfNeeded()
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        sessionQueue.async { [weak self] in
            self?.session.stopRunning()
        }
    }

    private func configureControls() {
        let controlTint = UIColor.white
        let chrome = UIColor.black.withAlphaComponent(0.42)

        configureIconButton(closeButton, symbol: "xmark", tint: controlTint, background: chrome)
        configureIconButton(flashButton, symbol: "bolt.slash.fill", tint: controlTint, background: chrome)
        configureIconButton(cameraButton, symbol: "camera.rotate.fill", tint: controlTint, background: chrome)
        configureIconButton(libraryButton, symbol: "photo.on.rectangle", tint: controlTint, background: chrome)

        closeButton.addTarget(self, action: #selector(close), for: .touchUpInside)
        flashButton.addTarget(self, action: #selector(toggleFlash), for: .touchUpInside)
        cameraButton.addTarget(self, action: #selector(switchCamera), for: .touchUpInside)
        libraryButton.addTarget(self, action: #selector(returnToLibrary), for: .touchUpInside)

        captureButton.translatesAutoresizingMaskIntoConstraints = false
        captureButton.backgroundColor = .white
        captureButton.layer.cornerRadius = 36
        captureButton.layer.borderColor = UIColor.white.withAlphaComponent(0.55).cgColor
        captureButton.layer.borderWidth = 6
        captureButton.accessibilityLabel = capturesVideo ? "Start video recording" : "Capture photo"
        captureButton.addTarget(self, action: #selector(capture), for: .touchUpInside)

        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        statusLabel.font = .preferredFont(forTextStyle: .caption1)
        statusLabel.textColor = .white
        statusLabel.textAlignment = .center
        statusLabel.adjustsFontForContentSizeCategory = true
        statusLabel.numberOfLines = 2

        [closeButton, flashButton, cameraButton, libraryButton, captureButton, statusLabel]
            .forEach(view.addSubview)

        NSLayoutConstraint.activate([
            closeButton.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 20),
            closeButton.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 14),
            flashButton.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            flashButton.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 14),
            cameraButton.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -20),
            cameraButton.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 14),
            libraryButton.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 20),
            libraryButton.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -24),
            captureButton.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            captureButton.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -20),
            captureButton.widthAnchor.constraint(equalToConstant: 72),
            captureButton.heightAnchor.constraint(equalTo: captureButton.widthAnchor),
            statusLabel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 32),
            statusLabel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -32),
            statusLabel.bottomAnchor.constraint(equalTo: captureButton.topAnchor, constant: -18)
        ])
        setStatus(capturesVideo ? "Video capture" : "Photo capture")
    }

    private func configureIconButton(
        _ button: UIButton,
        symbol: String,
        tint: UIColor,
        background: UIColor
    ) {
        button.translatesAutoresizingMaskIntoConstraints = false
        button.setImage(UIImage(systemName: symbol), for: .normal)
        button.tintColor = tint
        button.backgroundColor = background
        button.layer.cornerRadius = 23
        button.widthAnchor.constraint(equalToConstant: 46).isActive = true
        button.heightAnchor.constraint(equalTo: button.widthAnchor).isActive = true
    }

    private func requestAccessAndConfigureSession() {
        requestCameraAccess { [weak self] granted in
            guard let self else { return }
            guard granted else {
                self.setStatus(LegendSocialCameraError.permissionDenied.localizedDescription)
                self.captureButton.isEnabled = false
                return
            }

            if self.capturesVideo {
                self.requestMicrophoneAccess { _ in
                    self.configureSession()
                }
            } else {
                self.configureSession()
            }
        }
    }

    private func requestCameraAccess(_ completion: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            completion(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video, completionHandler: completion)
        default:
            completion(false)
        }
    }

    private func requestMicrophoneAccess(_ completion: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            completion(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio, completionHandler: completion)
        default:
            setStatus("Video will be captured without audio.")
            completion(false)
        }
    }

    private func configureSession() {
        sessionQueue.async { [weak self] in
            guard let self, !self.configured else { return }
            self.session.beginConfiguration()
            self.session.sessionPreset = .high

            guard self.configureVideoInput(position: .back) else {
                self.session.commitConfiguration()
                DispatchQueue.main.async {
                    self.setStatus(LegendSocialCameraError.unavailable.localizedDescription)
                    self.captureButton.isEnabled = false
                }
                return
            }

            if self.capturesVideo {
                if AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
                   let microphone = AVCaptureDevice.default(for: .audio),
                   let audioInput = try? AVCaptureDeviceInput(device: microphone),
                   self.session.canAddInput(audioInput) {
                    self.session.addInput(audioInput)
                }
                if self.session.canAddOutput(self.movieOutput) {
                    self.session.addOutput(self.movieOutput)
                    self.movieOutput.maxRecordedDuration = CMTime(seconds: 60, preferredTimescale: 600)
                }
            } else if self.session.canAddOutput(self.photoOutput) {
                self.session.addOutput(self.photoOutput)
            }

            self.session.commitConfiguration()
            self.configured = true
            DispatchQueue.main.async {
                self.previewLayer.session = self.session
                self.setStatus(self.capturesVideo ? "Video capture · up to 60 seconds" : "Photo capture")
                self.startSessionIfNeeded()
            }
        }
    }

    private func configureVideoInput(position: AVCaptureDevice.Position) -> Bool {
        let device = AVCaptureDevice.default(
            .builtInWideAngleCamera,
            for: .video,
            position: position)
        guard let device, let input = try? AVCaptureDeviceInput(device: device), session.canAddInput(input) else {
            return false
        }
        session.addInput(input)
        videoInput = input
        return true
    }

    private func startSessionIfNeeded() {
        sessionQueue.async { [weak self] in
            guard let self, self.configured, !self.session.isRunning else { return }
            self.session.startRunning()
        }
    }

    @objc private func close() {
        guard !completed, !isCancellingRecording else { return }
        if movieOutput.isRecording {
            isCancellingRecording = true
            movieOutput.stopRecording()
            return
        }
        completed = true
        cancelled()
    }

    @objc private func returnToLibrary() {
        close()
    }

    @objc private func capture() {
        guard configured, !completed else { return }
        if capturesVideo {
            captureVideo()
        } else {
            capturePhoto()
        }
    }

    private func capturePhoto() {
        let settings = AVCapturePhotoSettings()
        if photoOutput.supportedFlashModes.contains(flashEnabled ? .on : .off) {
            settings.flashMode = flashEnabled ? .on : .off
        }
        captureButton.isEnabled = false
        photoOutput.capturePhoto(with: settings, delegate: self)
    }

    private func captureVideo() {
        if movieOutput.isRecording {
            movieOutput.stopRecording()
            setRecordingAppearance(false)
            return
        }

        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-camera-\(UUID().uuidString)")
            .appendingPathExtension("mov")
        recordingURL = destination
        movieOutput.startRecording(to: destination, recordingDelegate: self)
        setRecordingAppearance(true)
    }

    @objc private func toggleFlash() {
        flashEnabled.toggle()
        let symbol = flashEnabled ? "bolt.fill" : "bolt.slash.fill"
        flashButton.setImage(UIImage(systemName: symbol), for: .normal)

        guard capturesVideo, let device = videoInput?.device, device.hasTorch else { return }
        sessionQueue.async {
            do {
                try device.lockForConfiguration()
                device.torchMode = self.flashEnabled ? .on : .off
                device.unlockForConfiguration()
            } catch {
                DispatchQueue.main.async { self.flashEnabled = false }
            }
        }
    }

    @objc private func switchCamera() {
        sessionQueue.async { [weak self] in
            guard let self, !self.movieOutput.isRecording,
                  let currentInput = self.videoInput else { return }
            let position: AVCaptureDevice.Position = currentInput.device.position == .back ? .front : .back
            self.session.beginConfiguration()
            self.session.removeInput(currentInput)
            self.videoInput = nil
            if !self.configureVideoInput(position: position) {
                self.session.addInput(currentInput)
                self.videoInput = currentInput
            }
            self.session.commitConfiguration()
        }
    }

    private func setRecordingAppearance(_ recording: Bool) {
        captureButton.backgroundColor = recording ? .systemRed : .white
        captureButton.accessibilityLabel = recording ? "Stop video recording" : "Start video recording"
        setStatus(recording ? "Recording · tap to finish" : "Video capture · up to 60 seconds")
    }

    private func setStatus(_ message: String) {
        DispatchQueue.main.async { [weak self] in
            self?.statusLabel.text = message
        }
    }

    private func finish(with result: Result<LegendSocialMediaDraft, Error>) {
        guard !completed else { return }
        completed = true
        sessionQueue.async { [weak self] in
            self?.session.stopRunning()
        }
        DispatchQueue.main.async { [captured] in
            captured(result)
        }
    }
}

extension LegendSocialCameraViewController: AVCapturePhotoCaptureDelegate, AVCaptureFileOutputRecordingDelegate {
    func photoOutput(
        _ output: AVCapturePhotoOutput,
        didFinishProcessingPhoto photo: AVCapturePhoto,
        error: Error?
    ) {
        if let error {
            finish(with: .failure(error))
            return
        }

        guard let data = photo.fileDataRepresentation() else {
            finish(with: .failure(LegendSocialCameraError.captureFailed))
            return
        }

        guard let image = UIImage(data: data) else {
            finish(with: .failure(LegendSocialCameraError.captureFailed))
            return
        }

        do {
            finish(with: .success(try LegendSocialMediaDraft.image(from: image)))
        } catch {
            finish(with: .failure(error))
        }
    }

    func fileOutput(
        _ output: AVCaptureFileOutput,
        didFinishRecordingTo outputFileURL: URL,
        from connections: [AVCaptureConnection],
        error: Error?
    ) {
        defer {
            recordingURL = nil
        }
        if isCancellingRecording {
            try? FileManager.default.removeItem(at: outputFileURL)
            completed = true
            DispatchQueue.main.async { [cancelled] in
                cancelled()
            }
            return
        }
        if let error {
            try? FileManager.default.removeItem(at: outputFileURL)
            finish(with: .failure(error))
            return
        }

        do {
            let draft = try LegendSocialMediaDraft.video(from: outputFileURL)
            try? FileManager.default.removeItem(at: outputFileURL)
            finish(with: .success(draft))
        } catch {
            try? FileManager.default.removeItem(at: outputFileURL)
            finish(with: .failure(error))
        }
    }
}

private extension MobileSocialContentType {
    func accepts(_ media: LegendSocialMediaDraft) -> Bool {
        media.isVideo ? acceptsVideos : acceptsImages
    }

    func accepts(_ asset: LegendPhotoLibraryAsset) -> Bool {
        asset.isVideo ? acceptsVideos : acceptsImages
    }
}

struct LegendPhotoLibraryAsset: Identifiable, Equatable {
    let id: String
    let isVideo: Bool
    let duration: TimeInterval

    init(_ asset: PHAsset) {
        id = asset.localIdentifier
        isVideo = asset.mediaType == .video
        duration = asset.duration
    }
}

private struct LegendPhotoLibraryThumbnail: View {
    let asset: LegendPhotoLibraryAsset
    @ObservedObject var photoLibrary: LegendPhotoLibraryAccess
    let isSelected: Bool
    let isEligible: Bool
    let select: () -> Void

    @State private var image: UIImage?
    @State private var requestID: PHImageRequestID = PHInvalidImageRequestID

    var body: some View {
        Button(action: select) {
            ZStack(alignment: .topTrailing) {
                thumbnail
                    .overlay {
                        if !isEligible {
                            Color.black.opacity(0.45)
                        }
                    }

                if asset.isVideo {
                    Text(durationLabel)
                        .font(.caption2.monospacedDigit().weight(.bold))
                        .padding(.horizontal, 5)
                        .padding(.vertical, 3)
                        .foregroundStyle(.white)
                        .background(.black.opacity(0.7), in: Capsule())
                        .padding(6)
                }

                if isSelected {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.title3)
                        .foregroundStyle(LegendNextColor.gold)
                        .background(Color.black.opacity(0.62), in: Circle())
                        .padding(6)
                }
            }
        }
        .buttonStyle(.plain)
        .disabled(!isEligible)
        .accessibilityLabel("\(asset.isVideo ? "Video" : "Photo")\(isSelected ? ", selected" : "")")
        .accessibilityHint(isEligible ? "Double tap to select." : "Not available for this format.")
        .task(id: asset.id) {
            requestID = photoLibrary.thumbnailRequest(
                for: asset.id,
                targetSize: CGSize(width: 280, height: 280)) { image in
                    self.image = image
                }
        }
        .onDisappear {
            photoLibrary.cancelThumbnailRequest(requestID)
        }
    }

    @ViewBuilder
    private var thumbnail: some View {
        if let image {
            Image(uiImage: image)
                .resizable()
                .scaledToFill()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipped()
        } else {
            Rectangle()
                .fill(LegendNextColor.surfaceInset)
                .overlay { ProgressView().tint(LegendNextColor.gold) }
        }
    }

    private var durationLabel: String {
        let total = max(0, Int(asset.duration.rounded()))
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

enum LegendPhotoLibraryAuthorization: Equatable {
    case notDetermined
    case authorized
    case limited
    case denied
    case restricted

    init(_ status: PHAuthorizationStatus) {
        switch status {
        case .authorized:
            self = .authorized
        case .limited:
            self = .limited
        case .denied:
            self = .denied
        case .restricted:
            self = .restricted
        case .notDetermined:
            self = .notDetermined
        @unknown default:
            self = .restricted
        }
    }
}

@MainActor
final class LegendPhotoLibraryAccess: ObservableObject {
    @Published private(set) var status: LegendPhotoLibraryAuthorization
    @Published private(set) var visibleAssets: [LegendPhotoLibraryAsset] = []
    @Published private(set) var canLoadMore = false

    private let imageManager = PHCachingImageManager()
    private var allAssets: [LegendPhotoLibraryAsset] = []
    private var loadedAssetCount = 0
    private let pageSize = 60

    init() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))
    }

    func refresh() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))

        guard status == .authorized || status == .limited else {
            allAssets = []
            visibleAssets = []
            loadedAssetCount = 0
            canLoadMore = false
            return
        }

        Task { [weak self] in
            let loaded = await Task.detached(priority: .userInitiated) {
                Self.fetchAssets()
            }.value
            guard let self else { return }
            allAssets = loaded
            loadedAssetCount = min(pageSize, loaded.count)
            visibleAssets = Array(loaded.prefix(loadedAssetCount))
            canLoadMore = loadedAssetCount < loaded.count
        }
    }

    func requestAccess() {
        guard status == .notDetermined else { return }

        PHPhotoLibrary.requestAuthorization(for: .readWrite) { [weak self] _ in
            Task { @MainActor in
                self?.refresh()
            }
        }
    }

    func presentLimitedLibraryPicker() {
        guard status == .limited,
              let presenter = activeViewController() else {
            return
        }

        PHPhotoLibrary.shared().presentLimitedLibraryPicker(from: presenter)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
            self?.refresh()
        }
    }

    func loadNextPage() {
        guard canLoadMore else { return }
        loadedAssetCount = min(loadedAssetCount + pageSize, allAssets.count)
        visibleAssets = Array(allAssets.prefix(loadedAssetCount))
        canLoadMore = loadedAssetCount < allAssets.count
    }

    func thumbnailRequest(
        for identifier: String,
        targetSize: CGSize,
        completion: @escaping (UIImage?) -> Void
    ) -> PHImageRequestID {
        guard let asset = asset(for: identifier) else {
            completion(nil)
            return PHInvalidImageRequestID
        }

        let options = PHImageRequestOptions()
        options.deliveryMode = .opportunistic
        options.resizeMode = .fast
        options.isNetworkAccessAllowed = true
        return imageManager.requestImage(
            for: asset,
            targetSize: targetSize,
            contentMode: .aspectFill,
            options: options) { image, _ in
                completion(image)
            }
    }

    func cancelThumbnailRequest(_ requestID: PHImageRequestID) {
        guard requestID != PHInvalidImageRequestID else { return }
        imageManager.cancelImageRequest(requestID)
    }

    fileprivate func loadDraft(
        for descriptor: LegendPhotoLibraryAsset
    ) async throws -> LegendSocialMediaDraft {
        guard let asset = asset(for: descriptor.id) else {
            throw LegendSocialMediaLoadingError.unavailable
        }

        if asset.mediaType == .image {
            let result = try await originalImageData(for: asset)
            return LegendSocialMediaDraft.image(
                data: result.data,
                uniformTypeIdentifier: result.uniformTypeIdentifier,
                sourceAssetIdentifier: descriptor.id)
        }

        guard asset.mediaType == .video else {
            throw LegendSocialMediaLoadingError.unsupported
        }
        let sourceURL = try await videoURL(for: asset)
        return try LegendSocialMediaDraft.video(
            from: sourceURL,
            sourceAssetIdentifier: descriptor.id)
    }

    func openSettings() {
        guard let settingsURL = URL(
            string: UIApplication.openSettingsURLString) else {
            return
        }

        UIApplication.shared.open(settingsURL)
    }

    private func activeViewController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
        let windows = scenes.flatMap { $0.windows }
        let root = windows.first(where: \ .isKeyWindow)?.rootViewController
            ?? windows.first?.rootViewController
        return visibleViewController(from: root)
    }

    private func visibleViewController(
        from controller: UIViewController?
    ) -> UIViewController? {
        if let presented = controller?.presentedViewController {
            return visibleViewController(from: presented)
        }
        if let navigation = controller as? UINavigationController {
            return visibleViewController(from: navigation.visibleViewController)
        }
        if let tab = controller as? UITabBarController {
            return visibleViewController(from: tab.selectedViewController)
        }
        return controller
    }

    private func asset(for identifier: String) -> PHAsset? {
        PHAsset.fetchAssets(withLocalIdentifiers: [identifier], options: nil)
            .firstObject
    }

    private func originalImageData(
        for asset: PHAsset
    ) async throws -> (data: Data, uniformTypeIdentifier: String?) {
        try await withCheckedThrowingContinuation { continuation in
            let options = PHImageRequestOptions()
            options.deliveryMode = .highQualityFormat
            options.resizeMode = .none
            options.isNetworkAccessAllowed = true
            imageManager.requestImageDataAndOrientation(
                for: asset,
                options: options) { data, uniformTypeIdentifier, _, info in
                    if (info?[PHImageCancelledKey] as? Bool) == true {
                        continuation.resume(throwing: LegendSocialMediaLoadingError.unavailable)
                    } else if let error = info?[PHImageErrorKey] as? Error {
                        continuation.resume(throwing: error)
                    } else if let data {
                        continuation.resume(returning: (data, uniformTypeIdentifier))
                    } else {
                        continuation.resume(throwing: LegendSocialMediaLoadingError.unavailable)
                    }
                }
        }
    }

    private func videoURL(for asset: PHAsset) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            let options = PHVideoRequestOptions()
            options.deliveryMode = .highQualityFormat
            options.isNetworkAccessAllowed = true
            imageManager.requestAVAsset(
                forVideo: asset,
                options: options) { avAsset, _, info in
                    if (info?[PHImageCancelledKey] as? Bool) == true {
                        continuation.resume(throwing: LegendSocialMediaLoadingError.unavailable)
                    } else if let error = info?[PHImageErrorKey] as? Error {
                        continuation.resume(throwing: error)
                    } else if let source = avAsset as? AVURLAsset {
                        continuation.resume(returning: source.url)
                    } else {
                        continuation.resume(throwing: LegendSocialMediaLoadingError.unavailable)
                    }
                }
        }
    }

    nonisolated private static func fetchAssets() -> [LegendPhotoLibraryAsset] {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = NSPredicate(
            format: "mediaType == %d OR mediaType == %d",
            PHAssetMediaType.image.rawValue,
            PHAssetMediaType.video.rawValue)

        let result = PHAsset.fetchAssets(with: options)
        var assets: [LegendPhotoLibraryAsset] = []
        assets.reserveCapacity(result.count)
        result.enumerateObjects { asset, _, _ in
            assets.append(LegendPhotoLibraryAsset(asset))
        }
        return assets
    }
}
