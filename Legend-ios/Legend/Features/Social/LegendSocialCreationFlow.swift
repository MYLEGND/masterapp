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
                LegendSocialCreationMenu(
                    select: { route = .composer($0) },
                    cancel: { route = nil })

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

private struct LegendSocialCreationMenu: View {
    let select: (MobileSocialContentType) -> Void
    let cancel: () -> Void

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text("Share with purpose")
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text("Choose the format that fits this moment. Your audience remains server-authorized.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: LegendNextSpacing.sm),
                        GridItem(.flexible(), spacing: LegendNextSpacing.sm)
                    ],
                    spacing: LegendNextSpacing.sm
                ) {
                    ForEach(MobileSocialContentType.allCases) { type in
                        Button {
                            select(type)
                        } label: {
                            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                                Image(systemName: type.systemImage)
                                    .font(.title3.weight(.bold))
                                    .foregroundStyle(LegendNextColor.gold)
                                    .frame(width: 42, height: 42)
                                    .background(
                                        LegendNextColor.navy,
                                        in: Circle())

                                Text(type.displayName)
                                    .font(.body.weight(.bold))
                                    .foregroundStyle(LegendNextColor.textPrimary)

                                Text(type.creationPrompt)
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .lineLimit(3)

                                Spacer(minLength: 0)
                            }
                            .frame(maxWidth: .infinity, minHeight: 168, alignment: .leading)
                            .padding(LegendNextSpacing.sm)
                            .background(
                                LegendNextColor.surfaceElevated,
                                in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous))
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Create \(type.displayName)")
                        .accessibilityHint(type.creationPrompt)
                    }
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.canvas)
            .navigationTitle("Create")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: cancel)
                }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }
}

struct LegendSocialComposer: View {
    let type: MobileSocialContentType
    @ObservedObject var social: MobileSocialStore
    let dismiss: () -> Void

    @StateObject private var photoLibrary = LegendPhotoLibraryAccess()
    @State private var caption = ""
    @State private var pickerItems: [PhotosPickerItem] = []
    @State private var selectedMedia: [LegendSocialMediaDraft] = []
    @State private var accessibilityText = ""
    @State private var mediaSelectionError: String?
    @State private var isPreparingMedia = false
    @State private var isPublishing = false
    @State private var isPresentingMusicPicker = false
    @State private var isPresentingCamera = false
    @State private var selectedMusic: LegendSocialMusicDraft?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Label(type.creationTitle, systemImage: type.systemImage)
                            .font(.title3.weight(.bold))
                            .foregroundStyle(.primary)
                        Text(type.creationPrompt)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text(type == .story ? "Add text" : "Caption")
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(LegendNextColor.textPrimary)
                            TextEditor(text: $caption)
                                .font(LegendNextTypography.body)
                                .frame(minHeight: 126)
                                .accessibilityLabel("\(type.displayName) caption")
                        }
                    }

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text(type.mediaSelectionTitle)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(LegendNextColor.textPrimary)
                            Text(type.mediaSelectionHint)
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)

                            HStack(spacing: LegendNextSpacing.sm) {
                                PhotosPicker(
                                    selection: $pickerItems,
                                    maxSelectionCount: type.maximumMediaItems,
                                    matching: pickerFilter,
                                    photoLibrary: .shared()
                                ) {
                                    Label("Library", systemImage: "photo.on.rectangle")
                                }
                                .buttonStyle(LegendButtonStyle(kind: .secondary))
                                .accessibilityLabel("Choose \(type.displayName.lowercased()) media from your library")

                                if UIImagePickerController.isSourceTypeAvailable(.camera) {
                                    Button {
                                        isPresentingCamera = true
                                    } label: {
                                        Label("Camera", systemImage: "camera")
                                    }
                                    .buttonStyle(LegendButtonStyle(kind: .secondary))
                                    .accessibilityLabel("Capture \(type.displayName.lowercased()) media with the camera")
                                }
                            }

                            if type.requiresVideo {
                                Label("Reels require one video.", systemImage: "video.fill")
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(LegendNextColor.information)
                            }
                        }
                    }

                    photoLibraryStatus

                    if isPreparingMedia {
                        HStack(spacing: LegendNextSpacing.xs) {
                            ProgressView()
                            Text("Preparing selected media…")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(.secondary)
                        }
                    }

                    if let mediaSelectionError {
                        Text(mediaSelectionError)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.danger)
                    }

                    selectedMediaContent

                    musicSelection

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                            Label("Audience", systemImage: "person.2.fill")
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(LegendNextColor.textPrimary)
                            Text("Your authorized Legend network")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                            Text("Visibility is determined by the server; this selection cannot expand access.")
                                .font(.caption)
                                .foregroundStyle(LegendNextColor.textSecondary)
                        }
                    }

                    if let failure = social.actionFailure {
                        Text(failure.message)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.danger)
                            .accessibilityLabel("Publishing error: \(failure.message)")
                    }
                }
                .padding(LegendNextSpacing.md)
            }
            .scrollIndicators(.hidden)
            .background(Color(uiColor: .systemBackground))
            .navigationTitle(type.displayName)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        dismiss()
                    }
                    .disabled(isPublishing)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(isPublishing ? "Publishing…" : "Publish") {
                        publish()
                    }
                    .disabled(!canPublish || isPublishing)
                }
            }
        }
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .sheet(isPresented: $isPresentingMusicPicker) {
            LegendSocialMusicSelectionSheet(
                social: social,
                selection: selectedMusic,
                save: { selectedMusic = $0 },
                cancel: { isPresentingMusicPicker = false })
        }
        .fullScreenCover(isPresented: $isPresentingCamera) {
            LegendSocialCameraCapture(
                capturesVideo: type.requiresVideo,
                captured: addCapturedMedia)
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
        .onChange(of: pickerItems) { _, items in
            Task {
                await importMedia(items)
            }
        }
        .onDisappear {
            if !isPublishing {
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

    private var pickerFilter: PHPickerFilter {
        type.acceptsImages
            ? .any(of: [.images, .videos])
            : .videos
    }

    private var musicSelection: some View {
        LegendNextSurface(style: .elevated) {
            HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                Image(systemName: "music.note")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 38, height: 38)
                    .background(
                        LegendNextColor.gold.opacity(0.14),
                        in: Circle())

                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text(selectedMusic?.track.trackTitle ?? "Add music")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                        .lineLimit(1)
                    Text(selectedMusic.map { "\($0.track.artistName) · clip and audio mix ready" } ?? "Available after selecting photo or video")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(1)
                }

                Spacer(minLength: LegendNextSpacing.xs)

                if selectedMusic != nil {
                    Button("Remove") {
                        selectedMusic = nil
                    }
                    .font(.caption.weight(.semibold))
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Remove selected music")
                }

                Button(selectedMusic == nil ? "Choose" : "Change") {
                    isPresentingMusicPicker = true
                }
                .font(.caption.weight(.bold))
                .buttonStyle(.bordered)
                .disabled(selectedMedia.isEmpty)
                .accessibilityLabel("Choose music for selected media")
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(selectedMusic.map { "Selected music: \($0.track.trackTitle) by \($0.track.artistName)" } ?? "No music selected")
    }

    @ViewBuilder
    private var selectedMediaContent: some View {
        if !selectedMedia.isEmpty {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                ForEach(selectedMedia) { media in
                    LegendSocialMediaPreview(media: media) {
                        remove(media)
                    }
                }

                TextField(
                    "Media description (optional)",
                    text: $accessibilityText,
                    axis: .vertical
                )
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("Media description")
            }
        }
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
                    "Choose media directly above, or manage photo access.",
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
                    "Photo access is off. You can still choose media through the system picker.",
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
                "Photo access is restricted on this device. You can still try the system picker.",
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

    @MainActor
    private func importMedia(_ items: [PhotosPickerItem]) async {
        guard !items.isEmpty else { return }

        isPreparingMedia = true
        mediaSelectionError = nil
        defer { isPreparingMedia = false }

        do {
            let prepared = try await items.asyncMap { item in
                try await LegendSocialMediaDraft.load(from: item)
            }
            guard prepared.allSatisfy(type.accepts) else {
                mediaSelectionError = type.requiresVideo
                    ? "A reel can only contain a video."
                    : "The selected media is not available for this update."
                pickerItems = []
                return
            }
            let previous = selectedMedia
            selectedMedia = prepared
            previous.forEach { $0.discardTemporaryFile() }
            pickerItems = []
        } catch {
            mediaSelectionError = "The selected media could not be prepared. Your current draft was kept intact."
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
        guard !isPublishing else { return }

        isPublishing = true
        let request = MobileSocialPublishRequest(
            contentType: type,
            body: caption,
            files: selectedMedia.map(\.multipartFile),
            accessibilityText: normalizedAccessibilityText,
            music: selectedMusic?.selection)

        Task {
            let succeeded = await social.publish(request)
            isPublishing = false

            if succeeded {
                dismiss()
            }
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
                    Text("Search the licensed Legend music catalog, preview a track, then set its clip and audio mix.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)

                    HStack(spacing: LegendNextSpacing.xs) {
                        TextField("Search music", text: $query)
                            .textFieldStyle(.roundedBorder)
                            .submitLabel(.search)
                            .onSubmit { search() }
                            .accessibilityLabel("Search licensed music")

                        Button("Search", action: search)
                            .buttonStyle(LegendButtonStyle(kind: .secondary))
                            .disabled(query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isSearching)
                    }

                    if isSearching {
                        HStack(spacing: LegendNextSpacing.xs) {
                            ProgressView()
                            Text("Searching licensed music…")
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
        case .image(_, let data, _, _):
            if let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                mediaPlaceholder
            }

        case .video(_, let fileURL, _, _):
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
    case image(UUID, Data, String, String)
    case video(UUID, URL, String, String)

    var id: UUID {
        switch self {
        case .image(let id, _, _, _), .video(let id, _, _, _):
            id
        }
    }

    var fileName: String {
        switch self {
        case .image(_, _, _, let fileName), .video(_, _, _, let fileName):
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

    var multipartFile: MultipartFormFile {
        switch self {
        case .image(_, let data, let mimeType, let fileName):
            MultipartFormFile(
                fieldName: "files",
                fileName: fileName,
                mimeType: mimeType,
                data: data)
        case .video(_, let fileURL, let mimeType, let fileName):
            MultipartFormFile(
                fieldName: "files",
                fileName: fileName,
                mimeType: mimeType,
                fileURL: fileURL)
        }
    }

    func discardTemporaryFile() {
        guard case .video(_, let fileURL, _, _) = self else { return }
        try? FileManager.default.removeItem(at: fileURL)
    }

    static func image(from image: UIImage) throws -> LegendSocialMediaDraft {
        guard let data = image.jpegData(compressionQuality: 0.9) else {
            throw LegendSocialMediaLoadingError.unavailable
        }
        return .image(UUID(), data, "image/jpeg", "legend-camera.jpg")
    }

    static func video(from sourceURL: URL) throws -> LegendSocialMediaDraft {
        let representation = videoRepresentation(for: sourceURL)
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("legend-camera-\(UUID().uuidString)")
            .appendingPathExtension(sourceURL.pathExtension)
        try FileManager.default.copyItem(at: sourceURL, to: destination)
        return .video(UUID(), destination, representation.mimeType, representation.fileName)
    }

    static func load(
        from item: PhotosPickerItem
    ) async throws -> LegendSocialMediaDraft {
        let supportedTypes = item.supportedContentTypes
        if let imageType = supportedTypes.first(where: { $0.conforms(to: .image) }) {
            guard let data = try await item.loadTransferable(type: Data.self) else {
                throw LegendSocialMediaLoadingError.unavailable
            }
            let representation = imageRepresentation(for: imageType)
            return .image(
                UUID(),
                data,
                representation.mimeType,
                representation.fileName)
        }

        if supportedTypes.contains(where: { $0.conforms(to: .movie) || $0.conforms(to: .video) }),
           let video = try await item.loadTransferable(type: LegendPickedVideo.self) {
            let representation = videoRepresentation(for: video.fileURL)
            return .video(
                UUID(),
                video.fileURL,
                representation.mimeType,
                representation.fileName)
        }

        throw LegendSocialMediaLoadingError.unsupported
    }

    private static func imageRepresentation(
        for type: UTType
    ) -> (mimeType: String, fileName: String) {
        if type.conforms(to: .png) { return ("image/png", "legend-image.png") }
        if type.conforms(to: .heic) { return ("image/heic", "legend-image.heic") }
        if type.conforms(to: .heif) { return ("image/heif", "legend-image.heif") }
        if type.conforms(to: .webP) { return ("image/webp", "legend-image.webp") }
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

    func makeCoordinator() -> Coordinator {
        Coordinator(captured: captured)
    }

    func makeUIViewController(context: Context) -> UIImagePickerController {
        let picker = UIImagePickerController()
        picker.sourceType = .camera
        picker.mediaTypes = [
            capturesVideo ? UTType.movie.identifier : UTType.image.identifier
        ]
        picker.cameraCaptureMode = capturesVideo ? .video : .photo
        picker.delegate = context.coordinator
        picker.allowsEditing = false
        return picker
    }

    func updateUIViewController(
        _ controller: UIImagePickerController,
        context: Context
    ) {}

    final class Coordinator: NSObject, UINavigationControllerDelegate, UIImagePickerControllerDelegate {
        private let captured: (Result<LegendSocialMediaDraft, Error>) -> Void

        init(captured: @escaping (Result<LegendSocialMediaDraft, Error>) -> Void) {
            self.captured = captured
        }

        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) {
            picker.dismiss(animated: true)
        }

        func imagePickerController(
            _ picker: UIImagePickerController,
            didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]
        ) {
            do {
                let media: LegendSocialMediaDraft
                if let image = info[.originalImage] as? UIImage {
                    media = try .image(from: image)
                } else if let url = info[.mediaURL] as? URL {
                    media = try .video(from: url)
                } else {
                    throw LegendSocialMediaLoadingError.unavailable
                }
                picker.dismiss(animated: true) { [captured] in
                    captured(.success(media))
                }
            } catch {
                picker.dismiss(animated: true) { [captured] in
                    captured(.failure(error))
                }
            }
        }
    }
}

private extension MobileSocialContentType {
    func accepts(_ media: LegendSocialMediaDraft) -> Bool {
        media.isVideo ? acceptsVideos : acceptsImages
    }
}

private struct LegendPickedVideo: Transferable {
    let fileURL: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .movie) { received in
            let extensionValue = received.file.pathExtension.isEmpty
                ? "mov"
                : received.file.pathExtension
            let destination = FileManager.default.temporaryDirectory
                .appendingPathComponent("legend-video-\(UUID().uuidString)")
                .appendingPathExtension(extensionValue)
            try FileManager.default.copyItem(
                at: received.file,
                to: destination)
            return LegendPickedVideo(fileURL: destination)
        }
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

    init() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))
    }

    func refresh() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))
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
        DispatchQueue.main.async { [weak self] in
            self?.refresh()
        }
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
}

private extension Array {
    func asyncMap<T>(
        _ transform: (Element) async throws -> T
    ) async rethrows -> [T] {
        var values: [T] = []
        values.reserveCapacity(count)

        for element in self {
            values.append(try await transform(element))
        }

        return values
    }
}
