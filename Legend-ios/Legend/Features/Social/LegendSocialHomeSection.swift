import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

/// The native home is intentionally a composition over the protected mobile
/// projections. It never derives a role, feed audience, finance state, or
/// profile identity in the client.
struct LegendSocialHomeSection<DashboardContent: View>: View {
    let session: MobileSession
    let home: MobileHomeResponse
    @ObservedObject var social: MobileSocialStore
    let openMessages: () -> Void
    let openCircles: () -> Void
    let dashboardContent: DashboardContent

    @State private var composerType: MobileSocialContentType = .post
    @State private var composerBody = ""
    @State private var isPresentingComposer = false
    @State private var isPresentingActivity = false
    @State private var commentTarget: MobileSocialPost?
    @State private var commentBody = ""

    init(
        session: MobileSession,
        home: MobileHomeResponse,
        social: MobileSocialStore,
        openMessages: @escaping () -> Void,
        openCircles: @escaping () -> Void,
        @ViewBuilder dashboardContent: () -> DashboardContent
    ) {
        self.session = session
        self.home = home
        _social = ObservedObject(wrappedValue: social)
        self.openMessages = openMessages
        self.openCircles = openCircles
        self.dashboardContent = dashboardContent()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
            topBar
            storyContent
            dashboardContent
            socialFeed
        }
        .sheet(isPresented: $isPresentingComposer, onDismiss: clearComposer) {
            LegendSocialComposer(
                type: $composerType,
                messageBody: $composerBody,
                submit: shareUpdate,
                cancel: { isPresentingComposer = false })
        }
        .sheet(isPresented: $isPresentingActivity) {
            LegendActivitySheet(activity: activity)
        }
        .sheet(item: $commentTarget, onDismiss: { commentBody = "" }) { post in
            LegendCommentComposer(
                authorName: post.author.displayName,
                messageBody: $commentBody,
                submit: { submitComment(to: post) },
                cancel: { commentTarget = nil })
        }
        .alert(
            social.actionFailure?.title ?? "Legend update unavailable",
            isPresented: failurePresentation,
            actions: {
                Button("OK", role: .cancel) { social.dismissActionFailure() }
            },
            message: {
                Text(social.actionFailure?.message ?? "The request could not be completed.")
            })
    }

    private var activity: [MobileSocialActivity] {
        guard case .loaded(let snapshot) = social.state else { return [] }
        return snapshot.activity
    }

    private var activityCount: Int {
        guard case .loaded(let snapshot) = social.state else { return 0 }
        return snapshot.activityCount
    }

    private var failurePresentation: Binding<Bool> {
        Binding(
            get: { social.actionFailure != nil },
            set: { if !$0 { social.dismissActionFailure() } })
    }

    private var topBar: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Button {
                composerType = .story
                isPresentingComposer = true
            } label: {
                Image(systemName: "plus")
                    .font(.title3.weight(.semibold))
                    .frame(width: 42, height: 42)
                    .background(LegendNextColor.surfaceElevated, in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendNextColor.textPrimary)
            .accessibilityLabel("Create a Legend story")

            Spacer(minLength: LegendNextSpacing.sm)

            Text("LEGEND")
                .font(
                    .system(
                        size: 23,
                        weight: .bold,
                        design: .default
                    )
                )
                .tracking(4.6)
                .foregroundStyle(LegendNextColor.gold)
                .accessibilityAddTraits(.isHeader)

            Spacer(minLength: LegendNextSpacing.sm)

            Button { isPresentingActivity = true } label: {
                ZStack(alignment: .topTrailing) {
                    Image(systemName: "heart")
                        .font(.title3.weight(.semibold))
                        .frame(width: 42, height: 42)
                        .background(LegendNextColor.surfaceElevated, in: Circle())
                    if activityCount > 0 {
                        Text("\(min(activityCount, 99))")
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .padding(5)
                            .background(LegendNextColor.danger, in: Circle())
                            .offset(x: 4, y: -4)
                    }
                }
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendNextColor.textPrimary)
            .accessibilityLabel("Open activity, \(activityCount) recent interactions")
        }
        .padding(.top, LegendNextSpacing.xs)
    }

    @ViewBuilder
    private var storyContent: some View {
        if case .loaded(let snapshot) = social.state {
            LegendStoryRail(
                currentActor: session.actor,
                stories: snapshot.stories,
                createStory: {
                    composerType = .story
                    isPresentingComposer = true
                }
            )
        }
    }

    @ViewBuilder
    private var socialFeed: some View {
        switch social.state {
        case .idle, .loading:
            LegendSocialLoadingSection()

        case .unavailable(let failure):
            LegendErrorCard(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: social.load
            )

        case .loaded(let snapshot):
            if session.actor.identity.participantType == .client,
               let journey = home.journey {
                Button(action: openCircles) {
                    HStack(spacing: LegendNextSpacing.sm) {
                        Image(systemName: "person.3.fill")
                            .foregroundStyle(LegendNextColor.gold)

                        Text("Journey Circles")
                            .font(.subheadline.weight(.semibold))

                        Spacer()

                        Text("\(journey.connectedPeerCount) connected")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(
                                LegendNextColor.textSecondary
                            )

                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(
                                LegendNextColor.textSecondary
                            )
                    }
                    .padding(LegendNextSpacing.sm)
                    .background(
                        LegendNextColor.surfaceElevated,
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous
                        )
                    )
                }
                .buttonStyle(.plain)
                .accessibilityLabel(
                    "Open Journey Circles. \(journey.connectedPeerCount) connected profiles"
                )
            }

            if session.actor.identity.participantType == .client,
               !snapshot.activity.isEmpty {
                circleActivity(snapshot.activity)
            }

            LegendNextSectionHeader(
                eyebrow: "Network",
                title: "Latest from Legend"
            )

            if snapshot.posts.isEmpty {
                LegendSocialEmptyFeed {
                    composerType = .post
                    isPresentingComposer = true
                }
            } else {
                ForEach(Array(snapshot.posts.prefix(3))) { post in
                    LegendSocialPostCard(
                        post: post,
                        currentIdentity: session.actor.identity,
                        social: social,
                        react: {
                            social.toggleReaction(postID: post.id)
                        },
                        comment: {
                            commentTarget = post
                        },
                        follow: {
                            social.toggleFollow(author: post.author)
                        }
                    )
                }
            }
        }
    }

    private func circleActivity(
        _ activity: [MobileSocialActivity]
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Your network",
                title: "Circle activity"
            )

            LegendNextSurface(style: .elevated) {
                VStack(spacing: LegendNextSpacing.xs) {
                    ForEach(Array(activity.prefix(3))) { item in
                        HStack(spacing: LegendNextSpacing.xs) {
                            LegendProfileAvatar(
                                avatar: item.actor.avatar,
                                displayName: item.actor.displayName,
                                size: 34
                            )

                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text(item.actor.displayName)
                                    .font(LegendNextTypography.bodyEmphasis)
                                    .foregroundStyle(LegendNextColor.textPrimary)
                                    .lineLimit(1)

                                Text(item.summary)
                                    .font(LegendNextTypography.supporting)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .lineLimit(1)
                            }

                            Spacer(minLength: LegendNextSpacing.xs)

                            VStack(
                                alignment: .trailing,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Image(systemName: item.systemImage)
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(LegendNextColor.information)

                                Text(item.occurredUTC, style: .relative)
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .lineLimit(1)
                            }
                        }
                    }
                }
            }
        }
    }

    private func shareUpdate(
        attachment: LegendSocialImageAttachment?
    ) {
        let body = composerBody.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty || attachment != nil else { return }

        if let attachment {
            social.createMediaPost(
                body: body,
                files: [attachment.file],
                accessibilityText: attachment.accessibilityText
            )
        } else {
            social.createPost(type: composerType, body: body)
        }
        isPresentingComposer = false
        clearComposer()
    }

    private func clearComposer() {
        composerType = .post
        composerBody = ""
    }

    private func submitComment(to post: MobileSocialPost) {
        let body = commentBody.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty else { return }
        social.addComment(postID: post.id, body: body)
        commentTarget = nil
        commentBody = ""
    }
}

private struct LegendStoryRail: View {
    let currentActor: MobileActor
    let stories: [MobileSocialPost]
    let createStory: () -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: LegendNextSpacing.md) {
                Button(action: createStory) {
                    VStack(spacing: LegendNextSpacing.xs) {
                        ZStack(alignment: .bottomTrailing) {
                            LegendProfileAvatar(
                                avatar: currentActor.avatar,
                                displayName: currentActor.displayName,
                                size: 58)
                                .padding(3)
                                .overlay { Circle().stroke(LegendNextColor.gold, lineWidth: 2) }
                            Image(systemName: "plus")
                                .font(.caption.weight(.black))
                                .foregroundStyle(.white)
                                .frame(width: 22, height: 22)
                                .background(LegendNextColor.navy, in: Circle())
                                .overlay { Circle().stroke(.white, lineWidth: 1.5) }
                        }
                        Text("Your story")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)
                    }
                    .frame(width: 72)
                }
                .buttonStyle(.plain)

                ForEach(stories) { story in
                    VStack(spacing: LegendNextSpacing.xs) {
                        LegendProfileAvatar(
                            avatar: story.author.avatar,
                            displayName: story.author.displayName,
                            size: 58)
                            .padding(3)
                            .overlay { Circle().stroke(LegendNextColor.gold, lineWidth: 2) }
                        Text(story.author.displayName)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)
                    }
                    .frame(width: 72)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Story from \(story.author.displayName)")
                }
            }
            .padding(.horizontal, 2)
        }
        .accessibilityLabel("Legend stories")
    }
}

private struct LegendSocialPostCard: View {
    let post: MobileSocialPost
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore
    let react: () -> Void
    let comment: () -> Void
    let follow: () -> Void

    var body: some View {
        LegendNextSurface {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                    LegendProfileAvatar(avatar: post.author.avatar, displayName: post.author.displayName, size: 42)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(post.author.displayName)
                            .font(.subheadline.weight(.bold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)
                        Text(metadata)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: LegendNextSpacing.xs)
                    if post.author.identity != currentIdentity {
                        Button(post.followedByCurrentActor ? "Following" : "Follow", action: follow)
                            .font(.caption.weight(.bold))
                            .foregroundStyle(post.followedByCurrentActor ? LegendNextColor.textSecondary : LegendNextColor.navy)
                            .padding(.horizontal, 9)
                            .padding(.vertical, 6)
                            .background(LegendNextColor.surfaceInset, in: Capsule())
                            .buttonStyle(.plain)
                    }
                }

                Text(post.body)
                    .font(LegendNextTypography.body)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)

                ForEach(post.media.filter(\.isImage)) { media in
                    LegendSocialMediaImage(
                        media: media,
                        social: social
                    )
                }

                HStack(spacing: LegendNextSpacing.md) {
                    Button(action: react) {
                        Label("\(post.reactionCount)", systemImage: post.reactedByCurrentActor ? "heart.fill" : "heart")
                            .foregroundStyle(post.reactedByCurrentActor ? LegendNextColor.danger : LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.reactedByCurrentActor ? "Remove appreciation" : "Appreciate this update")

                    Button(action: comment) {
                        Label("\(post.commentCount)", systemImage: "bubble.right")
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Comment on this update")

                    Spacer()

                    Text(post.postedUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                        .font(.caption2)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                if !post.comments.isEmpty {
                    Divider()
                    ForEach(post.comments.suffix(2)) { comment in
                        Text("\(comment.author.displayName): \(comment.body)")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(2)
                    }
                }
            }
        }
    }

    private var metadata: String {
        let kind = post.contentType == MobileSocialContentType.reel.rawValue ? "Reel" : post.contentType
        return "\(kind) · \(post.author.identity.participantType.rawValue)"
    }
}

private struct LegendSocialLoadingSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            LegendNextSectionHeader(title: "From your authorized Legend network")
            HStack(spacing: LegendNextSpacing.md) {
                ForEach(0 ..< 4, id: \.self) { _ in
                    Circle()
                        .fill(LegendNextColor.surfaceInset)
                        .frame(width: 58, height: 58)
                }
            }
            LegendNextSurface {
                HStack(spacing: LegendNextSpacing.sm) {
                    ProgressView()
                    Text("Loading your secure feed…")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }
            }
        }
        .redacted(reason: .placeholder)
    }
}

private struct LegendSocialEmptyFeed: View {
    let createPost: () -> Void

    var body: some View {
        LegendNextSurface {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                Label("Start the conversation", systemImage: "sparkles")
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Text("Share a focused update with the people already authorized in your Legend network.")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                Button("Create update", action: createPost)
                    .buttonStyle(LegendButtonStyle(kind: .primary))
            }
        }
    }
}

struct LegendSocialComposer: View {
    @Binding var type: MobileSocialContentType
    @Binding var messageBody: String
    let submit: (LegendSocialImageAttachment?) -> Void
    let cancel: () -> Void

    @State private var selectedPhoto: PhotosPickerItem?
    @State private var selectedImageData: Data?
    @State private var selectedImageMimeType = "image/jpeg"
    @State private var selectedImageFileName = "legend-image.jpg"
    @State private var imageDescription = ""
    @State private var imageSelectionError: String?
    @State private var isLoadingImage = false

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                Picker("Update type", selection: $type) {
                    ForEach(MobileSocialContentType.allCases) { option in
                        Text(option.displayName).tag(option)
                    }
                }
                .pickerStyle(.segmented)

                TextEditor(text: $messageBody)
                    .font(LegendNextTypography.body)
                    .padding(LegendNextSpacing.sm)
                    .frame(minHeight: 170)
                    .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
                    .accessibilityLabel("Legend update")

                PhotosPicker(
                    selection: $selectedPhoto,
                    matching: .images,
                    photoLibrary: .shared()
                ) {
                    Label(
                        selectedImageData == nil
                            ? "Add image"
                            : "Replace image",
                        systemImage: "photo.on.rectangle"
                    )
                }
                .buttonStyle(LegendButtonStyle(kind: .secondary))
                .accessibilityLabel("Add an image to your Legend update")

                if let selectedImageData,
                   let image = UIImage(data: selectedImageData) {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                        .frame(maxHeight: 180)
                        .clipShape(
                            RoundedRectangle(
                                cornerRadius: LegendNextRadius.control,
                                style: .continuous
                            )
                        )
                        .accessibilityLabel("Selected image preview")

                    TextField(
                        "Image description (optional)",
                        text: $imageDescription,
                        axis: .vertical
                    )
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Image description")
                }

                if isLoadingImage {
                    HStack(spacing: LegendNextSpacing.xs) {
                        ProgressView()
                        Text("Preparing image…")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                }

                if let imageSelectionError {
                    Text(imageSelectionError)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.danger)
                }

                Text("Images and updates are shared only with your server-authorized Legend network.")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)

                Spacer()
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("New Legend update")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: cancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Share") {
                        submit(selectedAttachment)
                    }
                    .disabled(!canShare)
                }
            }
        }
        .onChange(of: selectedPhoto) { _, photo in
            Task {
                await loadSelectedPhoto(photo)
            }
        }
    }

    private var canShare: Bool {
        !messageBody.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            || selectedImageData != nil
    }

    private var selectedAttachment: LegendSocialImageAttachment? {
        guard let selectedImageData else {
            return nil
        }

        let accessibilityText = imageDescription
            .trimmingCharacters(in: .whitespacesAndNewlines)

        return LegendSocialImageAttachment(
            file: MultipartFormFile(
                fieldName: "files",
                fileName: selectedImageFileName,
                mimeType: selectedImageMimeType,
                data: selectedImageData
            ),
            accessibilityText: accessibilityText.isEmpty
                ? nil
                : accessibilityText
        )
    }

    @MainActor
    private func loadSelectedPhoto(
        _ photo: PhotosPickerItem?
    ) async {
        imageSelectionError = nil
        selectedImageData = nil

        guard let photo else {
            return
        }

        isLoadingImage = true
        defer { isLoadingImage = false }

        do {
            guard let data = try await photo.loadTransferable(type: Data.self) else {
                imageSelectionError = "The selected image could not be read."
                return
            }

            let maximumImageBytes = 15 * 1024 * 1024
            guard data.count <= maximumImageBytes else {
                imageSelectionError = "Choose an image smaller than 15 MB."
                return
            }

            let imageType = photo.supportedContentTypes.first {
                $0.conforms(to: .image)
            }
            let representation = imageRepresentation(for: imageType)
            selectedImageMimeType = representation.mimeType
            selectedImageFileName = representation.fileName
            selectedImageData = data
        } catch {
            imageSelectionError = "The selected image could not be prepared."
        }
    }

    private func imageRepresentation(
        for type: UTType?
    ) -> (mimeType: String, fileName: String) {
        if type?.conforms(to: .png) == true {
            return ("image/png", "legend-image.png")
        }

        if type?.conforms(to: .heic) == true {
            return ("image/heic", "legend-image.heic")
        }

        if type?.conforms(to: .heif) == true {
            return ("image/heif", "legend-image.heif")
        }

        if type?.conforms(to: .webP) == true {
            return ("image/webp", "legend-image.webp")
        }

        return ("image/jpeg", "legend-image.jpg")
    }
}

private struct LegendSocialMediaImage: View {
    let media: MobileSocialMedia
    @ObservedObject var social: MobileSocialStore
    @State private var image: UIImage?

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFit()
            } else {
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .fill(LegendNextColor.surfaceInset)
                .frame(height: 180)
                .overlay {
                    ProgressView()
                }
            }
        }
        .clipShape(
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .task(id: media.id) {
            guard let data = await social.mediaData(for: media.id) else {
                return
            }

            image = UIImage(data: data)
        }
        .accessibilityLabel(media.accessibilityText ?? "Shared image")
    }
}

private struct LegendCommentComposer: View {
    let authorName: String
    @Binding var messageBody: String
    let submit: () -> Void
    let cancel: () -> Void

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                Text("Reply to \(authorName)")
                    .font(LegendNextTypography.section)
                TextEditor(text: $messageBody)
                    .font(LegendNextTypography.body)
                    .padding(LegendNextSpacing.sm)
                    .frame(minHeight: 130)
                    .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
                    .accessibilityLabel("Comment")
                Spacer()
            }
            .padding(LegendNextSpacing.md)
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Comment")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel", action: cancel) }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Send", action: submit)
                        .disabled(messageBody.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
    }
}

private struct LegendActivitySheet: View {
    let activity: [MobileSocialActivity]

    var body: some View {
        NavigationStack {
            Group {
                if activity.isEmpty {
                    LegendEmptyState(
                        title: "No activity yet",
                        message: "Appreciations, comments, and follows on your Legend updates appear here.",
                        symbolName: "heart")
                } else {
                    List(activity) { item in
                        HStack(spacing: LegendNextSpacing.sm) {
                            LegendProfileAvatar(avatar: item.actor.avatar, displayName: item.actor.displayName, size: 38)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.actor.displayName)
                                    .font(.subheadline.weight(.semibold))
                                Text(item.summary)
                                    .font(LegendNextTypography.supporting)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                            }
                            Spacer()
                            Text(item.occurredUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                                .font(.caption2)
                                .foregroundStyle(LegendNextColor.textSecondary)
                        }
                    }
                    .listStyle(.plain)
                }
            }
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Activity")
            .navigationBarTitleDisplayMode(.inline)
        }
    }

}
