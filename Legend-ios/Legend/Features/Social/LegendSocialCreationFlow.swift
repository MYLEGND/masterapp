import AVFoundation
import AVKit
import CoreImage
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

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.hero
                    .ignoresSafeArea()

                VStack(spacing: 0) {
                    HStack {
                        Button(action: dismiss) {
                            Image(systemName: "xmark")
                                .font(.headline.weight(.semibold))
                                .frame(width: 44, height: 44)
                                .background(Color.white.opacity(0.12), in: Circle())
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.white)
                        .accessibilityLabel(LegendLocalized("Close creator", context: "accessibility copy"))

                        Spacer()

                        Text(LegendLocalized("Create"))
                            .font(.headline.weight(.semibold))
                            .foregroundStyle(.white)

                        Spacer()

                        Color.clear.frame(width: 44, height: 44)
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.top, LegendNextSpacing.xs)

                    VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                        Text(LegendLocalized("Choose a format"))
                            .font(.title2.weight(.bold))
                            .foregroundStyle(.white)

                        ForEach(MobileSocialContentType.allCases) { candidate in
                            Button { select(candidate) } label: {
                                creationOption(candidate)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(LegendLocalized("Create {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (candidate.displayName))]))
                            .accessibilityHint(candidate.creationPrompt)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
                    .padding(.horizontal, LegendNextSpacing.lg)
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome(detents: [.large], showsDragIndicator: false)
    }

    private func creationOption(_ type: MobileSocialContentType) -> some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: type.systemImage)
                .font(.body.weight(.semibold))
                .frame(width: 40, height: 40)
                .foregroundStyle(LegendNextColor.goldBright)
                .background(Color.white.opacity(0.11), in: Circle())

            VStack(alignment: .leading, spacing: 2) {
                Text(type.displayName)
                    .font(.body.weight(.semibold))
                    .foregroundStyle(.white)
            }

            Spacer(minLength: LegendNextSpacing.xs)

            Image(systemName: "chevron.right")
                .font(.caption.weight(.bold))
                .foregroundStyle(Color.white.opacity(0.55))
                .accessibilityHidden(true)
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .frame(height: 64)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(Color.white.opacity(0.12), lineWidth: 1)
        }
    }
}

struct LegendSocialComposer: View {
    @ObservedObject var social: MobileSocialStore
    let dismiss: () -> Void

    @StateObject private var photoLibrary = LegendPhotoLibraryAccess()
    @State private var caption = ""
    @State private var type: MobileSocialContentType
    @State private var selectedMedia: [LegendSocialMediaDraft] = []
    /// One draft-level edit authority. It stays with the selected source until
    /// publication, then the same renderer supplies the preview and uploaded
    /// bytes. No visual-only editor state is allowed to escape this composer.
    @State private var mediaEdits: [UUID: LegendSocialMediaEditState] = [:]
    @State private var activeMediaID: UUID?
    @State private var accessibilityText = ""
    @State private var tagsAndMentions = ""
    @State private var shareLocation = ""
    @State private var commentsEnabled = true
    @State private var mediaSelectionError: String?
    @State private var stage: LegendSocialCreationStage = .library
    @State private var activeEditorTool: LegendSocialEditingTool = .text
    @State private var ownsMediaAfterDismissal = false
    @State private var selectedHacPreviewData: Data?
    @State private var isSelectingHacPreview = false
    @State private var isTrimmingVideo = false
    @State private var isPreparingPublication = false
    @State private var showsDiscardConfirmation = false
    @State private var postCanvas = LegendSocialPostCanvas.portrait
    @State private var selectedAdjustment: LegendSocialImageAdjustment = .brightness
    @State private var detailEditor: LegendSocialPublicationDetail?

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
                switch stage {
                case .library,
                     .preparingMedia,
                     .camera,
                     .failed:
                    libraryContent

                case .metadata:
                    metadataContent

                case .share:
                    shareDetailsContent

                case .handedOff:
                    shareDetailsContent

                case .music:
                    // Music attachment playback has no production mix/export
                    // authority yet. This legacy state remains source-compatible
                    // but is never routed from the creator.
                    metadataContent
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome(detents: [.large], showsDragIndicator: false)
        .sheet(isPresented: $isSelectingHacPreview) {
            if let sourceURL = selectedMedia.first?.videoFileURL {
                LegendHacPreviewSelector(
                    sourceURL: sourceURL,
                    selectedRange: activeVideoEdit.selectedRange,
                    save: { previewData in
                        selectedHacPreviewData = previewData
                        isSelectingHacPreview = false
                    },
                    cancel: {
                        isSelectingHacPreview = false
                })
            }
        }
        .sheet(isPresented: $isTrimmingVideo) {
            if let video = activeMedia,
               let sourceURL = video.videoFileURL {
                LegendSocialVideoTrimSheet(
                    sourceURL: sourceURL,
                    selection: activeVideoEdit,
                    save: { trim in
                        updateActiveEdit { $0.video = trim }
                        if type.requiresVideo {
                            selectedHacPreviewData = LegendHacPreviewFrame.jpegData(
                                from: sourceURL,
                                at: trim.trimStartSeconds)
                        }
                        isTrimmingVideo = false
                    },
                    cancel: { isTrimmingVideo = false })
            }
        }
        .sheet(item: $detailEditor) { detail in
            LegendSocialPublicationDetailSheet(
                detail: detail,
                tagsAndMentions: $tagsAndMentions,
                location: $shareLocation,
                accessibilityText: $accessibilityText)
        }
        .fullScreenCover(isPresented: cameraPresented) {
            LegendSocialCameraCapture(
                allowsPhotos: type.format.acceptsImages,
                allowsVideos: type.format.acceptsVideos,
                maximumVideoDuration: type.format.maximumVideoDurationSeconds,
                captured: { result in
                    addCapturedMedia(result)
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
            if updatedType != .post {
                postCanvas = .portrait
            }
        }
        .onDisappear {
            if !ownsMediaAfterDismissal {
                discardTemporaryMedia()
            }
        }
        .confirmationDialog(
            LegendLocalized("Discard this creation?"),
            isPresented: $showsDiscardConfirmation,
            titleVisibility: .visible
        ) {
            Button(LegendLocalized("Discard creation"), role: .destructive) {
                discardTemporaryMedia()
                dismiss()
            }
            Button(LegendLocalized("Keep editing"), role: .cancel) {}
        } message: {
            Text(LegendLocalized("Your selected media, edits, and publishing details will be removed."))
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
            return hasValidSelection || type.format.allowsTextOnlyPublication

        case .metadata, .share:
            // A failed publication no longer counts as in-flight, so a recoverable
            // upload failure cannot permanently disable Next and Share.
            return canPublish && !social.isPublishing

        case .music, .handedOff, .failed:
            return false
        }
    }

    private var primaryActionTitle: String {
        switch stage {
        case .share:
            "Share"
        default:
            "Next"
        }
    }

    private var cameraPresented: Binding<Bool> {
        Binding(
            get: { stage == .camera },
            set: { if !$0 && stage == .camera { stage = .library } })
    }

    /// The selected position is the one editing target for a multi-media Post.
    /// Stories and Hacs are constrained to a single item by the shared format
    /// contract, so they naturally resolve to that item.
    private var activeMedia: LegendSocialMediaDraft? {
        if let activeMediaID,
           let selected = selectedMedia.first(where: { $0.id == activeMediaID }) {
            return selected
        }
        return selectedMedia.first
    }

    private var activeEdit: LegendSocialMediaEditState {
        guard let activeMedia else { return .initial }
        return mediaEdits[activeMedia.id] ?? .initial
    }

    private var activeVideoEdit: LegendSocialVideoEdit {
        activeEdit.video
    }

    private func edit(for media: LegendSocialMediaDraft) -> LegendSocialMediaEditState {
        mediaEdits[media.id] ?? .initial
    }

    private func updateActiveEdit(
        _ transform: (inout LegendSocialMediaEditState) -> Void
    ) {
        guard let activeMedia else { return }
        var updated = edit(for: activeMedia)
        transform(&updated)
        mediaEdits[activeMedia.id] = updated
    }

    private func activeEditBinding() -> Binding<LegendSocialMediaEditState> {
        Binding(
            get: { activeEdit },
            set: { updated in
                guard let activeMedia else { return }
                mediaEdits[activeMedia.id] = updated
            })
    }

    private var libraryContent: some View {
        ZStack {
            LegendNextGradient.hero
                .ignoresSafeArea()

            VStack(spacing: 0) {
                libraryHeader

                ScrollView {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                        mediaLibraryToolbar

                        if !selectedMedia.isEmpty {
                            compactSelectionStrip
                        }

                        photoLibraryStatus

                        mediaGrid
                    }
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.bottom, LegendNextSpacing.lg)
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    private var libraryHeader: some View {
        HStack {
            Button(action: cancel) {
                Image(systemName: "xmark")
                    .font(.headline.weight(.semibold))
                    .frame(width: 44, height: 44)
                    .background(Color.white.opacity(0.12), in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.white)
            .accessibilityLabel(LegendLocalized("Cancel {value1} creation", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))]))

            Spacer()

            Text(type.newContentTitle)
                .font(.headline.weight(.semibold))
                .foregroundStyle(.white)

            Spacer()

            Button(action: primaryAction) {
                Text(LegendLocalized("Next"))
                    .font(.body.weight(.semibold))
                    .foregroundStyle(canContinue ? LegendNextColor.goldBright : Color.white.opacity(0.36))
                    .frame(width: 44, height: 44)
            }
            .buttonStyle(.plain)
            .disabled(!canContinue)
            .accessibilityLabel(LegendLocalized("Continue to {value1} editor", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))]))
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
    }

    private var eligibleLibraryAssets: [LegendPhotoLibraryAsset] {
        photoLibrary.assets(for: type)
    }

    private var selectionAspectRatio: CGFloat {
        1
    }

    private var selectionPreviewSide: CGFloat {
        66
    }

    private var mediaLibraryToolbar: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: 2) {
                Text(LegendLocalized("Recents"))
                    .font(.title3.weight(.bold))
                    .foregroundStyle(.white)
                Text(type.maximumMediaItems == 1
                     ? LegendLocalized(
                        "Select one {type} item",
                        arguments: ["type": type.displayName.lowercased()])
                     : LegendLocalized(
                        "Select up to {count} items",
                        arguments: ["count": String(type.maximumMediaItems)]))
                    .font(.caption)
                    .foregroundStyle(Color.white.opacity(0.62))
            }

            Spacer()

            Button { stage = .camera } label: {
                Image(systemName: "camera.fill")
                    .font(.body.weight(.semibold))
                    .frame(width: 42, height: 42)
                    .foregroundStyle(LegendNextColor.midnight)
                    .background(LegendNextColor.goldBright, in: Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Open camera for {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))]))
        }
    }

    @ViewBuilder
    private var compactSelectionStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .center, spacing: LegendNextSpacing.xs) {
                ForEach(selectedMedia) { media in
                    LegendSocialMediaPreview(
                        media: media,
                        presentation: .selection,
                        remove: { remove(media) })
                    .frame(width: selectionPreviewSide, height: selectionPreviewSide)
                    .overlay {
                        RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous)
                        .strokeBorder(
                            media.id == activeMedia?.id
                                ? LegendNextColor.goldBright
                                : Color.clear,
                            lineWidth: 3)
                    }
                    .onTapGesture { activeMediaID = media.id }
                    .accessibilityAddTraits(
                        media.id == activeMedia?.id ? .isSelected : [])
                }
            }
            .padding(.horizontal, 2)
        }
        .scrollIndicators(.hidden)
        .overlay(alignment: .bottomTrailing) {
            if type.maximumMediaItems > 1, selectedMedia.count > 1 {
                HStack(spacing: 0) {
                    Button { moveActiveMedia(by: -1) } label: {
                        Image(systemName: "arrow.left")
                    }
                    .disabled(activeMediaIndex == 0)
                    Button { moveActiveMedia(by: 1) } label: {
                        Image(systemName: "arrow.right")
                    }
                    .disabled(activeMediaIndex >= selectedMedia.count - 1)
                }
                .font(.caption.weight(.bold))
                .foregroundStyle(LegendNextColor.midnight)
                .padding(8)
                .background(LegendNextColor.goldBright, in: Capsule())
                .padding(6)
                .accessibilityLabel(LegendLocalized("Reorder selected media", context: "accessibility copy"))
            }
        }

        if let mediaSelectionError {
            Label(mediaSelectionError, systemImage: "exclamationmark.triangle.fill")
                .font(.caption)
                .foregroundStyle(LegendNextColor.warning)
        }
    }

    private var activeMediaIndex: Int {
        guard let activeMediaID,
              let index = selectedMedia.firstIndex(where: { $0.id == activeMediaID }) else {
            return 0
        }
        return index
    }

    private func moveActiveMedia(by offset: Int) {
        let source = activeMediaIndex
        let destination = min(max(0, source + offset), selectedMedia.count - 1)
        guard source != destination else { return }
        selectedMedia.swapAt(source, destination)
    }

    /// The full eligible library, laid out inline so the enclosing ScrollView owns the
    /// scrolling. The grid previously lived inside a GeometryReader with a fixed
    /// minHeight, which collapsed it to roughly two rows and made every asset past the
    /// sixth unreachable no matter how large the library was.
    private var mediaGrid: some View {
        let spacing: CGFloat = 2
        let columnsCount = 3
        let columns = Array(
            repeating: GridItem(
                .flexible(),
                spacing: spacing
            ),
            count: columnsCount
        )

        return LazyVGrid(
            columns: columns,
            alignment: .center,
            spacing: spacing
        ) {
            ForEach(eligibleLibraryAssets) { asset in
                LegendPhotoLibraryThumbnail(
                    asset: asset,
                    photoLibrary: photoLibrary,
                    isSelected:
                        selectedAssetIdentifiers.contains(asset.id),
                    selectionIndex: selectionIndex(of: asset),
                    isEligible: true,
                    select: { select(asset) }
                )
                .aspectRatio(
                    selectionAspectRatio,
                    contentMode: .fit
                )
                .clipped()
                .onAppear {
                    // Page in the next batch as the tail of the grid comes into view.
                    if asset.id == eligibleLibraryAssets.last?.id {
                        photoLibrary.loadNextPage(for: type)
                    }
                }
            }

            if photoLibrary.canLoadMore(for: type) {
                LegendSkeletonShape(cornerRadius: 2)
                    .frame(maxWidth: .infinity)
                    .aspectRatio(
                        selectionAspectRatio,
                        contentMode: .fit
                    )
                    .accessibilityLabel(LegendLocalized("Loading more media", context: "accessibility copy"))
                    .onAppear { photoLibrary.loadNextPage(for: type) }
            }
        }
    }

    /// One-based position of an asset in the current selection, for ordered badges.
    private func selectionIndex(of asset: LegendPhotoLibraryAsset) -> Int? {
        guard let index = selectedMedia.firstIndex(where: {
            $0.sourceAssetIdentifier == asset.id
        }) else {
            return nil
        }
        return index + 1
    }

    @ViewBuilder
    private var photoLibraryStatus: some View {
        switch photoLibrary.status {
        case .authorized:
            EmptyView()

        case .limited:
            HStack(spacing: LegendNextSpacing.sm) {
                photoLibraryNotice(
                    "Selected Photos access is enabled.",
                    symbol: "photo.on.rectangle.angled",
                    color: LegendNextColor.information)
                Spacer(minLength: LegendNextSpacing.xs)
                Button(LegendLocalized("Select More Photos")) {
                    photoLibrary.presentLimitedLibraryPicker()
                }
                .font(.caption.weight(.semibold))
                .buttonStyle(.bordered)
                .accessibilityLabel(LegendLocalized("Select more photos for Legend", context: "accessibility copy"))
            }

        case .notDetermined:
            HStack(spacing: LegendNextSpacing.sm) {
                photoLibraryNotice(
                    "Grant photo access to choose media from your library.",
                    symbol: "photo.badge.plus",
                    color: LegendNextColor.information)
                Spacer(minLength: LegendNextSpacing.xs)
                Button(LegendLocalized("Manage access")) {
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
                Button(LegendLocalized("Open Settings")) {
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
        immersiveEditingContent
    }

    private var editorCanvasAspectRatio: CGFloat {
        type == .post
            ? CGFloat(postCanvas.rawValue)
            : CGFloat(type.format.mediaAspectRatio)
    }

    private var editorCanvasMaximumWidth: CGFloat {
        CGFloat(type.format.editorMaximumWidth)
    }

    private var immersiveEditingContent: some View {
        Group {
            if type == .story {
                storyEditingContent
            } else {
                standardEditingContent
            }
        }
    }

    private var standardEditingContent: some View {
        ZStack {
            LegendNextColor.midnight.ignoresSafeArea()

            VStack(spacing: 0) {
                immersiveEditorHeader

                GeometryReader { geometry in
                    let horizontalInset = LegendNextSpacing.sm * 2
                    let availableWidth = max(0, geometry.size.width - horizontalInset)
                    let availableHeight = max(0, geometry.size.height - LegendNextSpacing.xs)
                    let width = min(
                        editorCanvasMaximumWidth,
                        availableWidth,
                        availableHeight * editorCanvasAspectRatio)

                    editorMediaCanvas
                        .frame(width: width, height: width / editorCanvasAspectRatio)
                        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }

                editorControlDeck
            }
        }
    }

    private var storyEditingContent: some View {
        ZStack {
            LegendNextColor.midnight.ignoresSafeArea()

            editorMediaCanvas
                .ignoresSafeArea(edges: .bottom)

            VStack(spacing: 0) {
                immersiveEditorHeader
                    .background(
                        LinearGradient(
                            colors: [LegendNextColor.midnight.opacity(0.90), .clear],
                            startPoint: .top,
                            endPoint: .bottom))

                Spacer(minLength: 0)

                editorControlDeck
                    .background(
                        LinearGradient(
                            colors: [.clear, LegendNextColor.midnight.opacity(0.94)],
                            startPoint: .top,
                            endPoint: .bottom))
            }
        }
    }

    private var immersiveEditorHeader: some View {
        HStack {
                Button {
                    stage = .library
                } label: {
                    Image(systemName: "chevron.left")
                        .font(.headline.weight(.semibold))
                        .frame(width: 44, height: 44)
                        .background(Color.white.opacity(0.14), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .accessibilityLabel(LegendLocalized("Back to media selection", context: "accessibility copy"))

                Spacer(minLength: 0)

                Text(type.editingTitle)
                    .font(.headline.weight(.semibold))
                    .foregroundStyle(.white)

                Spacer(minLength: 0)

                Button {
                    primaryAction()
                } label: {
                    Text(LegendLocalized("Next"))
                        .font(.body.weight(.semibold))
                        .foregroundStyle(canContinue ? LegendNextColor.goldBright : Color.white.opacity(0.38))
                        .frame(width: 44, height: 44)
                }
                .buttonStyle(.plain)
                .disabled(!canContinue)
                .accessibilityLabel(LegendLocalized("Continue to {value1} details", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))]))
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
    }

    @ViewBuilder
    private var editorMediaCanvas: some View {
        if selectedMedia.isEmpty {
            ZStack {
                LegendNextGradient.hero

                VStack(spacing: LegendNextSpacing.sm) {
                    Image(systemName: type.systemImage)
                        .font(
                            .system(
                                size: 42,
                                weight: .semibold
                            )
                        )

                    Text(LegendLocalized("Add content"))
                        .font(LegendNextTypography.cardTitle)
                }
                .foregroundStyle(.white)
            }
        } else {
            ZStack {
                LegendNextColor.midnight

                if let primaryMedia = activeMedia {
                    Group {
                        if let sourceURL = primaryMedia.videoFileURL {
                            LegendSocialPlayableVideoPreview(
                                sourceURL: sourceURL,
                                trim: activeVideoEdit,
                                isMuted: activeVideoEdit.isOriginalAudioMuted,
                                showsAudioControl: false,
                                onMutedChanged: { isMuted in
                                    updateActiveEdit { $0.video.isOriginalAudioMuted = isMuted }
                                })
                        } else {
                            LegendSocialEditableImageCanvas(
                                media: primaryMedia,
                                edit: activeEditBinding(),
                                aspectRatio: editorCanvasAspectRatio,
                                allowsStoryOverlay: type == .story)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .clipped()
                }

                if type.maximumMediaItems > 1 && selectedMedia.count > 1 {
                    HStack(spacing: LegendNextSpacing.xs) {
                        Button(LegendLocalized("Previous"), systemImage: "chevron.left") {
                            moveActiveMediaSelection(by: -1)
                        }
                        .disabled(activeMediaIndex == 0)

                        Text(LegendLocalized("{value1} of {value2}", arguments: ["value1": String(describing: (activeMediaIndex + 1)), "value2": String(describing: (selectedMedia.count))]))
                            .font(.caption.weight(.bold))
                            .monospacedDigit()

                        Button(LegendLocalized("Next"), systemImage: "chevron.right") {
                            moveActiveMediaSelection(by: 1)
                        }
                        .disabled(activeMediaIndex >= selectedMedia.count - 1)
                    }
                    .foregroundStyle(.white)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 7)
                    .background(LegendNextColor.midnight.opacity(0.62), in: Capsule())
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 12)
                }
            }
        }
    }

    private func moveActiveMediaSelection(by offset: Int) {
        let next = min(max(0, activeMediaIndex + offset), selectedMedia.count - 1)
        activeMediaID = selectedMedia[next].id
    }

    private var immersiveToolRail: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: LegendNextSpacing.sm) {
                ForEach(availableEditingTools) { tool in
                    editorToolButton(tool: tool)
                }
            }
            .padding(.horizontal, LegendNextSpacing.sm)
        }
    }

    private var availableEditingTools: [LegendSocialEditingTool] {
        guard let activeMedia else { return [] }
        return LegendSocialEditingTool.allCases.filter {
            $0.isAvailable(for: activeMedia, contentType: type)
        }
    }

    private func editorToolButton(
        tool: LegendSocialEditingTool
    ) -> some View {
        Button {
            activeEditorTool = tool
        } label: {
            VStack(spacing: 4) {
                Image(systemName: tool.systemImage)
                    .font(.body.weight(.semibold))
                    .frame(width: 40, height: 32)
                Text(tool.title)
                    .font(.caption2.weight(.semibold))
                    .lineLimit(1)
            }
            .frame(minWidth: 54, minHeight: 54)
            .foregroundStyle(activeEditorTool == tool ? LegendNextColor.midnight : .white)
            .background(
                activeEditorTool == tool ? LegendNextColor.goldBright : Color.white.opacity(0.11),
                in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(tool.title)
        .accessibilityAddTraits(
            activeEditorTool == tool
                ? .isSelected
                : []
        )
    }

    @ViewBuilder
    private var immersiveToolDetail: some View {
        switch activeEditorTool {
        case .audio:
            videoAudioEditor

        case .transform:
            imageTransformEditor

        case .style:
            imageStyleEditor

        case .adjust:
            imageAdjustmentEditor

        case .text:
            storyTextEditor
        }
    }

    @ViewBuilder
    private var videoAudioEditor: some View {
        if activeMedia?.isVideo == true {
            HStack(spacing: LegendNextSpacing.md) {
                Toggle(
                    LegendLocalized("Original audio"),
                    isOn: Binding(
                        get: { !activeVideoEdit.isOriginalAudioMuted },
                        set: { isEnabled in
                            updateActiveEdit { $0.video.isOriginalAudioMuted = !isEnabled }
                        }))
                .tint(LegendNextColor.goldBright)
                .foregroundStyle(.white)

                Button(LegendLocalized("Trim"), systemImage: "scissors") {
                    isTrimmingVideo = true
                }
                .font(.caption.weight(.bold))
                .foregroundStyle(LegendNextColor.goldBright)
                .accessibilityLabel(LegendLocalized("Trim selected video", context: "accessibility copy"))
            }
            .padding(.horizontal, LegendNextSpacing.md)
            .padding(.vertical, LegendNextSpacing.sm)
        }
    }

    private var imageTransformEditor: some View {
        HStack(spacing: LegendNextSpacing.md) {
            Text(LegendLocalized("Pinch to zoom · Drag to position"))
                .font(.caption)
                .foregroundStyle(Color.white.opacity(0.70))
            Spacer(minLength: 0)
            HStack(spacing: LegendNextSpacing.sm) {
                Button(LegendLocalized("Rotate left"), systemImage: "rotate.left") {
                    updateActiveEdit { $0.rotationDegrees = max(-180, $0.rotationDegrees - 90) }
                }
                Button(LegendLocalized("Rotate right"), systemImage: "rotate.right") {
                    updateActiveEdit { $0.rotationDegrees = min(180, $0.rotationDegrees + 90) }
                }
                Button(LegendLocalized("Reset")) { updateActiveEdit { $0.resetTransform() } }
            }
            .font(.caption.weight(.bold))
            .foregroundStyle(LegendNextColor.goldBright)
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var imageStyleEditor: some View {
        VStack(spacing: LegendNextSpacing.xs) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: LegendNextSpacing.sm) {
                    ForEach(LegendSocialImageFilter.allCases) { filter in
                        filterButton(filter)
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
            }
            if activeEdit.filter != .original {
                LegendSocialAdjustmentSlider(
                    title: LegendLocalized("Intensity"),
                    value: Binding(
                        get: { activeEdit.filterIntensity },
                        set: { value in updateActiveEdit { $0.filterIntensity = value } }),
                    range: 0...1)
            }
        }
        .padding(.vertical, LegendNextSpacing.xs)
    }

    @ViewBuilder
    private func filterButton(_ filter: LegendSocialImageFilter) -> some View {
        if let media = activeMedia {
            Button {
                updateActiveEdit { $0.filter = filter }
            } label: {
                VStack(spacing: 4) {
                    LegendSocialRenderedImagePreview(
                        media: media,
                        edit: filterPreviewEdit(filter),
                        aspectRatio: 1)
                    .frame(width: 52, height: 52)
                    .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 9, style: .continuous)
                            .strokeBorder(
                                activeEdit.filter == filter ? LegendNextColor.goldBright : .white.opacity(0.18),
                                lineWidth: activeEdit.filter == filter ? 2 : 1)
                    }

                    Text(filter.title)
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(activeEdit.filter == filter ? LegendNextColor.goldBright : .white.opacity(0.76))
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Apply {value1} filter", context: "accessibility copy", arguments: ["value1": String(describing: (filter.title))]))
            .accessibilityAddTraits(activeEdit.filter == filter ? .isSelected : [])
        }
    }

    private func filterPreviewEdit(_ filter: LegendSocialImageFilter) -> LegendSocialMediaEditState {
        var edit = activeEdit
        edit.filter = filter
        return edit
    }

    private var imageAdjustmentEditor: some View {
        VStack(spacing: LegendNextSpacing.sm) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: LegendNextSpacing.sm) {
                    ForEach(LegendSocialImageAdjustment.allCases) { adjustment in
                        Button(adjustment.title) { selectedAdjustment = adjustment }
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(selectedAdjustment == adjustment ? LegendNextColor.midnight : .white)
                            .padding(.horizontal, LegendNextSpacing.sm)
                            .frame(height: 30)
                            .background(
                                selectedAdjustment == adjustment ? LegendNextColor.goldBright : Color.white.opacity(0.11),
                                in: Capsule())
                            .accessibilityAddTraits(selectedAdjustment == adjustment ? .isSelected : [])
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
            }

            LegendSocialAdjustmentSlider(
                title: selectedAdjustment.title,
                value: adjustmentBinding(selectedAdjustment.keyPath),
                range: selectedAdjustment.range)
                .padding(.horizontal, LegendNextSpacing.md)

            HStack {
                Spacer()
                Button(LegendLocalized("Reset")) { updateActiveEdit { $0.resetAdjustments() } }
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.goldBright)
                    .padding(.trailing, LegendNextSpacing.md)
            }
        }
    }

    private func adjustmentBinding(
        _ keyPath: WritableKeyPath<LegendSocialMediaEditState, Double>
    ) -> Binding<Double> {
        Binding(
            get: { activeEdit[keyPath: keyPath] },
            set: { value in updateActiveEdit { $0[keyPath: keyPath] = value } })
    }

    private var storyTextEditor: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            TextField(LegendLocalized("Add story text…"), text: Binding(
                get: { activeEdit.storyOverlay.text },
                set: { text in updateActiveEdit { $0.storyOverlay.text = text } }),
                axis: .vertical)
            .lineLimit(1...3)
            .font(LegendNextTypography.body)
            .foregroundStyle(.white)
            .tint(LegendNextColor.goldBright)

            HStack(spacing: LegendNextSpacing.xs) {
                ForEach(LegendSocialStoryTextColor.allCases) { color in
                    Button {
                        updateActiveEdit { $0.storyOverlay.color = color }
                    } label: {
                        Circle()
                            .fill(color.swiftUIColor)
                            .frame(width: 26, height: 26)
                            .overlay {
                                Circle().strokeBorder(
                                    activeEdit.storyOverlay.color == color ? LegendNextColor.goldBright : .white.opacity(0.36),
                                    lineWidth: activeEdit.storyOverlay.color == color ? 3 : 1)
                            }
                    }
                    .accessibilityLabel(LegendLocalized("Story text color {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (color.title))]))
                    .accessibilityAddTraits(activeEdit.storyOverlay.color == color ? .isSelected : [])
                }
                Spacer(minLength: 0)
                LegendSocialAdjustmentSlider(
                    title: LegendLocalized("Size"),
                    value: Binding(
                        get: { activeEdit.storyOverlay.scale },
                        set: { value in updateActiveEdit { $0.storyOverlay.scale = value } }),
                    range: 0.7...1.7,
                    compact: true)
            }
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var editorControlDeck: some View {
        VStack(spacing: LegendNextSpacing.sm) {
            if type == .post {
                HStack(spacing: LegendNextSpacing.xs) {
                    Text(LegendLocalized("Format"))
                        .font(LegendNextTypography.label)
                        .foregroundStyle(Color.white.opacity(0.72))
                    Spacer(minLength: 0)
                    ForEach(LegendSocialPostCanvas.allCases) { canvas in
                        Button(canvas.title) {
                            postCanvas = canvas
                        }
                        .font(LegendNextTypography.label)
                        .foregroundStyle(
                            postCanvas == canvas
                                ? LegendNextColor.midnight
                                : .white)
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .frame(height: LegendNextSize.compactControlHeight)
                        .background(
                            postCanvas == canvas
                                ? LegendNextColor.goldBright
                                : Color.white.opacity(0.12),
                            in: Capsule())
                        .buttonStyle(.plain)
                        .accessibilityAddTraits(
                            postCanvas == canvas ? .isSelected : [])
                    }
                }
            }
            immersiveToolRail
            immersiveToolDetail
            publicationFailure
        }
        .padding(.top, LegendNextSpacing.xs)
        .padding(.bottom, LegendNextSpacing.sm)
        .background(
            LinearGradient(
                colors: [
                    LegendNextColor.midnight.opacity(0),
                    LegendNextColor.midnight.opacity(0.96)
                ],
                startPoint: .top,
                endPoint: .bottom
            )
        )
    }

    private var shareDetailsContent: some View {
        VStack(spacing: 0) {
            shareHeader

            ScrollView {
                VStack(spacing: LegendNextSpacing.md) {
                    finalReview

                    captionComposer

                    publicationSettings

                    publicationFailure
                        .padding(.horizontal, 16)

                    if let mediaSelectionError {
                        Label(mediaSelectionError, systemImage: "exclamationmark.triangle.fill")
                            .font(.caption)
                            .foregroundStyle(LegendNextColor.warning)
                            .padding(.horizontal, 16)
                    }

                    if isPreparingPublication {
                        HStack(spacing: LegendNextSpacing.xs) {
                            ProgressView()
                            Text(LegendLocalized("Preparing your final media…"))
                                .font(LegendNextTypography.supporting)
                        }
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .padding(.vertical, LegendNextSpacing.sm)
                    }
                }
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.sm)
            }
            .scrollIndicators(.hidden)
        }
        .background(LegendNextColor.canvas)
    }

    private var shareHeader: some View {
        HStack(spacing: 12) {
                Button {
                    stage = .metadata
                } label: {
                    Image(systemName: "chevron.left")
                        .font(.headline.weight(.semibold))
                        .frame(width: 44, height: 44)
                        .background(LegendNextColor.surfaceInset, in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.textPrimary)
                .accessibilityLabel(LegendLocalized("Back to media editing", context: "accessibility copy"))

                Spacer(minLength: 0)

                Text(type.newContentTitle)
                    .font(.headline.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Spacer(minLength: 0)

                Button(action: publish) {
                    Text(LegendLocalized("Share"))
                        .font(.body.weight(.semibold))
                        .frame(minWidth: 44, minHeight: 44)
                }
                .buttonStyle(.plain)
                .foregroundStyle(
                    canPublish && !social.isPublishing && !isPreparingPublication
                        ? LegendNextColor.goldBright
                        : LegendNextColor.textSecondary.opacity(0.45)
                )
                .disabled(!canPublish || social.isPublishing || isPreparingPublication)
                .accessibilityLabel(
                    LegendLocalized("Publish {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))])
                )
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
        .background(LegendNextColor.surface)
    }

    @ViewBuilder
    private var finalReview: some View {
        if let media = activeMedia {
            Group {
                if let sourceURL = media.videoFileURL {
                    LegendSocialPlayableVideoPreview(
                        sourceURL: sourceURL,
                        trim: activeVideoEdit,
                        isMuted: activeVideoEdit.isOriginalAudioMuted,
                        onMutedChanged: { isMuted in
                            updateActiveEdit { $0.video.isOriginalAudioMuted = isMuted }
                        })
                    .frame(height: reviewMediaHeight)
                } else {
                    LegendSocialRenderedImagePreview(
                        media: media,
                        edit: activeEdit,
                        aspectRatio: editorCanvasAspectRatio)
                    .frame(height: reviewMediaHeight)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        }
    }

    private var reviewMediaHeight: CGFloat {
        switch type {
        case .hac: 390
        case .story: 360
        case .post: 248
        }
    }

    private var captionComposer: some View {
        HStack(alignment: .top, spacing: 12) {
            shareThumbnail

            ZStack(alignment: .topLeading) {
                if caption.isEmpty {
                    Text(
                        type == .story
                            ? LegendLocalized("Add a story message...")
                            : LegendLocalized("Write a caption...")
                    )
                    .font(.system(size: 15))
                    .foregroundStyle(
                        LegendNextColor.textSecondary
                    )
                    .padding(.top, 8)
                    .padding(.leading, 5)
                    .allowsHitTesting(false)
                }

                TextEditor(text: $caption)
                    .font(.system(size: 15))
                    .foregroundStyle(
                        LegendNextColor.textPrimary
                    )
                    .scrollContentBackground(.hidden)
                    .frame(minHeight: 84)
                    .padding(.horizontal, 0)
                    .padding(.vertical, 0)
                    .background(Color.clear)
                    .accessibilityLabel(
                        LegendLocalized("{value1} caption", context: "accessibility copy", arguments: ["value1": String(describing: (type.displayName))])
                    )
            }
            .frame(maxWidth: .infinity)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    @ViewBuilder
    private var shareThumbnail: some View {
        if let primaryMedia = selectedMedia.first {
            LegendSocialMediaPreview(
                media: primaryMedia,
                presentation: .thumbnail,
                remove: {}
            )
            .frame(
                width: 68,
                height: type.format.usesFixedCanvasAspectRatio ? 88 : 68
            )
            .clipShape(
                RoundedRectangle(
                    cornerRadius: 5,
                    style: .continuous
                )
            )
            .allowsHitTesting(false)
        } else {
            ZStack {
                LegendNextColor.surfaceInset

                Image(systemName: type.systemImage)
                    .font(.system(size: 22, weight: .medium))
                    .foregroundStyle(
                        LegendNextColor.textSecondary
                    )
            }
            .frame(width: 68, height: 68)
            .clipShape(
                RoundedRectangle(
                    cornerRadius: 5,
                    style: .continuous
                )
            )
        }
    }

    private var publicationSettings: some View {
        VStack(spacing: 0) {
            publicationSettingButton(
                icon: "number",
                title: LegendLocalized("Topics & mentions"),
                value: tagsAndMentions.isEmpty ? "Add" : tagsAndMentions,
                action: { detailEditor = .topics })
            publicationSeparator
            publicationSettingButton(
                icon: "mappin.and.ellipse",
                title: LegendLocalized("Location"),
                value: shareLocation.isEmpty ? "Add" : shareLocation,
                action: { detailEditor = .location })

            publicationSeparator
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: "person.2")
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .frame(width: 24)
                Text(LegendLocalized("Audience"))
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Spacer()
                Text(LegendLocalized("Legend network"))
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .frame(minHeight: 52)

            if type.requiresVideo {
                publicationSeparator
                hacCoverSetting
            }

            publicationSeparator
            publicationSettingButton(
                icon: "accessibility",
                title: LegendLocalized("Alt text"),
                value: accessibilityText.isEmpty ? LegendLocalized("Add") : LegendLocalized("Added"),
                action: { detailEditor = .accessibility })
            publicationSeparator

            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: "ellipsis.bubble")
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .frame(width: 24)
                Text(LegendLocalized("Allow comments"))
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Spacer()
                Toggle(LegendLocalized("Allow comments"), isOn: $commentsEnabled)
                    .labelsHidden()
                    .tint(LegendNextColor.goldBright)
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .frame(minHeight: 52)
        }
        .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var publicationSeparator: some View {
        Divider().padding(.leading, 52)
    }

    private func publicationSettingButton(
        icon: String,
        title: String,
        value: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: icon)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .frame(width: 24)
                Text(title)
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Spacer()
                Text(value)
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(1)
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textSecondary.opacity(0.65))
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .frame(minHeight: 52)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var hacCoverSetting: some View {
        if let sourceURL = selectedMedia.first?.videoFileURL {
            Button {
                guard FileManager.default.fileExists(atPath: sourceURL.path) else {
                    mediaSelectionError = "Legend could not read this video cover. Choose the video again and try once more."
                    return
                }
                isSelectingHacPreview = true
            } label: {
                HStack(spacing: LegendNextSpacing.sm) {
                    Group {
                        if let selectedHacPreviewData,
                           let preview = UIImage(data: selectedHacPreviewData) {
                            Image(uiImage: preview).resizable().scaledToFill()
                        } else {
                            Image(systemName: "film")
                                .foregroundStyle(LegendNextColor.goldBright)
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                                .background(LegendNextColor.midnight)
                        }
                    }
                    .frame(width: 28, height: 36)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))

                    Text(LegendLocalized("Cover"))
                        .font(.subheadline)
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Spacer()
                    Text(LegendLocalized("Choose frame"))
                        .font(.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textSecondary.opacity(0.65))
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .frame(minHeight: 52)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Choose Hac cover", context: "accessibility copy"))
        }
    }

    @ViewBuilder
    private var publicationFailure: some View {
        if let failure = social.actionFailure {
            Label(failure.message, systemImage: "exclamationmark.triangle.fill")
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.danger)
        }
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
            activeEditorTool = preferredInitialEditingTool
            stage = .metadata

        case .metadata:
            stage = .share

        case .share:
            publish()

        default:
            break
        }
    }

    private var preferredInitialEditingTool: LegendSocialEditingTool {
        guard let activeMedia else { return .style }
        if activeMedia.isVideo { return .audio }
        return type == .story ? .text : .style
    }

    private func select(_ asset: LegendPhotoLibraryAsset) {
        if let rejection = type.selectionRejection(for: asset) {
            mediaSelectionError = rejection
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
                activeMediaID = media.id
                mediaEdits[media.id] = .initial
                refreshDefaultHacPreview(for: media)
            } catch {
                mediaSelectionError = "Legend could not prepare this media. The draft was kept intact."
            }
            stage = .library
        }
    }

    private func remove(_ media: LegendSocialMediaDraft) {
        selectedMedia.removeAll { $0.id == media.id }
        mediaEdits.removeValue(forKey: media.id)
        if activeMediaID == media.id {
            activeMediaID = selectedMedia.first?.id
        }
        media.discardTemporaryFile()
        if media.isVideo {
            selectedHacPreviewData = nil
        }
    }

    private func addCapturedMedia(_ result: Result<LegendSocialMediaDraft, Error>) {
        switch result {
        case .success(let media):
            guard type.accepts(media) else {
                media.discardTemporaryFile()
                mediaSelectionError = type.requiresVideo
                    ? "A Hac can only contain a playable video."
                    : "The captured media is not available for this update."
                stage = .library
                return
            }
            let previous = selectedMedia
            selectedMedia = [media]
            previous.forEach { $0.discardTemporaryFile() }
            mediaEdits = [media.id: .initial]
            activeMediaID = media.id
            refreshDefaultHacPreview(for: media)
            mediaSelectionError = nil
            stage = .library

        case .failure:
            mediaSelectionError = "The captured media could not be prepared. Your draft was kept intact."
            stage = .library
        }
    }

    private func publish() {
        guard !isPreparingPublication, canPublish else { return }
        isPreparingPublication = true
        mediaSelectionError = nil

        Task {
            do {
                let files = try await renderedPublicationFiles()
                let request = MobileSocialPublishRequest(
                    contentType: type,
                    body: normalizedPublicationBody,
                    files: files,
                    accessibilityText: normalizedAccessibilityText,
                    // Music metadata remains supported for existing content,
                    // but the creator no longer exposes an unrendered music
                    // control. Original audio and trim are committed into the
                    // uploaded video by the shared native media authority.
                    music: nil,
                    audience: .authorizedNetwork,
                    location: normalizedLocation,
                    commentsEnabled: commentsEnabled,
                    previewImage: hacPreviewMultipartFile)

                guard social.beginPublication(request) else {
                    discardGeneratedRenderedFiles(in: files)
                    isPreparingPublication = false
                    return
                }

                // The upload store now owns rendered temporary video files.
                // Release a source only if the final upload is a separately
                // rendered file. When the editor correctly reuses an unchanged
                // source URL, the store owns that file and must keep it until
                // its existing upload/retry lifecycle completes.
                discardSupersededSourceFiles(uploading: files)
                ownsMediaAfterDismissal = true
                stage = .handedOff
                dismiss()
            } catch {
                mediaSelectionError = LegendLocalized(error.localizedDescription)
                isPreparingPublication = false
            }
        }
    }

    private func renderedPublicationFiles() async throws -> [MultipartFormFile] {
        var files: [MultipartFormFile] = []
        for media in selectedMedia {
            let file = try await media.renderedMultipartFile(
                edit: edit(for: media),
                aspectRatio: type == .post
                    ? CGFloat(postCanvas.rawValue)
                    : CGFloat(type.format.mediaAspectRatio))
            files.append(file)
        }
        return files
    }

    private var selectedVideoSourceURLs: Set<URL> {
        Set(selectedMedia.compactMap(\.videoFileURL))
    }

    private func discardGeneratedRenderedFiles(in files: [MultipartFormFile]) {
        for file in files {
            guard case let .file(url) = file.source,
                  !selectedVideoSourceURLs.contains(url) else { continue }
            try? FileManager.default.removeItem(at: url)
        }
    }

    private func discardSupersededSourceFiles(uploading files: [MultipartFormFile]) {
        let uploadURLs = Set(files.compactMap { file -> URL? in
            guard case let .file(url) = file.source else { return nil }
            return url
        })
        for sourceURL in selectedVideoSourceURLs where !uploadURLs.contains(sourceURL) {
            try? FileManager.default.removeItem(at: sourceURL)
        }
    }


    private var normalizedPublicationBody: String {
        [
            caption.trimmingCharacters(
                in: .whitespacesAndNewlines
            ),
            tagsAndMentions.trimmingCharacters(
                in: .whitespacesAndNewlines
            )
        ]
        .filter { !$0.isEmpty }
        .joined(separator: "\n\n")
    }

    private var normalizedAccessibilityText: String? {
        let value = accessibilityText.trimmingCharacters(
            in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private var normalizedLocation: String? {
        let value = shareLocation.trimmingCharacters(
            in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : String(value.prefix(200))
    }

    private var hacPreviewMultipartFile: MultipartFormFile? {
        guard type.requiresVideo,
              let selectedHacPreviewData else {
            return nil
        }

        return MultipartFormFile(
            fieldName: "preview",
            fileName: "legend-hac-preview.jpg",
            mimeType: "image/jpeg",
            data: selectedHacPreviewData)
    }

    private func refreshDefaultHacPreview(for media: LegendSocialMediaDraft) {
        guard type.requiresVideo,
              let sourceURL = media.videoFileURL else {
            selectedHacPreviewData = nil
            return
        }
        selectedHacPreviewData = LegendHacPreviewFrame.jpegData(
            from: sourceURL,
            at: 0)
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
        if hasMeaningfulDraft {
            showsDiscardConfirmation = true
        } else {
            dismiss()
        }
    }

    private var hasMeaningfulDraft: Bool {
        !selectedMedia.isEmpty ||
            !caption.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !accessibilityText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !tagsAndMentions.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !shareLocation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            mediaEdits.values.contains(where: { $0 != .initial })
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
    @StateObject private var previewPlayer = LegendOpenMusicPreviewPlayer()

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.hero
                    .ignoresSafeArea()

                VStack(spacing: 0) {
                    musicHeader

                    ScrollView {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                            musicSearchField

                            Text(query.isEmpty ? LegendLocalized("Christian music") : LegendLocalized("Search results"))
                                .font(LegendNextTypography.section)
                                .foregroundStyle(.white)

                            if isSearching {
                                HStack(spacing: LegendNextSpacing.xs) {
                                    Text(LegendLocalized("Finding music"))
                                        .font(LegendNextTypography.supporting)
                                        .foregroundStyle(Color.white.opacity(0.70))
                                }
                            } else if tracks.isEmpty {
                                Label(LegendLocalized("No matching music found"), systemImage: "music.quarternote.3")
                                    .font(LegendNextTypography.supporting)
                                    .foregroundStyle(Color.white.opacity(0.72))
                                    .frame(maxWidth: .infinity, minHeight: 120)
                                    .background(
                                        Color.white.opacity(0.08),
                                        in: RoundedRectangle(
                                            cornerRadius: LegendNextRadius.card,
                                            style: .continuous))
                            } else {
                                LazyVStack(spacing: LegendNextSpacing.xs) {
                                    ForEach(tracks) { track in
                                        musicResultRow(track)
                                    }
                                }

                            }

                            if case .failed(_, let message) = previewPlayer.state {
                                Label(message, systemImage: "exclamationmark.triangle.fill")
                                    .font(LegendNextTypography.supporting)
                                    .foregroundStyle(Color.white.opacity(0.78))
                                    .padding(LegendNextSpacing.sm)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(
                                        Color.red.opacity(0.18),
                                        in: RoundedRectangle(
                                            cornerRadius: LegendNextRadius.control,
                                            style: .continuous))
                            }

                            if let selectedTrack {
                                mixingControls(for: selectedTrack)
                            }
                        }
                        .padding(LegendNextSpacing.md)
                    }
                    .scrollIndicators(.hidden)
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .onAppear {
            if let selection {
                selectedTrack = selection.track
                trimStart = NSDecimalNumber(decimal: selection.selection.trimStartSeconds).doubleValue
                trimEnd = NSDecimalNumber(decimal: selection.selection.trimEndSeconds).doubleValue
                musicVolume = NSDecimalNumber(decimal: selection.selection.musicVolume).doubleValue
                originalAudioVolume = NSDecimalNumber(decimal: selection.selection.originalAudioVolume).doubleValue
            }

            if tracks.isEmpty {
                search("Christian music")
            }
        }
        .onDisappear(perform: stopPreview)
    }

    private var musicHeader: some View {
        HStack {
            Button(LegendLocalized("Cancel")) {
                stopPreview()
                cancel()
            }
            .font(LegendNextTypography.label)
            .foregroundStyle(LegendNextColor.goldBright)
            .frame(width: LegendNextSize.prominentControlHeight, height: LegendNextSize.prominentControlHeight)
            .accessibilityLabel(LegendLocalized("Cancel music selection", context: "accessibility copy"))

            Spacer()

            Text(LegendLocalized("Add music"))
                .font(LegendNextTypography.section)
                .foregroundStyle(.white)

            Spacer()

            Button(LegendLocalized("Add")) {
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
            .font(LegendNextTypography.label)
            .foregroundStyle(
                selectedTrack != nil && trimEnd > trimStart
                    ? LegendNextColor.goldBright
                    : Color.white.opacity(0.38))
            .frame(width: LegendNextSize.prominentControlHeight, height: LegendNextSize.prominentControlHeight)
            .disabled(selectedTrack == nil || trimEnd <= trimStart)
            .accessibilityLabel(LegendLocalized("Add selected music", context: "accessibility copy"))
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var musicSearchField: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(LegendNextColor.goldBright)
                .accessibilityHidden(true)
            TextField(LegendLocalized("Search Christian music or an artist"), text: $query)
                .font(LegendNextTypography.body)
                .foregroundStyle(.white)
                .tint(LegendNextColor.goldBright)
                .submitLabel(.search)
                .onSubmit { search(query) }
                .accessibilityLabel(LegendLocalized("Search music", context: "accessibility copy"))
            if !query.isEmpty {
                Button {
                    query = ""
                    search("Christian music")
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(Color.white.opacity(0.62))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(LegendLocalized("Clear music search", context: "accessibility copy"))
            }
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .frame(height: LegendNextSize.controlHeight)
        .background(
            Color.white.opacity(0.10),
            in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous)
                .strokeBorder(Color.white.opacity(0.14), lineWidth: 1)
        }
    }

    private func musicResultRow(_ track: MobileSocialMusicTrack) -> some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Button {
                choose(track)
            } label: {
                HStack(spacing: LegendNextSpacing.sm) {
                    LegendSocialMusicArtwork(artistName: track.artistName)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(track.trackTitle)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)
                            .lineLimit(1)
                        Text("\(track.artistName) · \(duration(track.trackDurationSeconds))")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(Color.white.opacity(0.66))
                            .lineLimit(1)
                    }
                    Spacer(minLength: LegendNextSpacing.xs)
                    Image(systemName: selectedTrack?.id == track.id ? "checkmark.circle.fill" : "plus.circle")
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(
                            selectedTrack?.id == track.id
                                ? LegendNextColor.goldBright
                                : Color.white.opacity(0.72))
                }
                .padding(LegendNextSpacing.sm)
                .background(
                    selectedTrack?.id == track.id
                        ? LegendNextColor.goldBright.opacity(0.22)
                        : Color.white.opacity(0.08),
                    in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.control,
                        style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous)
                        .strokeBorder(
                            selectedTrack?.id == track.id
                                ? LegendNextColor.goldBright.opacity(0.72)
                                : Color.white.opacity(0.12),
                            lineWidth: 1)
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Select {value1} by {value2}", context: "accessibility copy", arguments: ["value1": String(describing: (track.trackTitle)), "value2": String(describing: (track.artistName))]))

            if track.audioURL != nil {
                Button {
                    Task {
                        await previewPlayer.toggle(track)
                    }
                } label: {
                    Image(systemName: previewButtonIcon(for: track))
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: LegendNextSize.controlHeight, height: LegendNextSize.controlHeight)
                        .background(LegendNextColor.goldBright, in: Circle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(LegendLocalized("Preview {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (track.trackTitle))]))
            }
        }
    }

    private func previewButtonIcon(for track: MobileSocialMusicTrack) -> String {
        switch previewPlayer.state {
        case .loading(let trackID) where trackID == track.id:
            return "hourglass"
        case .playing(let trackID) where trackID == track.id:
            return "pause.fill"
        default:
            return "play.fill"
        }
    }

    private func mixingControls(for track: MobileSocialMusicTrack) -> some View {
        let totalDuration = max(NSDecimalNumber(decimal: track.trackDurationSeconds).doubleValue, 0.01)
        return VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            Text(LegendLocalized("Clip and audio mix"))
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.white)
            musicSlider("Clip begins", value: $trimStart, range: 0...max(0, trimEnd - 0.01), detail: duration(Decimal(trimStart)))
            musicSlider("Clip ends", value: $trimEnd, range: min(totalDuration, trimStart + 0.01)...totalDuration, detail: duration(Decimal(trimEnd)))
            musicSlider("Music volume", value: $musicVolume, range: 0...1, detail: percentage(musicVolume))
            musicSlider("Original audio", value: $originalAudioVolume, range: 0...1, detail: percentage(originalAudioVolume))
        }
        .padding(LegendNextSpacing.md)
        .background(
            Color.white.opacity(0.10),
            in: RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous)
                .strokeBorder(Color.white.opacity(0.14), lineWidth: 1)
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
                    .foregroundStyle(Color.white.opacity(0.68))
                Spacer()
                Text(detail)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.white)
            }
            Slider(value: value, in: range)
                .tint(LegendNextColor.gold)
        }
    }

    private func search(_ rawValue: String) {
        let value = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
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

    private func stopPreview() {
        previewPlayer.stop()
    }

    private func duration(_ seconds: Decimal) -> String {
        let total = Int(NSDecimalNumber(decimal: seconds).doubleValue.rounded())
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    private func percentage(_ value: Double) -> String {
        "\(Int((value * 100).rounded()))%"
    }
}

private struct LegendSocialMusicArtwork: View {
    let artistName: String

    private var monogram: String {
        let letters = artistName
            .split(whereSeparator: { $0.isWhitespace })
            .prefix(2)
            .compactMap(\.first)
        let value = String(letters)
        return value.isEmpty ? "L" : value.uppercased()
    }

    var body: some View {
        Text(monogram)
            .font(.caption.weight(.bold))
            .foregroundStyle(LegendNextColor.midnight)
            .frame(width: LegendNextSize.controlHeight, height: LegendNextSize.controlHeight)
            .background(LegendNextColor.goldBright, in: Circle())
            .accessibilityLabel(LegendLocalized("{value1} artist monogram", context: "accessibility copy", arguments: ["value1": String(describing: (artistName))]))
    }
}

/// One serializable-in-memory edit authority for the active creation session.
/// The source remains untouched while the member explores. At publish time the
/// renderer below produces the actual uploaded bytes from this state, so the
/// final audience never sees a client-only interpretation.
struct LegendSocialMediaEditState: Equatable {
    var filter: LegendSocialImageFilter = .original
    var filterIntensity = 1.0
    var brightness = 0.0
    var contrast = 0.0
    var saturation = 0.0
    var warmth = 0.0
    var highlights = 0.0
    var shadows = 0.0
    var sharpness = 0.0
    var cropZoom = 1.0
    var cropOffset = CGSize.zero
    var rotationDegrees = 0.0
    var storyOverlay = LegendSocialStoryOverlay()
    var video = LegendSocialVideoEdit()

    static let initial = LegendSocialMediaEditState()

    mutating func resetTransform() {
        cropZoom = 1
        cropOffset = .zero
        rotationDegrees = 0
    }

    mutating func resetAdjustments() {
        brightness = 0
        contrast = 0
        saturation = 0
        warmth = 0
        highlights = 0
        shadows = 0
        sharpness = 0
        filter = .original
        filterIntensity = 1
    }
}

struct LegendSocialVideoEdit: Equatable {
    var trimStartSeconds = 0.0
    var trimEndSeconds: Double?
    var isOriginalAudioMuted = false

    var selectedRange: ClosedRange<Double>? {
        guard let trimEndSeconds,
              trimEndSeconds > trimStartSeconds else {
            return nil
        }
        return trimStartSeconds ... trimEndSeconds
    }

    func requiresRendering(for duration: Double) -> Bool {
        isOriginalAudioMuted ||
            trimStartSeconds > 0.01 ||
            (trimEndSeconds ?? duration) < duration - 0.01
    }
}

enum LegendSocialImageFilter: String, CaseIterable, Identifiable {
    case original
    case clean
    case warm
    case cool
    case rich
    case golden
    case soft
    case contrast
    case mono

    var id: String { rawValue }

    var title: String {
        switch self {
        case .original: LegendLocalized("Original")
        case .clean: LegendLocalized("Clean")
        case .warm: LegendLocalized("Warm")
        case .cool: LegendLocalized("Cool")
        case .rich: LegendLocalized("Rich")
        case .golden: LegendLocalized("Golden")
        case .soft: LegendLocalized("Soft")
        case .contrast: LegendLocalized("Contrast")
        case .mono: LegendLocalized("Mono")
        }
    }
}

enum LegendSocialStoryTextColor: String, CaseIterable, Identifiable {
    case white
    case gold
    case navy
    case sky
    case rose

    var id: String { rawValue }
    var title: String { rawValue.capitalized }

    var swiftUIColor: Color {
        switch self {
        case .white: .white
        case .gold: LegendNextColor.goldBright
        case .navy: LegendNextColor.midnight
        case .sky: LegendNextColor.information
        case .rose: Color(red: 0.93, green: 0.35, blue: 0.49)
        }
    }

    var uiColor: UIColor { UIColor(swiftUIColor) }
}

struct LegendSocialStoryOverlay: Equatable {
    var text = ""
    var position = CGSize.zero
    var scale = 1.0
    var color: LegendSocialStoryTextColor = .white

    var hasText: Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

private enum LegendSocialImageAdjustment: CaseIterable, Identifiable {
    case brightness
    case contrast
    case saturation
    case warmth
    case highlights
    case shadows
    case sharpness

    var id: Self { self }

    var title: String {
        switch self {
        case .brightness: LegendLocalized("Brightness")
        case .contrast: LegendLocalized("Contrast")
        case .saturation: LegendLocalized("Saturation")
        case .warmth: LegendLocalized("Warmth")
        case .highlights: LegendLocalized("Highlights")
        case .shadows: LegendLocalized("Shadows")
        case .sharpness: LegendLocalized("Sharpness")
        }
    }

    var keyPath: WritableKeyPath<LegendSocialMediaEditState, Double> {
        switch self {
        case .brightness: \.brightness
        case .contrast: \.contrast
        case .saturation: \.saturation
        case .warmth: \.warmth
        case .highlights: \.highlights
        case .shadows: \.shadows
        case .sharpness: \.sharpness
        }
    }

    var range: ClosedRange<Double> {
        self == .sharpness ? 0...1 : -1...1
    }
}

private enum LegendSocialPublicationDetail: String, Identifiable {
    case topics
    case location
    case accessibility

    var id: String { rawValue }

    var title: String {
        switch self {
        case .topics: LegendLocalized("Topics & mentions")
        case .location: LegendLocalized("Location")
        case .accessibility: LegendLocalized("Alt text")
        }
    }

    var prompt: String {
        switch self {
        case .topics: LegendLocalized("Add #topics or @mentions")
        case .location: LegendLocalized("Add a location")
        case .accessibility: LegendLocalized("Describe this media")
        }
    }

    var isMultiline: Bool {
        self != .location
    }
}

private struct LegendSocialPublicationDetailSheet: View {
    let detail: LegendSocialPublicationDetail
    @Binding var tagsAndMentions: String
    @Binding var location: String
    @Binding var accessibilityText: String
    @Environment(\.dismiss) private var dismiss

    private var value: Binding<String> {
        switch detail {
        case .topics: $tagsAndMentions
        case .location: $location
        case .accessibility: $accessibilityText
        }
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                if detail.isMultiline {
                    TextEditor(text: value)
                        .font(.body)
                        .scrollContentBackground(.hidden)
                        .frame(minHeight: 156)
                        .padding(LegendNextSpacing.sm)
                        .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                } else {
                    TextField(detail.prompt, text: value)
                        .font(.body)
                        .padding(LegendNextSpacing.sm)
                        .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.canvas)
            .navigationTitle(detail.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(LegendLocalized("Done")) { dismiss() }
                        .fontWeight(.semibold)
                }
            }
        }
        .presentationDetents([.medium])
    }
}

/// Editing tools are present only when they mutate the final image/video that
/// is submitted through the existing social upload authority.
private enum LegendSocialEditingTool: CaseIterable, Identifiable {
    case audio
    case transform
    case style
    case adjust
    case text

    var id: Self { self }

    var title: String {
        switch self {
        case .audio: LegendLocalized("Audio")
        case .transform: LegendLocalized("Crop")
        case .style: LegendLocalized("Filters")
        case .adjust: LegendLocalized("Adjust")
        case .text: LegendLocalized("Text")
        }
    }

    var systemImage: String {
        switch self {
        case .audio: "music.note"
        case .transform: "crop"
        case .style: "camera.filters"
        case .adjust: "slider.horizontal.3"
        case .text: "textformat"
        }
    }

    func isAvailable(
        for media: LegendSocialMediaDraft,
        contentType: MobileSocialContentType
    ) -> Bool {
        switch self {
        case .audio:
            media.isVideo
        case .transform, .style, .adjust:
            !media.isVideo
        case .text:
            contentType == .story && !media.isVideo
        }
    }
}

private struct LegendSocialAdjustmentSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    var compact = false

    var body: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.white)
                .frame(width: compact ? 32 : 72, alignment: .leading)
            Slider(value: $value, in: range)
                .tint(LegendNextColor.goldBright)
            Text(displayValue)
                .font(.caption2.monospacedDigit())
                .foregroundStyle(Color.white.opacity(0.72))
                .frame(width: 34, alignment: .trailing)
        }
    }

    private var displayValue: String {
        if range.lowerBound >= 0, range.upperBound <= 2 {
            return "\(Int((value * 100).rounded()))%"
        }
        return String(format: "%+.1f", value)
    }
}

/// The image renderer is shared by editing preview, final review, and upload.
/// Keeping those three consumers on this one path prevents a visual-only
/// filter/crop/overlay from being shown in the composer and then disappearing
/// once the Post or Story is published.
enum LegendSocialMediaRenderer {
    private static let imageContext = CIContext(options: nil)

    static func renderedImage(
        from data: Data,
        edit: LegendSocialMediaEditState,
        aspectRatio: CGFloat
    ) -> UIImage? {
        guard let source = UIImage(data: data),
              source.size.width > 0,
              source.size.height > 0,
              aspectRatio.isFinite,
              aspectRatio > 0 else {
            return nil
        }

        let longestEdge = min(max(source.size.width, source.size.height), 2_048)
        let targetSize = CGSize(
            width: max(1, longestEdge.rounded()),
            height: max(1, (longestEdge / aspectRatio).rounded()))
        let format = UIGraphicsImageRendererFormat.default()
        format.scale = 1
        format.opaque = true
        let base = UIGraphicsImageRenderer(size: targetSize, format: format).image { context in
            UIColor.black.setFill()
            context.fill(CGRect(origin: .zero, size: targetSize))

            let sourceScale = max(
                targetSize.width / source.size.width,
                targetSize.height / source.size.height)
                * max(1, edit.cropZoom)
            let drawSize = CGSize(
                width: source.size.width * sourceScale,
                height: source.size.height * sourceScale)

            context.cgContext.saveGState()
            context.cgContext.translateBy(
                x: targetSize.width / 2 + edit.cropOffset.width * targetSize.width * 0.28,
                y: targetSize.height / 2 + edit.cropOffset.height * targetSize.height * 0.28)
            context.cgContext.rotate(by: edit.rotationDegrees * .pi / 180)
            source.draw(in: CGRect(
                x: -drawSize.width / 2,
                y: -drawSize.height / 2,
                width: drawSize.width,
                height: drawSize.height))
            context.cgContext.restoreGState()
        }

        let styled = applyingAppearance(to: base, edit: edit) ?? base
        guard edit.storyOverlay.hasText else { return styled }
        return renderingStoryOverlay(edit.storyOverlay, on: styled)
    }

    static func jpegData(
        from data: Data,
        edit: LegendSocialMediaEditState,
        aspectRatio: CGFloat
    ) -> Data? {
        renderedImage(from: data, edit: edit, aspectRatio: aspectRatio)?
            .jpegData(compressionQuality: 0.9)
    }

    private static func applyingAppearance(
        to image: UIImage,
        edit: LegendSocialMediaEditState
    ) -> UIImage? {
        guard var output = CIImage(image: image) else { return nil }
        let intensity = min(max(edit.filterIntensity, 0), 1)
        let recipe = appearanceRecipe(for: edit.filter, intensity: intensity)

        if let controls = CIFilter(name: "CIColorControls") {
            controls.setValue(output, forKey: kCIInputImageKey)
            controls.setValue(edit.brightness + recipe.brightness, forKey: kCIInputBrightnessKey)
            controls.setValue(max(0.1, 1 + edit.contrast + recipe.contrast), forKey: kCIInputContrastKey)
            controls.setValue(max(0, 1 + edit.saturation + recipe.saturation), forKey: kCIInputSaturationKey)
            if let filtered = controls.outputImage { output = filtered }
        }

        if let exposure = CIFilter(name: "CIExposureAdjust") {
            exposure.setValue(output, forKey: kCIInputImageKey)
            exposure.setValue(recipe.exposure, forKey: kCIInputEVKey)
            if let filtered = exposure.outputImage { output = filtered }
        }

        if abs(edit.warmth + recipe.warmth) > 0.001,
           let temperature = CIFilter(name: "CITemperatureAndTint") {
            temperature.setValue(output, forKey: kCIInputImageKey)
            temperature.setValue(CIVector(x: 6_500, y: 0), forKey: "inputNeutral")
            temperature.setValue(
                CIVector(x: 6_500 + (edit.warmth + recipe.warmth) * 2_000, y: 0),
                forKey: "inputTargetNeutral")
            if let filtered = temperature.outputImage { output = filtered }
        }

        if abs(edit.highlights) > 0.001 || abs(edit.shadows) > 0.001,
           let highlights = CIFilter(name: "CIHighlightShadowAdjust") {
            highlights.setValue(output, forKey: kCIInputImageKey)
            highlights.setValue(max(0, 1 - edit.highlights), forKey: "inputHighlightAmount")
            highlights.setValue(max(0, 1 + edit.shadows), forKey: "inputShadowAmount")
            if let filtered = highlights.outputImage { output = filtered }
        }

        if edit.sharpness > 0.001,
           let sharpen = CIFilter(name: "CISharpenLuminance") {
            sharpen.setValue(output, forKey: kCIInputImageKey)
            sharpen.setValue(edit.sharpness * 2, forKey: "inputSharpness")
            if let filtered = sharpen.outputImage { output = filtered }
        }

        guard let cgImage = imageContext.createCGImage(output, from: output.extent) else {
            return nil
        }
        return UIImage(cgImage: cgImage)
    }

    private static func appearanceRecipe(
        for filter: LegendSocialImageFilter,
        intensity: Double
    ) -> (brightness: Double, contrast: Double, saturation: Double, warmth: Double, exposure: Double) {
        let raw: (Double, Double, Double, Double, Double)
        switch filter {
        case .original: raw = (0, 0, 0, 0, 0)
        case .clean: raw = (0.02, 0.06, 0.03, 0, 0.03)
        case .warm: raw = (0.02, 0.04, 0.06, 0.36, 0.04)
        case .cool: raw = (0, 0.04, 0.02, -0.32, 0)
        case .rich: raw = (-0.01, 0.18, 0.22, 0.04, 0.02)
        case .golden: raw = (0.04, 0.10, 0.10, 0.48, 0.08)
        case .soft: raw = (0.05, -0.14, -0.08, 0.10, 0.04)
        case .contrast: raw = (0, 0.30, -0.03, 0, 0)
        case .mono: raw = (0, 0.10, -1, 0, 0)
        }
        return (raw.0 * intensity, raw.1 * intensity, raw.2 * intensity, raw.3 * intensity, raw.4 * intensity)
    }

    private static func renderingStoryOverlay(
        _ overlay: LegendSocialStoryOverlay,
        on image: UIImage
    ) -> UIImage {
        let renderer = UIGraphicsImageRenderer(size: image.size)
        return renderer.image { _ in
            image.draw(in: CGRect(origin: .zero, size: image.size))
            let fontSize = max(20, image.size.width * 0.078 * overlay.scale)
            let paragraph = NSMutableParagraphStyle()
            paragraph.alignment = .center
            let attributes: [NSAttributedString.Key: Any] = [
                .font: UIFont.systemFont(ofSize: fontSize, weight: .bold),
                .foregroundColor: overlay.color.uiColor,
                .paragraphStyle: paragraph,
                .shadow: {
                    let shadow = NSShadow()
                    shadow.shadowColor = UIColor.black.withAlphaComponent(0.58)
                    shadow.shadowBlurRadius = 5
                    shadow.shadowOffset = CGSize(width: 0, height: 2)
                    return shadow
                }()
            ]
            let width = image.size.width * 0.80
            let origin = CGPoint(
                x: (image.size.width - width) / 2 + overlay.position.width * image.size.width * 0.25,
                y: image.size.height * 0.45 + overlay.position.height * image.size.height * 0.25)
            (overlay.text as NSString).draw(
                in: CGRect(x: origin.x, y: origin.y, width: width, height: image.size.height * 0.42),
                withAttributes: attributes)
        }
    }
}

private struct LegendSocialEditableImageCanvas: View {
    let media: LegendSocialMediaDraft
    @Binding var edit: LegendSocialMediaEditState
    let aspectRatio: CGFloat
    let allowsStoryOverlay: Bool

    @State private var cropStart = CGSize.zero
    @State private var zoomStart = 1.0
    @State private var textStart = CGSize.zero

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                if let data = media.imageData,
                   let rendered = LegendSocialMediaRenderer.renderedImage(
                    from: data,
                    edit: canvasEdit,
                    aspectRatio: aspectRatio) {
                    Image(uiImage: rendered)
                        .resizable()
                        .scaledToFill()
                        .frame(width: geometry.size.width, height: geometry.size.height)
                        .clipped()
                } else {
                    Image(systemName: "photo")
                        .font(.largeTitle)
                        .foregroundStyle(.white.opacity(0.6))
                }

                if allowsStoryOverlay, edit.storyOverlay.hasText {
                    Text(edit.storyOverlay.text)
                        .font(.system(
                            size: max(18, geometry.size.width * 0.078 * edit.storyOverlay.scale),
                            weight: .bold))
                        .multilineTextAlignment(.center)
                        .foregroundStyle(edit.storyOverlay.color.swiftUIColor)
                        .shadow(color: .black.opacity(0.58), radius: 4, y: 2)
                        .frame(maxWidth: geometry.size.width * 0.80)
                        .position(
                            x: geometry.size.width / 2 + edit.storyOverlay.position.width * geometry.size.width * 0.25,
                            y: geometry.size.height * 0.52 + edit.storyOverlay.position.height * geometry.size.height * 0.25)
                        .gesture(
                            DragGesture()
                                .onChanged { value in
                                    edit.storyOverlay.position = CGSize(
                                        width: textStart.width + value.translation.width / max(geometry.size.width, 1),
                                        height: textStart.height + value.translation.height / max(geometry.size.height, 1))
                                }
                                .onEnded { _ in textStart = edit.storyOverlay.position })
                }
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture()
                    .onChanged { value in
                        edit.cropOffset = CGSize(
                            width: cropStart.width + value.translation.width / max(geometry.size.width, 1),
                            height: cropStart.height + value.translation.height / max(geometry.size.height, 1))
                    }
                    .onEnded { _ in cropStart = edit.cropOffset })
            .simultaneousGesture(
                MagnificationGesture()
                    .onChanged { value in
                        edit.cropZoom = min(max(1, zoomStart * value), 3)
                    }
                    .onEnded { _ in zoomStart = edit.cropZoom })
            .onAppear {
                cropStart = edit.cropOffset
                zoomStart = edit.cropZoom
                textStart = edit.storyOverlay.position
            }
        }
    }

    private var canvasEdit: LegendSocialMediaEditState {
        var value = edit
        value.storyOverlay.text = ""
        return value
    }
}

private struct LegendSocialRenderedImagePreview: View {
    let media: LegendSocialMediaDraft
    let edit: LegendSocialMediaEditState
    let aspectRatio: CGFloat

    var body: some View {
        Group {
            if let data = media.imageData,
               let rendered = LegendSocialMediaRenderer.renderedImage(
                from: data,
                edit: edit,
                aspectRatio: aspectRatio) {
                Image(uiImage: rendered)
                    .resizable()
                    .scaledToFit()
            } else {
                ContentUnavailableView("Media unavailable", systemImage: "photo")
            }
        }
        .frame(maxWidth: .infinity)
        .clipShape(RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
    }
}

private struct LegendSocialPlayableVideoPreview: View {
    let sourceURL: URL
    let trim: LegendSocialVideoEdit
    let isMuted: Bool
    var showsAudioControl = true
    let onMutedChanged: (Bool) -> Void

    @State private var player: AVPlayer?
    @State private var duration = 0.0
    @State private var position = 0.0
    @State private var isPlaying = false
    @State private var failureMessage: String?
    @State private var timeObserver: Any?

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black

                if let player {
                    VideoPlayer(player: player)
                        .allowsHitTesting(false)
                } else if let failureMessage {
                    ContentUnavailableView(
                        "Video unavailable",
                        systemImage: "exclamationmark.triangle",
                        description: Text(failureMessage))
                        .foregroundStyle(.white)
                } else {
                    ProgressView()
                        .tint(.white)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            if let player, duration > 0 {
                VStack(spacing: LegendNextSpacing.xs) {
                    Slider(
                        value: Binding(
                            get: { position },
                            set: { seek(player, to: $0) }),
                        in: playableRange)
                    .tint(LegendNextColor.goldBright)

                    HStack(spacing: LegendNextSpacing.sm) {
                        Button {
                            isPlaying ? pause(player) : play(player)
                        } label: {
                            Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                                .frame(width: 34, height: 30)
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.white)
                        .accessibilityLabel(
                            isPlaying
                                ? LegendLocalized("Pause preview", context: "accessibility copy")
                                : LegendLocalized("Play preview", context: "accessibility copy"))

                        Text("\(LegendHacPreviewFrame.timeLabel(position)) / \(LegendHacPreviewFrame.timeLabel(playableRange.upperBound))")
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.white.opacity(0.76))

                        if showsAudioControl {
                            Spacer(minLength: 0)

                            Button {
                                player.isMuted.toggle()
                                onMutedChanged(player.isMuted)
                            } label: {
                                Image(systemName: player.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                                    .frame(width: 34, height: 30)
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(.white)
                            .accessibilityLabel(
                                player.isMuted
                                    ? LegendLocalized("Enable original audio", context: "accessibility copy")
                                    : LegendLocalized("Mute original audio", context: "accessibility copy"))
                        }
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.xs)
                .background(LegendNextColor.midnight.opacity(0.92))
            }
        }
        .background(Color.black)
        .clipShape(RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
        .task(id: sourceURL) { await loadPlayer() }
        .onChange(of: trim) { _, _ in applyTrim() }
        .onChange(of: isMuted) { _, muted in player?.isMuted = muted }
        .onDisappear { tearDown() }
    }

    private var playableRange: ClosedRange<Double> {
        let lower = min(max(0, trim.trimStartSeconds), max(0, duration - 0.01))
        let requestedEnd = trim.trimEndSeconds ?? duration
        let upper = max(lower + 0.01, min(max(requestedEnd, lower + 0.01), duration))
        return lower ... upper
    }

    private func loadPlayer() async {
        tearDown()
        let asset = AVURLAsset(url: sourceURL)
        do {
            let loadedDuration = try await asset.load(.duration).seconds
            guard loadedDuration.isFinite, loadedDuration > 0 else {
                failureMessage = "This video has no playable duration."
                return
            }
            duration = loadedDuration
            let newPlayer = AVPlayer(url: sourceURL)
            newPlayer.isMuted = isMuted
            player = newPlayer
            installObserver(on: newPlayer)
            applyTrim()
        } catch {
            failureMessage = "Legend could not open this video preview."
        }
    }

    private func installObserver(on player: AVPlayer) {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.15, preferredTimescale: 600),
            queue: .main) { time in
                let seconds = time.seconds
                guard seconds.isFinite else { return }
                if seconds >= playableRange.upperBound - 0.02 {
                    pause(player)
                    seek(player, to: playableRange.lowerBound)
                    return
                }
                position = min(max(seconds, playableRange.lowerBound), playableRange.upperBound)
            }
    }

    private func applyTrim() {
        guard let player, duration > 0 else { return }
        player.currentItem?.forwardPlaybackEndTime = CMTime(
            seconds: playableRange.upperBound,
            preferredTimescale: 600)
        seek(player, to: playableRange.lowerBound)
    }

    private func play(_ player: AVPlayer) {
        if position >= playableRange.upperBound - 0.02 {
            seek(player, to: playableRange.lowerBound)
        }
        player.play()
        isPlaying = true
    }

    private func pause(_ player: AVPlayer) {
        player.pause()
        isPlaying = false
    }

    private func seek(_ player: AVPlayer, to seconds: Double) {
        let clamped = min(max(seconds, playableRange.lowerBound), playableRange.upperBound)
        player.seek(
            to: CMTime(seconds: clamped, preferredTimescale: 600),
            toleranceBefore: .zero,
            toleranceAfter: .zero)
        position = clamped
    }

    private func tearDown() {
        if let timeObserver, let player {
            player.removeTimeObserver(timeObserver)
        }
        timeObserver = nil
        player?.pause()
        player = nil
        isPlaying = false
    }
}

private struct LegendSocialVideoTrimSheet: View {
    let sourceURL: URL
    let selection: LegendSocialVideoEdit
    let save: (LegendSocialVideoEdit) -> Void
    let cancel: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var player: AVPlayer
    @State private var duration = 0.0
    @State private var startSeconds: Double
    @State private var endSeconds: Double

    init(
        sourceURL: URL,
        selection: LegendSocialVideoEdit,
        save: @escaping (LegendSocialVideoEdit) -> Void,
        cancel: @escaping () -> Void
    ) {
        self.sourceURL = sourceURL
        self.selection = selection
        self.save = save
        self.cancel = cancel
        _player = State(initialValue: AVPlayer(url: sourceURL))
        _startSeconds = State(initialValue: selection.trimStartSeconds)
        _endSeconds = State(initialValue: selection.trimEndSeconds ?? 0)
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: LegendNextSpacing.md) {
                VideoPlayer(player: player)
                    .aspectRatio(9 / 16, contentMode: .fit)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))

                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    HStack {
                        Text(LegendLocalized("Trim"))
                            .font(.headline.weight(.semibold))
                        Spacer()
                        Text("\(LegendHacPreviewFrame.timeLabel(startSeconds)) – \(LegendHacPreviewFrame.timeLabel(endSeconds))")
                            .font(.caption.monospacedDigit().weight(.semibold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    LegendSocialVideoRangeTimeline(
                        sourceURL: sourceURL,
                        duration: duration,
                        startSeconds: $startSeconds,
                        endSeconds: $endSeconds,
                        rangeChanged: previewRange)
                        .frame(height: 76)

                    HStack {
                        Button(LegendLocalized("Preview"), systemImage: "play.fill") {
                            player.currentItem?.forwardPlaybackEndTime = CMTime(
                                seconds: endSeconds,
                                preferredTimescale: 600)
                            player.seek(to: CMTime(seconds: startSeconds, preferredTimescale: 600))
                            player.play()
                        }
                        Spacer()
                        Button(LegendLocalized("Reset")) {
                            startSeconds = 0
                            endSeconds = duration
                            previewRange()
                        }
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.goldBright)
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.midnight)
            .navigationTitle(LegendLocalized("Trim"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(LegendLocalized("Cancel")) {
                        player.pause()
                        cancel()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(LegendLocalized("Save")) {
                        player.pause()
                        save(LegendSocialVideoEdit(
                            trimStartSeconds: startSeconds,
                            trimEndSeconds: endSeconds,
                            isOriginalAudioMuted: selection.isOriginalAudioMuted))
                        dismiss()
                    }
                    .fontWeight(.semibold)
                }
            }
            .task {
                duration = await LegendHacPreviewFrame.duration(of: sourceURL)
                if endSeconds <= 0 || endSeconds > duration { endSeconds = duration }
                startSeconds = min(max(0, startSeconds), max(0, endSeconds - 0.05))
                previewRange()
            }
            .onDisappear { player.pause() }
        }
        .preferredColorScheme(.dark)
    }

    private func previewRange() {
        player.pause()
        player.currentItem?.forwardPlaybackEndTime = CMTime(
            seconds: endSeconds,
            preferredTimescale: 600)
        player.seek(to: CMTime(seconds: startSeconds, preferredTimescale: 600))
    }
}

private struct LegendSocialVideoRangeTimeline: View {
    let sourceURL: URL
    let duration: Double
    @Binding var startSeconds: Double
    @Binding var endSeconds: Double
    let rangeChanged: () -> Void
    @State private var frames: [Data] = []

    private let minimumSelection = 0.05

    var body: some View {
        GeometryReader { geometry in
            let width = max(1, geometry.size.width)
            let safeDuration = max(duration, minimumSelection)
            let startX = CGFloat(startSeconds / safeDuration) * width
            let endX = CGFloat(endSeconds / safeDuration) * width

            ZStack(alignment: .leading) {
                Color.black

                HStack(spacing: 1) {
                    ForEach(Array(frames.enumerated()), id: \.offset) { _, data in
                        Group {
                            if let image = UIImage(data: data) {
                                Image(uiImage: image).resizable().scaledToFill()
                            } else {
                                Color.black
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .clipped()
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.20), lineWidth: 1)
                }

                Color.black.opacity(0.46)
                    .frame(width: max(0, startX))
                    .allowsHitTesting(false)
                Color.black.opacity(0.46)
                    .frame(width: max(0, width - endX))
                    .offset(x: endX)
                    .allowsHitTesting(false)

                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(LegendNextColor.goldBright, lineWidth: 3)
                    .frame(width: max(14, endX - startX), height: geometry.size.height)
                    .offset(x: startX)
                    .allowsHitTesting(false)

                trimHandle(at: startX, width: width, isStart: true)
                trimHandle(at: endX, width: width, isStart: false)
            }
        }
        .task(id: duration) {
            guard duration > 0 else { return }
            frames = await LegendHacPreviewFrame.timelineFrames(
                from: sourceURL,
                range: 0...duration,
                count: 10)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(LegendLocalized("Trim range from {value1} to {value2}", context: "accessibility copy", arguments: ["value1": String(describing: (LegendHacPreviewFrame.timeLabel(startSeconds))), "value2": String(describing: (LegendHacPreviewFrame.timeLabel(endSeconds)))]))
    }

    private func trimHandle(at x: CGFloat, width: CGFloat, isStart: Bool) -> some View {
        Capsule()
            .fill(LegendNextColor.goldBright)
            .frame(width: 14, height: 42)
            .shadow(color: .black.opacity(0.28), radius: 3, y: 1)
            .position(x: min(max(7, x), width - 7), y: 38)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let seconds = min(max(0, Double(value.location.x / width) * duration), duration)
                        if isStart {
                            startSeconds = min(seconds, endSeconds - minimumSelection)
                        } else {
                            endSeconds = max(seconds, startSeconds + minimumSelection)
                        }
                        rangeChanged()
                    })
    }
}

private enum LegendSocialMediaPreviewPresentation {
    case selection
    case thumbnail
}

private struct LegendSocialMediaPreview: View {
    let media: LegendSocialMediaDraft
    let presentation: LegendSocialMediaPreviewPresentation
    let remove: () -> Void

    init(
        media: LegendSocialMediaDraft,
        presentation: LegendSocialMediaPreviewPresentation,
        remove: @escaping () -> Void
    ) {
        self.media = media
        self.presentation = presentation
        self.remove = remove
    }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            preview
                .frame(
                    maxWidth: .infinity,
                    maxHeight: .infinity
                )
                .clipped()
                .background(LegendNextColor.midnight.opacity(0.16))

            if presentation == .selection {
                Button(action: remove) {
                    Image(systemName: "xmark")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: 30, height: 30)
                        .background(.white, in: Circle())
                }
                .padding(LegendNextSpacing.xs)
                .accessibilityLabel(LegendLocalized("Remove selected media", context: "accessibility copy"))
            }
        }
        .clipShape(
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous)
                .strokeBorder(Color.white.opacity(0.36), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(LegendLocalized("Selected {value1}: {value2}", context: "accessibility copy", arguments: ["value1": String(describing: (media.kindDescription)), "value2": String(describing: (media.fileName))]))
    }

    @ViewBuilder
    private var preview: some View {
        switch media {
        case .image(_, let data, _, _, _):
            if let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity
                    )
                    .clipped()
            } else {
                mediaPlaceholder
            }

        case .video(_, let fileURL, _, _, _):
            VideoPlayer(player: AVPlayer(url: fileURL))
                .aspectRatio(contentMode: .fill)
                .frame(
                    maxWidth: .infinity,
                    maxHeight: .infinity
                )
                .clipped()
                .allowsHitTesting(false)
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

    var videoFileURL: URL? {
        guard case .video(_, let fileURL, _, _, _) = self else { return nil }
        return fileURL
    }

    var imageData: Data? {
        guard case .image(_, let data, _, _, _) = self else { return nil }
        return data
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

    /// Builds the one final media payload used by the established mobile
    /// social upload contract. Image edits are flattened into the JPEG and
    /// video trim/audio edits are exported by `LegendSocialVideoPreparation`.
    /// The selected source itself is never mutated in the editor.
    func renderedMultipartFile(
        edit: LegendSocialMediaEditState,
        aspectRatio: CGFloat
    ) async throws -> MultipartFormFile {
        switch self {
        case .image(_, let data, _, _, _):
            guard let rendered = LegendSocialMediaRenderer.jpegData(
                from: data,
                edit: edit,
                aspectRatio: aspectRatio) else {
                throw LegendSocialMediaLoadingError.unavailable
            }
            return MultipartFormFile(
                fieldName: "files",
                fileName: "legend-edited-image.jpg",
                mimeType: "image/jpeg",
                data: rendered)

        case .video(_, let fileURL, _, _, _):
            let outputURL = try await LegendSocialVideoPreparation
                .prepareEditedForPublication(
                    from: fileURL,
                    trimStartSeconds: edit.video.trimStartSeconds,
                    trimEndSeconds: edit.video.trimEndSeconds,
                    muteOriginalAudio: edit.video.isOriginalAudioMuted)
            return MultipartFormFile(
                fieldName: "files",
                fileName: "legend-video.mp4",
                mimeType: "video/mp4",
                fileURL: outputURL)
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

    static func video(from sourceURL: URL) async throws -> LegendSocialMediaDraft {
        let preparedURL = try await LegendSocialVideoPreparation
            .prepareForPublication(from: sourceURL)
        return .video(UUID(), preparedURL, "video/mp4", "legend-video.mp4", nil)
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
    ) async throws -> LegendSocialMediaDraft {
        let destination = try await LegendSocialVideoPreparation
            .prepareForPublication(from: sourceURL)
        return .video(
            UUID(),
            destination,
            "video/mp4",
            "legend-video.mp4",
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

}

/// A compact, local frame picker. It uses the prepared MP4 rather than an
/// independently rendered copy, which keeps the exact creator-selected frame
/// aligned with the video ultimately sent to the server.
private struct LegendHacPreviewSelector: View {
    let sourceURL: URL
    let selectedRange: ClosedRange<Double>?
    let save: (Data) -> Void
    let cancel: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var player: AVPlayer
    @State private var duration: Double = 1
    @State private var selectedSeconds: Double = 0

    init(
        sourceURL: URL,
        selectedRange: ClosedRange<Double>? = nil,
        save: @escaping (Data) -> Void,
        cancel: @escaping () -> Void
    ) {
        self.sourceURL = sourceURL
        self.selectedRange = selectedRange
        self.save = save
        self.cancel = cancel
        _player = State(initialValue: AVPlayer(url: sourceURL))
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: LegendNextSpacing.md) {
                VideoPlayer(player: player)
                    .aspectRatio(9 / 16, contentMode: .fit)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))

                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    HStack {
                        Text(LegendLocalized("Cover"))
                            .font(.headline.weight(.semibold))
                        Spacer()
                        Text(LegendHacPreviewFrame.timeLabel(selectedSeconds))
                            .font(.caption.monospacedDigit().weight(.semibold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    LegendHacCoverTimeline(
                        sourceURL: sourceURL,
                        range: selectableRange,
                        selectedSeconds: $selectedSeconds)
                        .frame(height: 76)
                        .onChange(of: selectedSeconds) { _, seconds in
                        player.seek(
                            to: CMTime(seconds: seconds, preferredTimescale: 600),
                            toleranceBefore: .zero,
                            toleranceAfter: .zero)
                    }
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.midnight)
            .navigationTitle(LegendLocalized("Cover"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(LegendLocalized("Cancel")) {
                        player.pause()
                        cancel()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(LegendLocalized("Use frame")) {
                        if let imageData = LegendHacPreviewFrame.jpegData(
                            from: sourceURL,
                            at: selectedSeconds) {
                            player.pause()
                            save(imageData)
                            dismiss()
                        }
                    }
                    .fontWeight(.semibold)
                }
            }
            .task {
                duration = await LegendHacPreviewFrame.duration(of: sourceURL)
                selectedSeconds = selectableRange.lowerBound
                await player.seek(to: CMTime(
                    seconds: selectableRange.lowerBound,
                    preferredTimescale: 600))
            }
            .onDisappear {
                player.pause()
            }
        }
        .preferredColorScheme(.dark)
    }

    private var selectableRange: ClosedRange<Double> {
        let fallback = 0 ... max(duration, 0.1)
        guard let selectedRange else { return fallback }
        let lower = min(max(0, selectedRange.lowerBound), fallback.upperBound)
        let upper = max(lower, min(selectedRange.upperBound, fallback.upperBound))
        return lower ... max(lower + 0.01, upper)
    }
}

private struct LegendHacCoverTimeline: View {
    let sourceURL: URL
    let range: ClosedRange<Double>
    @Binding var selectedSeconds: Double
    @State private var frames: [Data] = []

    var body: some View {
        GeometryReader { geometry in
            let width = max(1, geometry.size.width)
            let span = max(0.01, range.upperBound - range.lowerBound)
            let selectedX = CGFloat((selectedSeconds - range.lowerBound) / span) * width

            ZStack(alignment: .leading) {
                Color.black

                HStack(spacing: 1) {
                    ForEach(Array(frames.enumerated()), id: \.offset) { _, data in
                        Group {
                            if let image = UIImage(data: data) {
                                Image(uiImage: image).resizable().scaledToFill()
                            } else {
                                Color.black
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .clipped()
                    }
                }

                Rectangle()
                    .fill(LegendNextColor.goldBright)
                    .frame(width: 3, height: geometry.size.height + 8)
                    .offset(x: min(max(0, selectedX), width - 3))
                    .shadow(color: .black.opacity(0.34), radius: 2)
                    .allowsHitTesting(false)
            }
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.20), lineWidth: 1)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let progress = min(max(0, Double(value.location.x / width)), 1)
                        selectedSeconds = range.lowerBound + span * progress
                    })
        }
        .task(id: range) {
            frames = await LegendHacPreviewFrame.timelineFrames(
                from: sourceURL,
                range: range,
                count: 10)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(LegendLocalized("Cover frame at {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (LegendHacPreviewFrame.timeLabel(selectedSeconds)))]))
    }
}

/// One JPEG-generation authority for the Hac publishing flow. A 720px cap
/// and bounded progressive compression keep poster transfers lightweight while
/// preserving the existing preview-storage contract and source video unchanged.
private enum LegendHacPreviewFrame {
    private static let maximumEdge: CGFloat = 720
    private static let maximumBytes = 384 * 1_024

    static func duration(of url: URL) async -> Double {
        let asset = AVURLAsset(url: url)
        let seconds = (try? await asset.load(.duration))?.seconds ?? 0
        return seconds.isFinite && seconds > 0 ? seconds : 1
    }

    static func jpegData(from url: URL, at seconds: Double) -> Data? {
        let asset = AVURLAsset(url: url)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero

        let time = CMTime(seconds: max(0, seconds), preferredTimescale: 600)
        guard let image = try? generator.copyCGImage(at: time, actualTime: nil) else {
            return nil
        }

        let source = UIImage(cgImage: image)
        let largestSide = max(source.size.width, source.size.height)
        let scale = largestSide > maximumEdge ? maximumEdge / largestSide : 1
        let targetSize = CGSize(
            width: max(1, (source.size.width * scale).rounded()),
            height: max(1, (source.size.height * scale).rounded()))
        let renderer = UIGraphicsImageRenderer(size: targetSize)
        let normalized = renderer.image { _ in
            source.draw(in: CGRect(origin: .zero, size: targetSize))
        }

        for quality in [CGFloat(0.72), 0.58, 0.46, 0.34] {
            if let data = normalized.jpegData(compressionQuality: quality),
               data.count <= maximumBytes {
                return data
            }
        }
        return nil
    }

    static func timelineFrames(
        from url: URL,
        range: ClosedRange<Double>,
        count: Int
    ) async -> [Data] {
        let safeCount = max(2, count)
        return await Task.detached(priority: .userInitiated) {
            let span = max(0.01, range.upperBound - range.lowerBound)
            return (0..<safeCount).compactMap { index in
                let progress = Double(index) / Double(max(1, safeCount - 1))
                return LegendHacPreviewFrame.jpegData(
                    from: url,
                    at: range.lowerBound + span * progress)
            }
        }.value
    }

    static func timeLabel(_ seconds: Double) -> String {
        let wholeSeconds = max(0, Int(seconds.rounded(.down)))
        return String(format: "%d:%02d", wholeSeconds / 60, wholeSeconds % 60)
    }
}

private enum LegendSocialMediaLoadingError: LocalizedError {
    case unavailable
    case unsupported

    var errorDescription: String? {
        switch self {
        case .unavailable:
            LegendLocalized("Legend could not prepare this media for publishing.")
        case .unsupported:
            LegendLocalized("This media format is not supported for this update.")
        }
    }
}

private struct LegendSocialCameraCapture: UIViewControllerRepresentable {
    let allowsPhotos: Bool
    let allowsVideos: Bool
    let maximumVideoDuration: Double?
    let captured: (Result<LegendSocialMediaDraft, Error>) -> Void
    let cancelled: () -> Void

    func makeUIViewController(context: Context) -> LegendSocialCameraViewController {
        LegendSocialCameraViewController(
            allowsPhotos: allowsPhotos,
            allowsVideos: allowsVideos,
            maximumVideoDuration: maximumVideoDuration,
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
            LegendLocalized("Camera capture is unavailable on this device.")
        case .permissionDenied:
            LegendLocalized("Camera access is required to capture a new update.")
        case .captureFailed:
            LegendLocalized("Legend could not complete that capture.")
        }
    }
}

private final class LegendSocialCameraViewController: UIViewController {
    private let allowsPhotos: Bool
    private let allowsVideos: Bool
    private let maximumVideoDuration: Double?
    private let captured: (Result<LegendSocialMediaDraft, Error>) -> Void
    private let cancelled: () -> Void

    private let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "com.mylegnd.legend.registered.social.camera")
    private let photoOutput = AVCapturePhotoOutput()
    private let movieOutput = AVCaptureMovieFileOutput()
    private let previewLayer = AVCaptureVideoPreviewLayer()
    private let captureButton = UIButton(type: .custom)
    private let cameraButton = UIButton(type: .system)
    private let flashButton = UIButton(type: .system)
    private let closeButton = UIButton(type: .system)
    private let libraryButton = UIButton(type: .system)
    private let captureModeButton = UIButton(type: .system)
    private let statusLabel = UILabel()

    private var videoInput: AVCaptureDeviceInput?
    private var configured = false
    private var completed = false
    private var isCancellingRecording = false
    private var flashEnabled = false
    private var recordingURL: URL?
    private var capturesVideo: Bool

    init(
        allowsPhotos: Bool,
        allowsVideos: Bool,
        maximumVideoDuration: Double?,
        captured: @escaping (Result<LegendSocialMediaDraft, Error>) -> Void,
        cancelled: @escaping () -> Void
    ) {
        self.allowsPhotos = allowsPhotos
        self.allowsVideos = allowsVideos
        self.maximumVideoDuration = maximumVideoDuration
        self.captured = captured
        self.cancelled = cancelled
        capturesVideo = allowsVideos && !allowsPhotos
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor(LegendNextColor.midnight)
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
        let chrome = UIColor(LegendNextColor.midnight).withAlphaComponent(0.42)

        configureIconButton(closeButton, symbol: "xmark", tint: controlTint, background: chrome)
        configureIconButton(flashButton, symbol: "bolt.slash.fill", tint: controlTint, background: chrome)
        configureIconButton(cameraButton, symbol: "camera.rotate.fill", tint: controlTint, background: chrome)
        configureIconButton(libraryButton, symbol: "photo.on.rectangle", tint: controlTint, background: chrome)
        configureIconButton(captureModeButton, symbol: "video.fill", tint: controlTint, background: chrome)

        closeButton.addTarget(self, action: #selector(close), for: .touchUpInside)
        flashButton.addTarget(self, action: #selector(toggleFlash), for: .touchUpInside)
        cameraButton.addTarget(self, action: #selector(switchCamera), for: .touchUpInside)
        libraryButton.addTarget(self, action: #selector(returnToLibrary), for: .touchUpInside)
        captureModeButton.addTarget(self, action: #selector(toggleCaptureMode), for: .touchUpInside)
        captureModeButton.isHidden = !(allowsPhotos && allowsVideos)

        captureButton.translatesAutoresizingMaskIntoConstraints = false
        captureButton.backgroundColor = .white
        captureButton.layer.cornerRadius = 36
        captureButton.layer.borderColor = UIColor.white.withAlphaComponent(0.55).cgColor
        captureButton.layer.borderWidth = 6
        updateCaptureModeAppearance()
        captureButton.addTarget(self, action: #selector(capture), for: .touchUpInside)

        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        statusLabel.font = .preferredFont(forTextStyle: .caption1)
        statusLabel.textColor = .white
        statusLabel.textAlignment = .center
        statusLabel.adjustsFontForContentSizeCategory = true
        statusLabel.numberOfLines = 2

        [closeButton, flashButton, cameraButton, libraryButton, captureModeButton, captureButton, statusLabel]
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
            captureModeButton.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -20),
            captureModeButton.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -24),
            captureButton.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            captureButton.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -20),
            captureButton.widthAnchor.constraint(equalToConstant: 72),
            captureButton.heightAnchor.constraint(equalTo: captureButton.widthAnchor),
            statusLabel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 32),
            statusLabel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -32),
            statusLabel.bottomAnchor.constraint(equalTo: captureButton.topAnchor, constant: -18)
        ])
        setStatus(captureStatus)
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

            if self.allowsVideos {
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

            if self.allowsVideos {
                if AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
                   let microphone = AVCaptureDevice.default(for: .audio),
                   let audioInput = try? AVCaptureDeviceInput(device: microphone),
                   self.session.canAddInput(audioInput) {
                    self.session.addInput(audioInput)
                }
                if self.session.canAddOutput(self.movieOutput) {
                    self.session.addOutput(self.movieOutput)
                    if let maximumVideoDuration = self.maximumVideoDuration {
                        self.movieOutput.maxRecordedDuration = CMTime(
                            seconds: maximumVideoDuration,
                            preferredTimescale: 600)
                    }
                }
            }

            if self.allowsPhotos, self.session.canAddOutput(self.photoOutput) {
                self.session.addOutput(self.photoOutput)
            }

            self.session.commitConfiguration()
            self.configured = true
            DispatchQueue.main.async {
                self.previewLayer.session = self.session
                self.setStatus(self.captureStatus)
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

    @objc private func toggleCaptureMode() {
        guard allowsPhotos,
              allowsVideos,
              !movieOutput.isRecording else { return }
        capturesVideo.toggle()
        updateCaptureModeAppearance()
        setStatus(captureStatus)
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
        captureButton.accessibilityLabel = recording
            ? LegendLocalized("Stop video recording", context: "accessibility copy")
            : LegendLocalized("Start video recording", context: "accessibility copy")
        setStatus(recording ? LegendLocalized("Recording · tap to finish") : captureStatus)
    }

    private var captureStatus: String {
        guard capturesVideo else { return LegendLocalized("Photo capture") }
        let audioIsAvailable = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        let mode = audioIsAvailable
            ? LegendLocalized("Video capture")
            : LegendLocalized("Video capture without audio")
        guard let maximumVideoDuration else { return mode }
        return LegendLocalized(
            "{mode} · up to {seconds} seconds",
            arguments: ["mode": mode, "seconds": Int(maximumVideoDuration)])
    }

    private func updateCaptureModeAppearance() {
        let nextModeSymbol = capturesVideo ? "camera.fill" : "video.fill"
        captureModeButton.setImage(UIImage(systemName: nextModeSymbol), for: .normal)
        captureModeButton.accessibilityLabel = capturesVideo
            ? LegendLocalized("Switch to photo capture", context: "accessibility copy")
            : LegendLocalized("Switch to video capture", context: "accessibility copy")
        captureButton.accessibilityLabel = capturesVideo
            ? LegendLocalized("Start video recording", context: "accessibility copy")
            : LegendLocalized("Capture photo", context: "accessibility copy")
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

        Task { [weak self] in
            defer { try? FileManager.default.removeItem(at: outputFileURL) }
            do {
                let draft = try await LegendSocialMediaDraft.video(from: outputFileURL)
                self?.finish(with: .success(draft))
            } catch {
                self?.finish(with: .failure(error))
            }
        }
    }
}

private extension MobileSocialContentType {
    func accepts(_ media: LegendSocialMediaDraft) -> Bool {
        media.isVideo ? acceptsVideos : acceptsImages
    }

    func accepts(_ asset: LegendPhotoLibraryAsset) -> Bool {
        selectionRejection(for: asset) == nil
    }

    func selectionRejection(for asset: LegendPhotoLibraryAsset) -> String? {
        guard asset.isVideo else {
            return acceptsImages
                ? nil
                : LegendLocalized("Hacs use one video. Choose a video to continue.")
        }

        guard acceptsVideos else {
            return LegendLocalized("This media is not available for the selected format.")
        }

        guard format.acceptsVideo(duration: asset.duration) else {
            return LegendLocalized("Videos must be 10 minutes or less.")
        }

        return nil
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
    let selectionIndex: Int?
    let isEligible: Bool
    let select: () -> Void

    @State private var image: UIImage?
    @State private var requestID: PHImageRequestID = PHInvalidImageRequestID
    @State private var isPreviewing = false

    var body: some View {
        Button(action: select) {
            ZStack(alignment: .topTrailing) {
                thumbnail
                    .overlay {
                        if !isEligible {
                            LegendNextColor.midnight.opacity(0.45)
                        }
                    }

                if asset.isVideo {
                    VStack {
                        Spacer()
                        HStack {
                            Spacer()
                            Text(durationLabel)
                                .font(.caption2.monospacedDigit().weight(.bold))
                                .padding(.horizontal, 5)
                                .padding(.vertical, 3)
                                .foregroundStyle(.white)
                                .background(LegendNextColor.midnight.opacity(0.7), in: Capsule())
                        }
                    }
                    .padding(6)
                }

                if isSelected {
                    selectionBadge
                        .padding(6)
                }
            }
        }
        .buttonStyle(.plain)
        .disabled(!isEligible)
        .onLongPressGesture(minimumDuration: 0.35) {
            guard isEligible else { return }
            isPreviewing = true
        }
        .fullScreenCover(isPresented: $isPreviewing) {
            LegendPhotoLibraryAssetPreview(
                asset: asset,
                photoLibrary: photoLibrary,
                isSelected: isSelected,
                toggleSelection: {
                    isPreviewing = false
                    select()
                },
                dismiss: { isPreviewing = false })
        }
        .accessibilityLabel(accessibilityDescription)
        .accessibilityHint(
            isEligible
                ? "Double tap to select. Touch and hold to preview."
                : "Not available for this format.")
        .task(id: "\(asset.id)-\(typeThumbnailKey)") {
            requestID = photoLibrary.thumbnailRequest(
                for: asset.id,
                targetSize: thumbnailRequestSize
            ) { image in
                self.image = image
            }
        }
        .onDisappear {
            photoLibrary.cancelThumbnailRequest(requestID)
        }
    }

    /// A numbered badge when the selection is ordered, a check when it is single-pick.
    @ViewBuilder
    private var selectionBadge: some View {
        if let selectionIndex {
            Text("\(selectionIndex)")
                .font(.caption.weight(.black))
                .foregroundStyle(LegendNextColor.midnight)
                .frame(width: 24, height: 24)
                .background(LegendNextColor.goldBright, in: Circle())
                .overlay { Circle().strokeBorder(.white, lineWidth: 1.5) }
        } else {
            Image(systemName: "checkmark.circle.fill")
                .font(.title3)
                .foregroundStyle(LegendNextColor.gold)
                .background(LegendNextColor.midnight.opacity(0.62), in: Circle())
        }
    }

    private var accessibilityDescription: String {
        var description = asset.isVideo
            ? LegendLocalized("Video", context: "accessibility copy")
            : LegendLocalized("Photo", context: "accessibility copy")
        if asset.isVideo {
            description = LegendLocalized(
                "{description}, {duration}",
                context: "accessibility copy",
                arguments: ["description": description, "duration": durationLabel])
        }
        if let selectionIndex {
            description = LegendLocalized(
                "{description}, selected, position {position}",
                context: "accessibility copy",
                arguments: ["description": description, "position": selectionIndex])
        } else if isSelected {
            description = LegendLocalized(
                "{description}, selected",
                context: "accessibility copy",
                arguments: ["description": description])
        }
        return description
    }

    private var typeThumbnailKey: String {
        "\(Int(thumbnailRequestSize.width))x\(Int(thumbnailRequestSize.height))"
    }

    private var thumbnailRequestSize: CGSize {
        CGSize(width: 360, height: 640)
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
            LegendSkeletonShape(cornerRadius: 0)
        }
    }

    private var durationLabel: String {
        let total = max(0, Int(asset.duration.rounded()))
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

/// Full-screen preview shown on a long press in the media grid, matching the
/// peek behaviour people expect from a native picker.
private struct LegendPhotoLibraryAssetPreview: View {
    let asset: LegendPhotoLibraryAsset
    @ObservedObject var photoLibrary: LegendPhotoLibraryAccess
    let isSelected: Bool
    let toggleSelection: () -> Void
    let dismiss: () -> Void

    @State private var image: UIImage?
    @State private var requestID: PHImageRequestID = PHInvalidImageRequestID

    var body: some View {
        ZStack {
            LegendNextColor.midnight.ignoresSafeArea()

            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFit()
                    .ignoresSafeArea()
            } else {
                LegendSkeletonShape(cornerRadius: 0)
                    .ignoresSafeArea()
            }

            VStack {
                HStack {
                    Button(action: dismiss) {
                        Image(systemName: "xmark")
                            .font(.title3.weight(.semibold))
                            .frame(width: 44, height: 44)
                            .background(LegendNextColor.midnight.opacity(0.55), in: Circle())
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.white)
                    .accessibilityLabel(LegendLocalized("Close preview", context: "accessibility copy"))

                    Spacer()

                    if asset.isVideo {
                        Label(durationLabel, systemImage: "video.fill")
                            .font(.caption.weight(.bold))
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .foregroundStyle(.white)
                            .background(LegendNextColor.midnight.opacity(0.55), in: Capsule())
                    }
                }
                .padding(LegendNextSpacing.md)

                Spacer()

                Button(action: toggleSelection) {
                    Label(
                        isSelected
                            ? LegendLocalized("Remove from selection")
                            : LegendLocalized("Add to selection"),
                        systemImage: isSelected ? "minus.circle" : "checkmark.circle")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendNextButtonStyle(kind: .primary))
                .padding(LegendNextSpacing.md)
            }
        }
        .task {
            requestID = photoLibrary.thumbnailRequest(
                for: asset.id,
                targetSize: CGSize(width: 1_440, height: 1_440)
            ) { image in
                self.image = image
            }
        }
        .onDisappear { photoLibrary.cancelThumbnailRequest(requestID) }
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
final class LegendPhotoLibraryAccess: NSObject, ObservableObject {
    @Published private(set) var status: LegendPhotoLibraryAuthorization
    @Published private(set) var visibleAssets: [LegendPhotoLibraryAsset] = []

    private let imageManager = PHCachingImageManager()
    private var allAssets: [LegendPhotoLibraryAsset] = []
    /// Retained so a thumbnail request resolves its PHAsset by index instead of
    /// re-running a fetch for every visible cell.
    private var fetchResult: PHFetchResult<PHAsset>?
    private var assetIndexByIdentifier: [String: Int] = [:]
    private var loadedAssetCount = 0
    private var isObservingLibrary = false
    private let pageSize = 60

    override init() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))
        super.init()
    }

    deinit {
        if isObservingLibrary {
            PHPhotoLibrary.shared().unregisterChangeObserver(self)
        }
    }

    func refresh() {
        status = LegendPhotoLibraryAuthorization(
            PHPhotoLibrary.authorizationStatus(for: .readWrite))

        guard status == .authorized || status == .limited else {
            allAssets = []
            visibleAssets = []
            fetchResult = nil
            assetIndexByIdentifier = [:]
            loadedAssetCount = 0
            return
        }

        startObservingLibraryIfNeeded()

        Task { [weak self] in
            let loaded = await Task.detached(priority: .userInitiated) {
                Self.fetchAssets()
            }.value
            guard let self else { return }
            apply(loaded, preservingLoadedCount: true)
        }
    }

    private func apply(
        _ loaded: PHFetchResult<PHAsset>,
        preservingLoadedCount: Bool
    ) {
        fetchResult = loaded

        var descriptors: [LegendPhotoLibraryAsset] = []
        var indexByIdentifier: [String: Int] = [:]
        descriptors.reserveCapacity(loaded.count)
        indexByIdentifier.reserveCapacity(loaded.count)
        loaded.enumerateObjects { asset, index, _ in
            descriptors.append(LegendPhotoLibraryAsset(asset))
            indexByIdentifier[asset.localIdentifier] = index
        }

        allAssets = descriptors
        assetIndexByIdentifier = indexByIdentifier

        // Keep the user's scroll depth across a library change or a re-entry.
        let desired = preservingLoadedCount
            ? max(loadedAssetCount, min(pageSize, descriptors.count))
            : min(pageSize, descriptors.count)
        loadedAssetCount = min(desired, descriptors.count)
        visibleAssets = Array(descriptors.prefix(loadedAssetCount))
    }

    private func startObservingLibraryIfNeeded() {
        guard !isObservingLibrary else { return }
        PHPhotoLibrary.shared().register(self)
        isObservingLibrary = true
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

    /// Paginates after filtering by the selected creation format. Previously the
    /// composer took the first mixed photo/video page and filtered it in the view,
    /// leaving a Hac picker empty whenever the first 60 assets were photos.
    func assets(for contentType: MobileSocialContentType) -> [LegendPhotoLibraryAsset] {
        Array(filteredAssets(for: contentType).prefix(loadedAssetCount))
    }

    func canLoadMore(for contentType: MobileSocialContentType) -> Bool {
        loadedAssetCount < filteredAssets(for: contentType).count
    }

    func loadNextPage(for contentType: MobileSocialContentType) {
        let availableAssets = filteredAssets(for: contentType)
        guard loadedAssetCount < availableAssets.count else { return }
        loadedAssetCount = min(loadedAssetCount + pageSize, availableAssets.count)
        visibleAssets = Array(allAssets.prefix(loadedAssetCount))
    }

    private func filteredAssets(
        for contentType: MobileSocialContentType
    ) -> [LegendPhotoLibraryAsset] {
        allAssets.filter(contentType.accepts)
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
        return try await LegendSocialMediaDraft.video(
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

    /// Resolves through the retained fetch result. The previous implementation ran a
    /// fresh `fetchAssets(withLocalIdentifiers:)` for every thumbnail request, which
    /// made scrolling a large library progressively more expensive.
    private func asset(for identifier: String) -> PHAsset? {
        if let fetchResult,
           let index = assetIndexByIdentifier[identifier],
           index < fetchResult.count {
            return fetchResult.object(at: index)
        }

        return PHAsset.fetchAssets(withLocalIdentifiers: [identifier], options: nil)
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

    nonisolated private static func fetchAssets() -> PHFetchResult<PHAsset> {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = NSPredicate(
            format: "mediaType == %d OR mediaType == %d",
            PHAssetMediaType.image.rawValue,
            PHAssetMediaType.video.rawValue)

        return PHAsset.fetchAssets(with: options)
    }
}

extension LegendPhotoLibraryAccess: PHPhotoLibraryChangeObserver {
    /// Keeps the grid honest when the user captures, deletes, or changes their
    /// Selected Photos set while the composer is open.
    nonisolated func photoLibraryDidChange(_ changeInstance: PHChange) {
        Task { @MainActor [weak self] in
            guard let self, let current = self.fetchResult else { return }
            guard let details = changeInstance.changeDetails(for: current) else { return }
            self.apply(details.fetchResultAfterChanges, preservingLoadedCount: true)
        }
    }
}
