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
        .presentationDetents([.large])
        .presentationDragIndicator(.hidden)
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
    @State private var activeStoryTool: LegendSocialStoryEditingTool = .text
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
            .toolbar(.hidden, for: .navigationBar)
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

            Text("New \(type.displayName.lowercased())")
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
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var legendModeRail: some View {
        LegendSocialCreationModeRail(selection: $type)
    }

    private var eligibleLibraryAssets: [LegendPhotoLibraryAsset] {
        photoLibrary.visibleAssets.filter(type.accepts)
    }

    private var selectionAspectRatio: CGFloat {
        switch type {
        case .post:
            1
        case .story, .reel:
            9 / 16
        }
    }

    private var emptyPreviewHeight: CGFloat {
        switch type {
        case .post:
            286
        case .story, .reel:
            440
        }
    }

    private var primaryPreviewSize: CGSize {
        switch type {
        case .post:
            CGSize(width: 286, height: 286)
        case .story, .reel:
            CGSize(width: 248, height: 440)
        }
    }

    private var companionPreviewSize: CGSize {
        switch type {
        case .post:
            CGSize(width: 132, height: 132)
        case .story, .reel:
            CGSize(width: 124, height: 220)
        }
    }

    private var libraryTileHeight: CGFloat {
        switch type {
        case .post:
            112
        case .story, .reel:
            196
        }
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
                .accessibilityLabel("Open camera for \(type.displayName.lowercased())")
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
        if type == .post {
            ScrollView(.horizontal) {
                HStack(
                    alignment: .top,
                    spacing: LegendNextSpacing.xs
                ) {
                    if let primaryMedia = selectedMedia.first {
                        LegendSocialMediaPreview(
                            media: primaryMedia,
                            presentation: .featured,
                            overlayText: overlayText,
                            remove: { remove(primaryMedia) }
                        )
                        .frame(
                            width: primaryPreviewSize.width,
                            height: primaryPreviewSize.height
                        )
                    }

                    ForEach(Array(selectedMedia.dropFirst())) { media in
                        LegendSocialMediaPreview(
                            media: media,
                            presentation: .companion,
                            remove: { remove(media) }
                        )
                        .frame(
                            width: companionPreviewSize.width,
                            height: companionPreviewSize.height
                        )
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
        } else {
            HStack {
                Spacer(minLength: 0)

                if let primaryMedia = selectedMedia.first {
                    LegendSocialMediaPreview(
                        media: primaryMedia,
                        presentation: .featured,
                        overlayText: overlayText,
                        remove: { remove(primaryMedia) }
                    )
                    .frame(
                        width: primaryPreviewSize.width,
                        height: primaryPreviewSize.height
                    )
                }

                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 2)
            .background(
                isDark ? Color.clear : LegendNextColor.surfaceInset,
                in: RoundedRectangle(
                    cornerRadius: LegendNextRadius.card,
                    style: .continuous
                )
            )
        }
    }

    private var mediaGrid: some View {
        GeometryReader { proxy in
            let spacing: CGFloat = 2
            let columnsCount = 3
            let availableWidth =
                proxy.size.width -
                (spacing * CGFloat(columnsCount - 1))
            let tileWidth = floor(
                availableWidth / CGFloat(columnsCount)
            )
            let tileHeight: CGFloat = {
                switch type {
                case .post:
                    return tileWidth
                case .story, .reel:
                    return tileWidth / selectionAspectRatio
                }
            }()

            let columns = Array(
                repeating: GridItem(
                    .fixed(tileWidth),
                    spacing: spacing
                ),
                count: columnsCount
            )

            LazyVGrid(
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
                        isEligible: true,
                        select: { select(asset) }
                    )
                    .frame(
                        width: tileWidth,
                        height: tileHeight
                    )
                    .clipped()
                }

                if photoLibrary.canLoadMore {
                    Button {
                        photoLibrary.loadNextPage()
                    } label: {
                        VStack(spacing: LegendNextSpacing.xs) {
                            Image(systemName: "plus")
                                .font(.title3.weight(.bold))

                            Text("More")
                                .font(LegendNextTypography.label)
                        }
                        .foregroundStyle(LegendNextColor.goldBright)
                        .frame(
                            width: tileWidth,
                            height: tileHeight
                        )
                        .background(
                            Color.white.opacity(0.08),
                            in: RoundedRectangle(
                                cornerRadius: LegendNextRadius.control,
                                style: .continuous
                            )
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(
                        "Show more recent \(type.displayName.lowercased()) media"
                    )
                }
            }
        }
        .frame(
            minHeight:
                type == .post
                    ? 226
                    : libraryTileHeight * 2 + 2
        )
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

    @ViewBuilder
    private var metadataContent: some View {
        if type == .story {
            storyDetailsContent
        } else {
            publicationDetailsContent
        }
    }

    private var publicationDetailsContent: some View {
        VStack(spacing: 0) {
            metadataHeader(isDark: false)

            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                    metadataMediaPreview(isDark: false)
                    captionEditor(isDark: false)
                    musicSelection(isDark: false)
                    accessibilityEditor(isDark: false)
                    publicationFailure
                }
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.top, LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.md)
            }
            .scrollIndicators(.hidden)
        }
        .background(LegendNextColor.canvas)
        .safeAreaInset(edge: .bottom, spacing: 0) {
            shareControl
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.sm)
                .background(LegendNextColor.canvas)
        }
    }

    private var storyDetailsContent: some View {
        ZStack {
            LegendNextGradient.hero
                .ignoresSafeArea()

            VStack(spacing: 0) {
                metadataHeader(isDark: true)

                ScrollView {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                        storyEditingCanvas
                        storyToolRail
                        storyToolDetail
                        publicationFailure
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.top, LegendNextSpacing.sm)
                    .padding(.bottom, LegendNextSpacing.md)
                }
                .scrollIndicators(.hidden)
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            shareControl
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.sm)
                .background(LegendNextColor.midnight)
        }
    }

    private func metadataHeader(isDark: Bool) -> some View {
        HStack {
            Button {
                stage = .library
            } label: {
                Image(systemName: "chevron.left")
                    .font(.title3.weight(.semibold))
                    .frame(
                        width: LegendNextSize.prominentControlHeight,
                        height: LegendNextSize.prominentControlHeight)
                    .background(
                        isDark ? Color.white.opacity(0.12) : LegendNextColor.fill,
                        in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
            .accessibilityLabel("Back to media selection")

            Spacer()

            Text("New \(type.displayName.lowercased())")
                .font(LegendNextTypography.section)
                .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)

            Spacer()

            Color.clear
                .frame(
                    width: LegendNextSize.prominentControlHeight,
                    height: LegendNextSize.prominentControlHeight)
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    @ViewBuilder
    private func metadataMediaPreview(isDark: Bool) -> some View {
        if selectedMedia.isEmpty {
            LegendNextInsetSurface {
                Label("Text-only \(type.displayName.lowercased())", systemImage: type.systemImage)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(isDark ? Color.white.opacity(0.72) : LegendNextColor.textSecondary)
            }
        } else {
            mediaPreviewStrip(isDark: isDark)
        }
    }

    private var storyEditingCanvas: some View {
        Group {
            if selectedMedia.isEmpty {
                LegendNextInsetSurface {
                    Label("Select a story visual", systemImage: "photo.on.rectangle.angled")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(Color.white.opacity(0.72))
                }
            } else {
                mediaPreviewStrip(
                    isDark: true,
                    overlayText: caption.isEmpty ? "Tap Text to add a message" : caption)
            }
        }
    }

    private var storyToolRail: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            ForEach(LegendSocialStoryEditingTool.allCases) { tool in
                Button {
                    activeStoryTool = tool
                    if tool == .audio {
                        stage = .music
                    }
                } label: {
                    VStack(spacing: LegendNextSpacing.micro) {
                        Image(systemName: tool.systemImage)
                            .font(.title3.weight(.semibold))
                        Text(tool.title)
                            .font(.caption.weight(.semibold))
                    }
                    .foregroundStyle(
                        activeStoryTool == tool
                            ? LegendNextColor.midnight
                            : .white)
                    .frame(maxWidth: .infinity)
                    .frame(height: 74)
                    .background(
                        activeStoryTool == tool
                            ? LegendNextColor.goldBright
                            : Color.white.opacity(0.10),
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Story (tool.title)")
                .accessibilityAddTraits(activeStoryTool == tool ? .isSelected : [])
            }
        }
    }

    @ViewBuilder
    private var storyToolDetail: some View {
        switch activeStoryTool {
        case .audio:
            musicSelection(isDark: true)
        case .text, .overlay:
            captionEditor(isDark: true)
        case .filter:
            LegendNextInsetSurface {
                Label("Original presentation", systemImage: "camera.filters")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(Color.white.opacity(0.80))
            }
        case .edit:
            accessibilityEditor(isDark: true)
        }
    }

    private func captionEditor(isDark: Bool) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            Text(type == .story ? "Add text" : "Write a caption")
                .font(LegendNextTypography.section)
                .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
            TextEditor(text: $caption)
                .font(LegendNextTypography.body)
                .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
                .scrollContentBackground(.hidden)
                .frame(minHeight: type == .story ? 150 : 118)
                .padding(LegendNextSpacing.xs)
                .background(
                    isDark ? Color.white.opacity(0.10) : LegendNextColor.surfaceInset,
                    in: RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous)
                        .strokeBorder(
                            isDark ? Color.white.opacity(0.14) : LegendNextColor.separator,
                            lineWidth: 1)
                }
                .accessibilityLabel("\(type.displayName) caption")
        }
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

    private var shareControl: some View {
        Button(action: primaryAction) {
            Label("Share \(type.displayName)", systemImage: "arrow.up.circle.fill")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(LegendButtonStyle(kind: .primary))
        .disabled(!canContinue)
        .accessibilityLabel("Share \(type.displayName)")
    }

    @ViewBuilder
    private var publicationFailure: some View {
        if let failure = social.actionFailure {
            Label(failure.message, systemImage: "exclamationmark.triangle.fill")
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.danger)
        }
    }

    private func musicSelection(isDark: Bool) -> some View {
        Button {
            stage = .music
        } label: {
            HStack(spacing: LegendNextSpacing.sm) {
                LegendSocialMusicArtwork(
                    artistName: selectedMusic?.track.artistName ?? "Legend")
                VStack(alignment: .leading, spacing: 2) {
                    Text(selectedMusic?.track.trackTitle ?? "Add licensed music")
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(isDark ? .white : LegendNextColor.textPrimary)
                    Text(selectedMusic.map { "\($0.track.artistName) · \($0.track.trackTitle)" } ?? "Browse Christian music or search for a song.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(isDark ? Color.white.opacity(0.68) : LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
                Image(systemName: "chevron.right")
                    .foregroundStyle(isDark ? Color.white.opacity(0.68) : LegendNextColor.textSecondary)
            }
            .padding(LegendNextSpacing.sm)
            .background(
                isDark ? Color.white.opacity(0.10) : LegendNextColor.surfaceElevated,
                in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
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
                                    ProgressView()
                                        .tint(LegendNextColor.goldBright)
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

            if track.previewURL != nil {
                Button {
                    preview(track)
                } label: {
                    Image(systemName: "play.fill")
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

private enum LegendSocialStoryEditingTool: CaseIterable, Identifiable {
    case audio
    case text
    case overlay
    case filter
    case edit

    var id: Self { self }

    var title: String {
        switch self {
        case .audio: "Audio"
        case .text: "Text"
        case .overlay: "Overlay"
        case .filter: "Filter"
        case .edit: "Edit"
        }
    }

    var systemImage: String {
        switch self {
        case .audio: "music.note"
        case .text: "textformat"
        case .overlay: "square.3.layers.3d.top.filled"
        case .filter: "camera.filters"
        case .edit: "slider.horizontal.3"
        }
    }
}

private enum LegendSocialMediaPreviewPresentation {
    case featured
    case companion
}

private struct LegendSocialMediaPreview: View {
    let media: LegendSocialMediaDraft
    let presentation: LegendSocialMediaPreviewPresentation
    let overlayText: String?
    let remove: () -> Void

    init(
        media: LegendSocialMediaDraft,
        presentation: LegendSocialMediaPreviewPresentation,
        overlayText: String? = nil,
        remove: @escaping () -> Void
    ) {
        self.media = media
        self.presentation = presentation
        self.overlayText = overlayText
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
                .background(Color.black.opacity(0.16))

            if presentation == .companion {
                LinearGradient(
                    colors: [.clear, Color.black.opacity(0.68)],
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

            if presentation == .featured,
               let overlayText,
               !overlayText.isEmpty {
                Text(overlayText)
                    .font(LegendNextTypography.cardTitle)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.white)
                    .padding(LegendNextSpacing.sm)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .background(Color.black.opacity(0.38), in: Capsule())
                    .padding(LegendNextSpacing.md)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .accessibilityLabel("Story text: \(overlayText)")
            }

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
