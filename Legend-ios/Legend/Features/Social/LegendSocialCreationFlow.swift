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

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.hero
                    .ignoresSafeArea()

                ScrollView {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.xl) {
                        HStack {
                            Button(action: dismiss) {
                                Image(systemName: "xmark")
                                    .font(.title3.weight(.semibold))
                                    .frame(
                                        width: LegendNextSize.prominentControlHeight,
                                        height: LegendNextSize.prominentControlHeight)
                                    .background(
                                        Color.white.opacity(0.12),
                                        in: Circle())
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(.white)
                            .accessibilityLabel("Close creator")

                            Spacer()

                            Text("Create")
                                .font(LegendNextTypography.hero)
                                .foregroundStyle(.white)

                            Spacer()

                            Color.clear
                                .frame(
                                    width: LegendNextSize.prominentControlHeight,
                                    height: LegendNextSize.prominentControlHeight)
                        }

                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text("Share what matters")
                                .font(LegendNextTypography.display)
                                .foregroundStyle(.white)
                            Text("Choose how you want to share with your Legend network.")
                                .font(LegendNextTypography.body)
                                .foregroundStyle(Color.white.opacity(0.72))
                        }

                        VStack(spacing: LegendNextSpacing.sm) {
                            ForEach(MobileSocialContentType.allCases) { candidate in
                                Button {
                                    select(candidate)
                                } label: {
                                    creationOption(candidate)
                                }
                                .buttonStyle(.plain)
                                .accessibilityLabel("Create \(candidate.displayName)")
                                .accessibilityHint(candidate.creationPrompt)
                            }
                        }

                        Spacer(minLength: LegendNextSpacing.scene)
                    }
                    .padding(.horizontal, LegendNextSpacing.lg)
                    .padding(.vertical, LegendNextSpacing.md)
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome(detents: [.large], showsDragIndicator: false)
    }

    private func creationOption(_ type: MobileSocialContentType) -> some View {
        HStack(spacing: LegendNextSpacing.md) {
            Image(systemName: type.systemImage)
                .font(.title2.weight(.semibold))
                .frame(width: 52, height: 52)
                .foregroundStyle(LegendNextColor.goldBright)
                .background(LegendNextColor.midnight, in: Circle())

            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                Text(type.displayName)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(.white)
                Text(type.creationPrompt)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(Color.white.opacity(0.68))
                    .lineLimit(2)
            }

            Spacer(minLength: LegendNextSpacing.xs)

            Image(systemName: "arrow.right")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(LegendNextColor.goldBright)
                .accessibilityHidden(true)
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.white.opacity(0.08), in: RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous)
                .strokeBorder(Color.white.opacity(0.13), lineWidth: 1)
        }
    }
}

private struct LegendSocialCreationModeRail: View {
    @Binding var selection: MobileSocialContentType

    var body: some View {
        HStack(spacing: LegendNextSpacing.tiny) {
            ForEach(MobileSocialContentType.allCases) { candidate in
                Button {
                    withAnimation(LegendNextMotion.tab) {
                        selection = candidate
                    }
                } label: {
                    Label(candidate.displayName, systemImage: candidate.systemImage)
                        .font(LegendNextTypography.label)
                        .frame(maxWidth: .infinity)
                        .frame(height: LegendNextSize.compactControlHeight)
                        .foregroundStyle(candidate == selection ? LegendNextColor.midnight : Color.white.opacity(0.72))
                        .background(
                            candidate == selection ? LegendNextColor.goldBright : Color.clear,
                            in: Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Create \(candidate.displayName)")
                .accessibilityAddTraits(candidate == selection ? .isSelected : [])
            }
        }
        .padding(LegendNextSpacing.tiny)
        .background(Color.white.opacity(0.10), in: Capsule())
        .overlay {
            Capsule()
                .strokeBorder(Color.white.opacity(0.14), lineWidth: 1)
        }
    }
}

/// Shared progression chrome for every creation surface. The user is always
/// editing one draft, moving through Media → Edit → Share; the view never
/// manufactures a second route or a parallel draft state.
private struct LegendSocialCreationProgress: View {
    private enum Step: Int, CaseIterable {
        case media
        case edit
        case share

        var title: String {
            switch self {
            case .media: "Media"
            case .edit: "Edit"
            case .share: "Share"
            }
        }
    }

    let stage: LegendSocialCreationStage
    let isDark: Bool

    private var currentStep: Step {
        switch stage {
        case .library, .preparingMedia, .camera, .failed:
            .media
        case .metadata, .music:
            .edit
        case .share, .handedOff:
            .share
        }
    }

    var body: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            ForEach(Step.allCases, id: \.rawValue) { step in
                HStack(spacing: LegendNextSpacing.micro) {
                    Image(systemName: step.rawValue < currentStep.rawValue
                          ? "checkmark"
                          : "circle.fill")
                        .font(.system(size: step.rawValue < currentStep.rawValue ? 9 : 6, weight: .bold))
                    Text(step.title)
                        .font(.caption2.weight(.bold))
                }
                .foregroundStyle(foreground(for: step))

                if step != .share {
                    Capsule()
                        .fill(connectorColor(after: step))
                        .frame(width: 24, height: 2)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Creation step \(currentStep.rawValue + 1) of 3: \(currentStep.title)")
    }

    private func foreground(for step: Step) -> Color {
        guard step.rawValue <= currentStep.rawValue else {
            return isDark ? Color.white.opacity(0.38) : LegendNextColor.textSecondary.opacity(0.58)
        }
        return isDark ? LegendNextColor.goldBright : LegendNextColor.royal
    }

    private func connectorColor(after step: Step) -> Color {
        step.rawValue < currentStep.rawValue
            ? foreground(for: step)
            : (isDark ? Color.white.opacity(0.22) : LegendNextColor.separator)
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
    @State private var tagsAndMentions = ""
    @State private var shareLocation = ""
    @State private var commentsEnabled = true
    @State private var mediaSelectionError: String?
    @State private var stage: LegendSocialCreationStage = .library
    @State private var musicReturnStage: LegendSocialCreationStage = .metadata
    @State private var selectedMusic: LegendSocialMusicDraft?
    @State private var activeEditorTool: LegendSocialEditingTool = .text
    @State private var ownsMediaAfterDismissal = false
    @State private var selectedHacPreviewData: Data?
    @State private var isSelectingHacPreview = false
    @State private var postCanvas = LegendSocialPostCanvas.portrait

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

                case .music:
                    if musicReturnStage == .share {
                        shareDetailsContent
                    } else {
                        metadataContent
                    }

                case .handedOff:
                    shareDetailsContent
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome(detents: [.large], showsDragIndicator: false)
        .sheet(isPresented: musicPickerPresented) {
            LegendSocialMusicSelectionSheet(
                social: social,
                selection: selectedMusic,
                save: {
                    selectedMusic = $0
                    stage = musicReturnStage
                },
                cancel: {
                    stage = musicReturnStage
                })
        }
        .sheet(isPresented: $isSelectingHacPreview) {
            if let sourceURL = selectedMedia.first?.videoFileURL {
                LegendHacPreviewSelector(
                    sourceURL: sourceURL,
                    save: { previewData in
                        selectedHacPreviewData = previewData
                        isSelectingHacPreview = false
                    },
                    cancel: {
                        isSelectingHacPreview = false
                    })
            }
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

    private var musicPickerPresented: Binding<Bool> {
        Binding(
            get: {
                stage == .music
            },
            set: {
                if !$0 && stage == .music {
                    stage = musicReturnStage
                }
            }
        )
    }

    private var cameraPresented: Binding<Bool> {
        Binding(
            get: { stage == .camera },
            set: { if !$0 && stage == .camera { stage = .library } })
    }

    private var libraryContent: some View {
        ZStack {
            LegendNextGradient.hero
                .ignoresSafeArea()

            VStack(spacing: 0) {
                libraryHeader

                ScrollView {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                        selectionPreview(isDark: true)
                        legendModeRail
                        photoLibraryStatus

                        HStack(alignment: .firstTextBaseline) {
                            Text("Recents")
                                .font(LegendNextTypography.title)
                                .foregroundStyle(.white)
                            Spacer()
                            Text("Select media")
                                .font(LegendNextTypography.label)
                                .foregroundStyle(LegendNextColor.goldBright)
                        }

                        mediaGrid
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.bottom, LegendNextSpacing.xl)
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    private var libraryHeader: some View {
        VStack(spacing: LegendNextSpacing.micro) {
            HStack {
                Button(action: cancel) {
                    Image(systemName: "xmark")
                        .font(.title3.weight(.semibold))
                        .frame(
                            width: LegendNextSize.prominentControlHeight,
                            height: LegendNextSize.prominentControlHeight)
                        .background(Color.white.opacity(0.12), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .accessibilityLabel("Close creator")

                Spacer()

                Text(type.newContentTitle)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(.white)

                Spacer()

                Button(action: primaryAction) {
                    Text(primaryActionTitle)
                        .font(LegendNextTypography.label)
                        .foregroundStyle(canContinue ? LegendNextColor.goldBright : Color.white.opacity(0.38))
                        .frame(
                            width: LegendNextSize.prominentControlHeight,
                            height: LegendNextSize.prominentControlHeight)
                }
                .buttonStyle(.plain)
                .disabled(!canContinue)
                .accessibilityLabel("Continue to \(type.displayName) details")
            }

            LegendSocialCreationProgress(stage: stage, isDark: true)
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var legendModeRail: some View {
        LegendSocialCreationModeRail(selection: $type)
    }

    private var eligibleLibraryAssets: [LegendPhotoLibraryAsset] {
        photoLibrary.assets(for: type)
    }

    private var selectionAspectRatio: CGFloat {
        1
    }

    private var emptyPreviewHeight: CGFloat {
        CGFloat(type.format.emptyPreviewHeight)
    }

    private var selectionPreviewSide: CGFloat {
        CGFloat(type.format.selectionThumbnailSide)
    }

    @ViewBuilder
    private func selectionPreview(isDark: Bool) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(type.mediaSelectionTitle)
                        .font(.headline.weight(.bold))
                        .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
                    Text(type.mediaSelectionHint)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(isDark ? Color.white.opacity(0.68) : LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: LegendNextSpacing.sm)
                Button {
                    stage = .camera
                } label: {
                    Image(systemName: "camera.fill")
                        .font(.title3.weight(.bold))
                        .frame(width: LegendNextSize.controlHeight, height: LegendNextSize.controlHeight)
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.midnight)
                .background(LegendNextColor.goldBright, in: Circle())
                .accessibilityLabel("Open camera for \(type.displayName)")
            }

            if selectedMedia.isEmpty {
                VStack(spacing: LegendNextSpacing.sm) {
                    Image(systemName: "photo.on.rectangle.angled")
                        .font(.system(size: 34, weight: .semibold))
                    Text("Choose from your library below")
                        .font(LegendNextTypography.cardTitle)
                    Text("or use the camera above")
                        .font(LegendNextTypography.supporting)
                }
                .frame(maxWidth: .infinity)
                .frame(height: emptyPreviewHeight)
                .foregroundStyle(
                    isDark
                        ? Color.white.opacity(0.80)
                        : LegendNextColor.textSecondary
                )
                .background(
                    isDark ? Color.white.opacity(0.08) : LegendNextColor.surfaceInset,
                    in: RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous)
                        .strokeBorder(
                            isDark ? Color.white.opacity(0.13) : LegendNextColor.separator,
                            style: StrokeStyle(lineWidth: 1, dash: [5, 4]))
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Select media from your library below, or use the camera above")
            } else {
                mediaPreviewStrip(isDark: isDark)
            }

            if let mediaSelectionError {
                Label(mediaSelectionError, systemImage: "exclamationmark.triangle.fill")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.warning)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    @ViewBuilder
    private func mediaPreviewStrip(
        isDark: Bool,
        overlayText: String? = nil
    ) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                ForEach(selectedMedia) { media in
                    LegendSocialMediaPreview(
                        media: media,
                        presentation: .selection,
                        overlayText: overlayText,
                        remove: { remove(media) })
                    .frame(width: selectionPreviewSide, height: selectionPreviewSide)
                }
            }
            .padding(.vertical, 2)
        }
        .scrollIndicators(.hidden)
        .background(
            isDark ? Color.clear : LegendNextColor.surfaceInset,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous
            )
        )
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
                    .accessibilityLabel("Loading more media")
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
        ZStack {
            LegendNextColor.midnight
                .ignoresSafeArea()

            VStack(spacing: 0) {
                immersiveEditorHeader

                GeometryReader { geometry in
                    let availableWidth = max(
                        0,
                        geometry.size.width -
                            LegendNextSpacing.md * 2
                    )
                    let availableHeight = max(
                        0,
                        geometry.size.height -
                            LegendNextSpacing.sm * 2
                    )
                    let widthFromHeight =
                        availableHeight *
                        editorCanvasAspectRatio
                    let canvasWidth = min(
                        editorCanvasMaximumWidth,
                        availableWidth,
                        widthFromHeight
                    )
                    let canvasHeight =
                        canvasWidth /
                        editorCanvasAspectRatio

                    ZStack(alignment: .trailing) {
                        editorMediaCanvas
                            .frame(
                                width: canvasWidth,
                                height: canvasHeight
                            )
                            .clipShape(
                                RoundedRectangle(
                                    cornerRadius: 22,
                                    style: .continuous
                                )
                            )
                            .overlay {
                                RoundedRectangle(
                                    cornerRadius: 22,
                                    style: .continuous
                                )
                                .strokeBorder(
                                    Color.white.opacity(0.18),
                                    lineWidth: 1
                                )
                            }
                            .frame(
                                maxWidth: .infinity,
                                maxHeight: .infinity,
                                alignment: .center
                            )

                        immersiveToolRail
                            .padding(.trailing, LegendNextSpacing.sm)
                    }
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity
                    )
                }

                immersiveEditorFooter
            }
        }
    }

    private var immersiveEditorHeader: some View {
        VStack(spacing: LegendNextSpacing.micro) {
            HStack {
                Button {
                    stage = .library
                } label: {
                    Image(systemName: "xmark")
                        .font(.title2.weight(.medium))
                        .frame(
                            width:
                                LegendNextSize
                                    .prominentControlHeight,
                            height:
                                LegendNextSize
                                    .prominentControlHeight
                        )
                        .background(
                            Color.white.opacity(0.20),
                            in: Circle()
                        )
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .accessibilityLabel("Back to media selection")

                Spacer(minLength: 0)

                Text(type.editingTitle)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(.white)

                Spacer(minLength: 0)

                Color.clear
                    .frame(
                        width:
                            LegendNextSize
                                .prominentControlHeight,
                        height:
                            LegendNextSize
                                .prominentControlHeight
                    )
            }

            LegendSocialCreationProgress(stage: stage, isDark: true)
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
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

                    Text("Add content")
                        .font(LegendNextTypography.cardTitle)
                }
                .foregroundStyle(.white)
            }
        } else {
            ZStack {
                LegendNextColor.midnight

                if let primaryMedia = selectedMedia.first {
                    LegendSocialMediaPreview(
                        media: primaryMedia,
                        presentation: .canvas,
                        overlayText:
                            editorOverlayText,
                        dimmingEdges: type.format.usesFixedCanvasAspectRatio,
                        remove: {
                            remove(primaryMedia)
                        }
                    )
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity
                    )
                    .clipped()
                }

                if type.format.maximumMediaItems > 1 &&
                    selectedMedia.count > 1 {
                    VStack {
                        Spacer()

                        Text(
                            "1 of \(selectedMedia.count)"
                        )
                        .font(.caption.weight(.bold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 7)
                        .background(
                            LegendNextColor.midnight.opacity(0.55),
                            in: Capsule()
                        )
                        .padding(.bottom, 12)
                    }
                }
            }
        }
    }

    private var editorOverlayText: String? {
        guard type == .story else {
            return nil
        }

        let value = caption.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        return value.isEmpty
            ? nil
            : value
    }

    private var immersiveToolRail: some View {
        VStack(spacing: LegendNextSpacing.xs) {
            ForEach(LegendSocialEditingTool.allCases) { tool in
                editorToolButton(tool: tool)
            }
        }
    }

    private func editorToolButton(
        tool: LegendSocialEditingTool
    ) -> some View {
        Button {
            activeEditorTool = tool

            if tool == .audio {
                musicReturnStage = .metadata
                stage = .music
            }
        } label: {
            Image(systemName: tool.systemImage)
                .font(.title3.weight(.semibold))
                .frame(width: 50, height: 50)
                .foregroundStyle(
                    activeEditorTool == tool
                        ? LegendNextColor.midnight
                        : .white
                )
                .background(
                    activeEditorTool == tool
                        ? LegendNextColor.goldBright
                        : LegendNextColor.midnight.opacity(0.56),
                    in: Circle()
                )
                .overlay {
                    Circle()
                        .strokeBorder(
                            Color.white.opacity(0.22),
                            lineWidth: 1
                        )
                }
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
            EmptyView()

        case .text:
            TextField(
                type == .story
                    ? "Add text..."
                    : "Add a caption...",
                text: $caption,
                axis: .vertical
            )
            .lineLimit(1...3)
            .font(LegendNextTypography.body)
            .foregroundStyle(.white)
            .tint(LegendNextColor.goldBright)
            .padding(.horizontal, LegendNextSpacing.md)
            .frame(
                minHeight:
                    LegendNextSize.controlHeight
            )
            .background(
                Color.white.opacity(0.14),
                in: RoundedRectangle(
                    cornerRadius:
                        LegendNextRadius.control,
                    style: .continuous
                )
            )
            .accessibilityLabel(
                type == .story
                    ? "Story text"
                    : "\(type.displayName) caption"
            )

        case .describe:
            accessibilityEditor(isDark: true)
        }
    }

    private var immersiveEditorFooter: some View {
        VStack(spacing: LegendNextSpacing.sm) {
            if type == .post {
                HStack(spacing: LegendNextSpacing.xs) {
                    Text("Post format")
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
            immersiveToolDetail
            publicationFailure

            Button {
                stage = .share
            } label: {
                Label(
                    "Next",
                    systemImage: "arrow.right"
                )
                .font(LegendNextTypography.section)
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(
                LegendNextButtonStyle(kind: .primary)
            )
            .disabled(!canContinue)
            .accessibilityLabel(
                "Continue to share \(type.displayName)"
            )
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.top, LegendNextSpacing.sm)
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

            Divider()
                .overlay(LegendNextColor.separator)

            ScrollView {
                VStack(spacing: 0) {
                    captionComposer

                    Divider()
                        .padding(.leading, 96)

                    tagsRow

                    Divider()
                        .padding(.leading, 56)

                    locationRow

                    Divider()
                        .padding(.leading, 56)

                    musicRow

                    if type.requiresVideo {
                        Divider()
                            .padding(.leading, 56)

                        hacPreviewRow
                    }

                    Divider()
                        .padding(.leading, 56)

                    accessibilityRow

                    Divider()
                        .padding(.leading, 56)

                    commentsRow

                    publicationFailure
                        .padding(.horizontal, 16)
                        .padding(.vertical, 12)
                }
            }
            .scrollIndicators(.hidden)
        }
        .background(LegendNextColor.canvas)
    }

    private var shareHeader: some View {
        VStack(spacing: LegendNextSpacing.micro) {
            HStack(spacing: 12) {
                Button {
                    stage = .metadata
                } label: {
                    Image(systemName: "chevron.left")
                        .font(.system(size: 20, weight: .semibold))
                        .frame(width: 32, height: 44)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.textPrimary)
                .accessibilityLabel("Back to media editing")

                Spacer(minLength: 0)

                Text(type.newContentTitle)
                    .font(.headline)
                    .foregroundStyle(LegendNextColor.textPrimary)

                Spacer(minLength: 0)

                Button(action: publish) {
                    Text("Share")
                        .font(.system(size: 16, weight: .semibold))
                        .frame(minWidth: 44, minHeight: 44)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(
                    canPublish && !social.isPublishing
                        ? LegendNextColor.goldBright
                        : LegendNextColor.textSecondary.opacity(0.45)
                )
                .disabled(!canPublish || social.isPublishing)
                .accessibilityLabel(
                    "Publish \(type.displayName)"
                )
            }

            LegendSocialCreationProgress(stage: stage, isDark: false)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, LegendNextSpacing.micro)
        .background(LegendNextColor.surface)
    }

    private var captionComposer: some View {
        HStack(alignment: .top, spacing: 12) {
            shareThumbnail

            ZStack(alignment: .topLeading) {
                if caption.isEmpty {
                    Text(
                        type == .story
                            ? "Add a story message..."
                            : "Write a caption..."
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
                    .frame(minHeight: 92)
                    .padding(.horizontal, 0)
                    .padding(.vertical, 0)
                    .background(Color.clear)
                    .accessibilityLabel(
                        "\(type.displayName) caption"
                    )
            }
            .frame(maxWidth: .infinity)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
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

    private var tagsRow: some View {
        HStack(spacing: 14) {
            Image(systemName: "person.crop.circle.badge.plus")
                .font(.system(size: 20))
                .frame(width: 26)
                .foregroundStyle(
                    LegendNextColor.textPrimary
                )

            TextField(
                "Tag people or add mentions",
                text: $tagsAndMentions,
                axis: .vertical
            )
            .lineLimit(1...2)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .font(.system(size: 15))
            .foregroundStyle(
                LegendNextColor.textPrimary
            )

            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(
                    LegendNextColor.textSecondary.opacity(0.55)
                )
        }
        .padding(.horizontal, 16)
        .frame(minHeight: 54)
        .background(LegendNextColor.surface)
        .accessibilityElement(children: .contain)
    }

    private var locationRow: some View {
        HStack(spacing: 14) {
            Image(systemName: "mappin.and.ellipse")
                .font(.system(size: 20))
                .frame(width: 26)
                .foregroundStyle(
                    LegendNextColor.textPrimary
                )

            TextField(
                "Add location",
                text: $shareLocation
            )
            .font(.system(size: 15))
            .foregroundStyle(
                LegendNextColor.textPrimary
            )

            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(
                    LegendNextColor.textSecondary.opacity(0.55)
                )
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
        .background(LegendNextColor.surface)
    }

    private var musicRow: some View {
        Button {
            musicReturnStage = .share
            stage = .music
        } label: {
            HStack(spacing: 14) {
                Image(systemName: "music.note")
                    .font(.system(size: 20))
                    .frame(width: 26)
                    .foregroundStyle(
                        LegendNextColor.textPrimary
                    )

                VStack(
                    alignment: .leading,
                    spacing: 2
                ) {
                    Text("Add music")
                        .font(.system(size: 15))
                        .foregroundStyle(
                            LegendNextColor.textPrimary
                        )

                    if let selectedMusic {
                        Text(
                            "\(selectedMusic.track.trackTitle) · " +
                            selectedMusic.track.artistName
                        )
                        .font(.system(size: 12))
                        .foregroundStyle(
                            LegendNextColor.textSecondary
                        )
                        .lineLimit(1)
                    }
                }

                Spacer(minLength: 8)

                Image(systemName: "chevron.right")
                    .font(
                        .system(
                            size: 13,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(
                        LegendNextColor.textSecondary.opacity(0.55)
                    )
            }
            .padding(.horizontal, 16)
            .frame(minHeight: 54)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(selectedMedia.isEmpty)
        .background(LegendNextColor.surface)
        .accessibilityLabel(
            selectedMusic == nil
                ? "Add music"
                : "Change music"
        )
    }

    @ViewBuilder
    private var hacPreviewRow: some View {
        if type.requiresVideo,
           let sourceURL = selectedMedia.first?.videoFileURL {
            Button {
                // The picker reads the same prepared MP4 that will be uploaded,
                // so the selected frame always represents the published Hac.
                guard FileManager.default.fileExists(atPath: sourceURL.path) else {
                    mediaSelectionError = "Legend could not read this video preview. Choose the video again and try once more."
                    return
                }
                isSelectingHacPreview = true
            } label: {
                HStack(spacing: 14) {
                    Group {
                        if let selectedHacPreviewData,
                           let preview = UIImage(data: selectedHacPreviewData) {
                            Image(uiImage: preview)
                                .resizable()
                                .scaledToFill()
                        } else {
                            Image(systemName: "film")
                                .font(.system(size: 20, weight: .semibold))
                                .foregroundStyle(LegendNextColor.goldBright)
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                                .background(LegendNextColor.navy)
                        }
                    }
                    .frame(width: 38, height: 48)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))

                    VStack(alignment: .leading, spacing: 2) {
                        Text("Preview frame")
                            .font(.system(size: 15))
                            .foregroundStyle(LegendNextColor.textPrimary)
                        Text("Choose what appears in home and your profile")
                            .font(.system(size: 12))
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(1)
                    }

                    Spacer(minLength: 8)

                    Image(systemName: "chevron.right")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(LegendNextColor.textSecondary.opacity(0.55))
                }
                .padding(.horizontal, 16)
                .frame(minHeight: 62)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .background(LegendNextColor.surface)
            .accessibilityLabel("Choose Hac preview frame")
        }
    }

    private var accessibilityRow: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "accessibility")
                .font(.system(size: 20))
                .frame(width: 26)
                .foregroundStyle(
                    LegendNextColor.textPrimary
                )
                .padding(.top, 9)

            TextField(
                "Write alt text",
                text: $accessibilityText,
                axis: .vertical
            )
            .lineLimit(1...3)
            .font(.system(size: 15))
            .foregroundStyle(
                LegendNextColor.textPrimary
            )
            .padding(.vertical, 9)

            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(
                    LegendNextColor.textSecondary.opacity(0.55)
                )
                .padding(.top, 12)
        }
        .padding(.horizontal, 16)
        .frame(minHeight: 54)
        .background(LegendNextColor.surface)
    }

    private var commentsRow: some View {
        HStack(spacing: 14) {
            Image(systemName: "ellipsis.bubble")
                .font(.system(size: 20))
                .frame(width: 26)
                .foregroundStyle(
                    LegendNextColor.textPrimary
                )

            VStack(
                alignment: .leading,
                spacing: 2
            ) {
                Text("Allow comments")
                    .font(.system(size: 15))
                    .foregroundStyle(
                        LegendNextColor.textPrimary
                    )

                Text("Advanced settings")
                    .font(.system(size: 12))
                    .foregroundStyle(
                        LegendNextColor.textSecondary
                    )
            }

            Spacer(minLength: 8)

            Toggle(
                "",
                isOn: $commentsEnabled
            )
            .labelsHidden()
            .tint(LegendNextColor.goldBright)
        }
        .padding(.horizontal, 16)
        .frame(minHeight: 58)
        .background(LegendNextColor.surface)
    }

    @ViewBuilder
    private func accessibilityEditor(isDark: Bool) -> some View {
        if !selectedMedia.isEmpty {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                Text("Accessibility")
                    .font(LegendNextTypography.label)
                    .foregroundStyle(isDark ? Color.white.opacity(0.80) : LegendNextColor.textSecondary)
                TextField(
                    "Describe this media",
                    text: $accessibilityText,
                    axis: .vertical)
                .textFieldStyle(.plain)
                .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
                .padding(LegendNextSpacing.sm)
                .background(
                    isDark ? Color.white.opacity(0.10) : LegendNextColor.surfaceInset,
                    in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
                .accessibilityLabel("Media description")
            }
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
            stage = .metadata

        case .metadata:
            stage = .share

        case .share:
            publish()

        default:
            break
        }
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
                refreshDefaultHacPreview(for: media)
            } catch {
                mediaSelectionError = "Legend could not prepare this media. The draft was kept intact."
            }
            stage = .library
        }
    }

    private func remove(_ media: LegendSocialMediaDraft) {
        selectedMedia.removeAll { $0.id == media.id }
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
            refreshDefaultHacPreview(for: media)
            mediaSelectionError = nil
            stage = .library

        case .failure:
            mediaSelectionError = "The captured media could not be prepared. Your draft was kept intact."
            stage = .library
        }
    }

    private func publish() {
        let request = MobileSocialPublishRequest(
            contentType: type,
            body: normalizedPublicationBody,
            files: selectedMedia.map(\.multipartFile),
            accessibilityText: normalizedAccessibilityText,
            music: selectedMusic?.selection,
            audience: .authorizedNetwork,
            location: normalizedLocation,
            commentsEnabled: commentsEnabled,
            previewImage: hacPreviewMultipartFile)

        if social.beginPublication(request) {
            ownsMediaAfterDismissal = true
            stage = .handedOff
            dismiss()
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

                            Text(query.isEmpty ? "Christian music" : "Search results")
                                .font(LegendNextTypography.section)
                                .foregroundStyle(.white)

                            if isSearching {
                                HStack(spacing: LegendNextSpacing.xs) {
                                    Text("Finding music")
                                        .font(LegendNextTypography.supporting)
                                        .foregroundStyle(Color.white.opacity(0.70))
                                }
                            } else if tracks.isEmpty {
                                Label("No matching music found", systemImage: "music.quarternote.3")
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
            Button("Cancel") {
                stopPreview()
                cancel()
            }
            .font(LegendNextTypography.label)
            .foregroundStyle(LegendNextColor.goldBright)
            .frame(width: LegendNextSize.prominentControlHeight, height: LegendNextSize.prominentControlHeight)
            .accessibilityLabel("Cancel music selection")

            Spacer()

            Text("Add music")
                .font(LegendNextTypography.section)
                .foregroundStyle(.white)

            Spacer()

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
            .font(LegendNextTypography.label)
            .foregroundStyle(
                selectedTrack != nil && trimEnd > trimStart
                    ? LegendNextColor.goldBright
                    : Color.white.opacity(0.38))
            .frame(width: LegendNextSize.prominentControlHeight, height: LegendNextSize.prominentControlHeight)
            .disabled(selectedTrack == nil || trimEnd <= trimStart)
            .accessibilityLabel("Add selected music")
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var musicSearchField: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(LegendNextColor.goldBright)
                .accessibilityHidden(true)
            TextField("Search Christian music or an artist", text: $query)
                .font(LegendNextTypography.body)
                .foregroundStyle(.white)
                .tint(LegendNextColor.goldBright)
                .submitLabel(.search)
                .onSubmit { search(query) }
                .accessibilityLabel("Search music")
            if !query.isEmpty {
                Button {
                    query = ""
                    search("Christian music")
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(Color.white.opacity(0.62))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear music search")
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
            .accessibilityLabel("Select \(track.trackTitle) by \(track.artistName)")

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
                .accessibilityLabel("Preview \(track.trackTitle)")
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
            Text("Clip and audio mix")
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
            .accessibilityLabel("\(artistName) artist monogram")
    }
}

/// Editing tools that are actually wired to behaviour.
///
/// `overlay` and `filter` were removed rather than left on screen: `overlay` bound to
/// the same caption field as `text`, and `filter` rendered a fixed "Original
/// presentation" label with no filter pipeline behind it. Adjustment, crop, and filter
/// tooling returns when the rendering pipeline that backs it exists.
private enum LegendSocialEditingTool: CaseIterable, Identifiable {
    case audio
    case text
    case describe

    var id: Self { self }

    var title: String {
        switch self {
        case .audio: "Audio"
        case .text: "Text"
        case .describe: "Alt text"
        }
    }

    var systemImage: String {
        switch self {
        case .audio: "music.note"
        case .text: "textformat"
        case .describe: "text.below.photo"
        }
    }
}

private enum LegendSocialMediaPreviewPresentation {
    case selection
    case canvas
    case thumbnail
}

private struct LegendSocialMediaPreview: View {
    let media: LegendSocialMediaDraft
    let presentation: LegendSocialMediaPreviewPresentation
    let overlayText: String?
    let dimmingEdges: Bool
    let remove: () -> Void

    init(
        media: LegendSocialMediaDraft,
        presentation: LegendSocialMediaPreviewPresentation,
        overlayText: String? = nil,
        dimmingEdges: Bool = false,
        remove: @escaping () -> Void
    ) {
        self.media = media
        self.presentation = presentation
        self.overlayText = overlayText
        self.dimmingEdges = dimmingEdges
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

            if dimmingEdges {
                LinearGradient(
                    colors: [
                        .clear,
                        LegendNextColor.midnight.opacity(0.30)
                    ],
                    startPoint: .top,
                    endPoint: .bottom)

                LinearGradient(
                    colors: [
                        .clear,
                        LegendNextColor.midnight.opacity(0.30)
                    ],
                    startPoint: .leading,
                    endPoint: .trailing)
            }

            if presentation == .thumbnail {
                LinearGradient(
                    colors: [.clear, LegendNextColor.midnight.opacity(0.68)],
                    startPoint: .center,
                    endPoint: .bottom)

                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Spacer(minLength: 0)
                    Text(media.kindDescription)
                        .font(.caption.weight(.bold))
                    Text(media.fileName)
                        .font(.caption2)
                        .lineLimit(1)
                }
                .foregroundStyle(.white)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                .padding(LegendNextSpacing.sm)
            }

            if presentation == .canvas,
               let overlayText,
               !overlayText.isEmpty {
                Text(overlayText)
                    .font(LegendNextTypography.cardTitle)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.white)
                    .padding(LegendNextSpacing.sm)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .background(LegendNextColor.midnight.opacity(0.38), in: Capsule())
                    .padding(LegendNextSpacing.md)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .accessibilityLabel("Story text: \(overlayText)")
            }

            if presentation == .selection {
                Button(action: remove) {
                    Image(systemName: "xmark")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: 30, height: 30)
                        .background(.white, in: Circle())
                }
                .padding(LegendNextSpacing.xs)
                .accessibilityLabel("Remove selected media")
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
    let save: (Data) -> Void
    let cancel: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var player: AVPlayer
    @State private var duration: Double = 1
    @State private var selectedSeconds: Double = 0

    init(
        sourceURL: URL,
        save: @escaping (Data) -> Void,
        cancel: @escaping () -> Void
    ) {
        self.sourceURL = sourceURL
        self.save = save
        self.cancel = cancel
        _player = State(initialValue: AVPlayer(url: sourceURL))
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                VideoPlayer(player: player)
                    .aspectRatio(9 / 16, contentMode: .fit)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(
                        cornerRadius: LegendNextRadius.card,
                        style: .continuous))

                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    Text("Scroll to choose the preview frame")
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)

                    Slider(
                        value: $selectedSeconds,
                        in: 0 ... max(duration, 0.1))
                    .tint(LegendNextColor.goldBright)
                    .onChange(of: selectedSeconds) { _, seconds in
                        player.seek(
                            to: CMTime(seconds: seconds, preferredTimescale: 600),
                            toleranceBefore: .zero,
                            toleranceAfter: .zero)
                    }

                    Text(LegendHacPreviewFrame.timeLabel(selectedSeconds))
                        .font(LegendNextTypography.caption.monospacedDigit())
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.canvas)
            .navigationTitle("Hac preview")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        player.pause()
                        cancel()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Use frame") {
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
                await player.seek(to: .zero)
            }
            .onDisappear {
                player.pause()
            }
        }
    }
}

/// One JPEG-generation authority for the Hac publishing flow. The 1080px cap
/// and progressive compression keep a user-selected poster below the server's
/// 1 MB ingress limit without changing the source video.
private enum LegendHacPreviewFrame {
    private static let maximumEdge: CGFloat = 1_080
    private static let maximumBytes = 1_024 * 1_024

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

        for quality in [CGFloat(0.82), 0.70, 0.58] {
            if let data = normalized.jpegData(compressionQuality: quality),
               data.count <= maximumBytes {
                return data
            }
        }
        return nil
    }

    static func timeLabel(_ seconds: Double) -> String {
        let wholeSeconds = max(0, Int(seconds.rounded(.down)))
        return String(format: "%d:%02d", wholeSeconds / 60, wholeSeconds % 60)
    }
}

private enum LegendSocialMediaLoadingError: Error {
    case unavailable
    case unsupported
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
            "Camera capture is unavailable on this device."
        case .permissionDenied:
            "Camera access is required to capture a new update."
        case .captureFailed:
            "Legend could not complete that capture."
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
        captureButton.accessibilityLabel = recording ? "Stop video recording" : "Start video recording"
        setStatus(recording ? "Recording · tap to finish" : captureStatus)
    }

    private var captureStatus: String {
        guard capturesVideo else { return "Photo capture" }
        let audioIsAvailable = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        let mode = audioIsAvailable ? "Video capture" : "Video capture without audio"
        guard let maximumVideoDuration else { return mode }
        return "\(mode) · up to \(Int(maximumVideoDuration)) seconds"
    }

    private func updateCaptureModeAppearance() {
        let nextModeSymbol = capturesVideo ? "camera.fill" : "video.fill"
        captureModeButton.setImage(UIImage(systemName: nextModeSymbol), for: .normal)
        captureModeButton.accessibilityLabel = capturesVideo
            ? "Switch to photo capture"
            : "Switch to video capture"
        captureButton.accessibilityLabel = capturesVideo
            ? "Start video recording"
            : "Capture photo"
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
                : "Hacs use one video. Choose a video to continue."
        }

        guard acceptsVideos else {
            return "This media is not available for the selected format."
        }

        guard format.acceptsVideo(duration: asset.duration) else {
            return "Videos must be 10 minutes or less."
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
        var description = asset.isVideo ? "Video" : "Photo"
        if asset.isVideo {
            description += ", \(durationLabel)"
        }
        if let selectionIndex {
            description += ", selected, position \(selectionIndex)"
        } else if isSelected {
            description += ", selected"
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
                    .accessibilityLabel("Close preview")

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
                        isSelected ? "Remove from selection" : "Add to selection",
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
