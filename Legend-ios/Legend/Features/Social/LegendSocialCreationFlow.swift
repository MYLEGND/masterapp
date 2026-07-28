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
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text("Create")
                        .font(.title3.weight(.bold))
                        .foregroundStyle(.primary)
                    Text("Choose the kind of Legend update you want to share.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }

                ForEach(MobileSocialContentType.allCases) { type in
                    Button {
                        select(type)
                    } label: {
                        HStack(spacing: LegendNextSpacing.sm) {
                            Image(systemName: type.systemImage)
                                .font(.title3.weight(.semibold))
                                .foregroundStyle(LegendNextColor.navy)
                                .frame(width: 38, height: 38)
                                .background(
                                    LegendNextColor.gold.opacity(0.18),
                                    in: Circle())

                            VStack(alignment: .leading, spacing: 2) {
                                Text(type.displayName)
                                    .font(.body.weight(.semibold))
                                    .foregroundStyle(.primary)
                                Text(type.creationPrompt)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(2)
                            }

                            Spacer(minLength: LegendNextSpacing.xs)
                            Image(systemName: "chevron.right")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(.tertiary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Create \(type.displayName)")
                    .accessibilityHint(type.creationPrompt)
                }

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.md)
            .background(Color(uiColor: .systemBackground))
            .navigationTitle("Create")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: cancel)
                }
            }
        }
        .presentationDetents([.medium])
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

                    TextEditor(text: $caption)
                        .font(LegendNextTypography.body)
                        .padding(LegendNextSpacing.sm)
                        .frame(minHeight: 150)
                        .background(
                            Color(uiColor: .secondarySystemBackground),
                            in: RoundedRectangle(
                                cornerRadius: LegendNextRadius.control,
                                style: .continuous))
                        .accessibilityLabel("\(type.displayName) caption")

                    PhotosPicker(
                        selection: $pickerItems,
                        maxSelectionCount: 10,
                        matching: .any(of: [.images, .videos]),
                        photoLibrary: .shared()
                    ) {
                        Label(
                            selectedMedia.isEmpty
                                ? "Choose photo or video"
                                : "Replace selected media",
                            systemImage: "photo.on.rectangle")
                    }
                    .buttonStyle(LegendButtonStyle(kind: .secondary))
                    .accessibilityLabel("Choose photos or videos from the system picker")

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
        !caption.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !selectedMedia.isEmpty
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

    private func publish() {
        guard !isPublishing else { return }

        isPublishing = true
        let request = MobileSocialPublishRequest(
            contentType: type,
            body: caption,
            files: selectedMedia.map(\.multipartFile),
            accessibilityText: normalizedAccessibilityText,
            music: nil)

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
