import AVKit
import SwiftUI
import UIKit

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

    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var isPresentingActivity = false
    @State private var isPresentingCreatorInsights = false
    @State private var commentTarget: MobileSocialPost?
    @State private var commentBody = ""
    @State private var postInsight: MobileSocialPostInsight?

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
        .sheet(item: $creationRoute) { _ in
            LegendSocialCreationSheet(
                route: $creationRoute,
                social: social)
        }
        .sheet(isPresented: $isPresentingActivity) {
            LegendActivitySheet(activity: activity)
        }
        .sheet(isPresented: $isPresentingCreatorInsights) {
            if case .loaded(let snapshot) = social.state {
                LegendCreatorInsightsSheet(
                    insights: snapshot.creatorInsights,
                    profileMetrics: snapshot.currentProfileMetrics)
            }
        }
        .sheet(item: $postInsight) { insight in
            LegendPostInsightsSheet(insight: insight)
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
                creationRoute = .menu
            } label: {
                Image(systemName: "plus")
                    .font(.title3.weight(.semibold))
                    .frame(width: 42, height: 42)
                    .background(LegendNextColor.surfaceElevated, in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendNextColor.textPrimary)
            .accessibilityLabel("Create a Legend update")

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
                .foregroundStyle(LegendNextColor.navy)
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
                    creationRoute = .menu
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
                    creationRoute = .menu
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
                            social.toggleFollow(author: post.author, sourcePostID: post.id)
                        },
                        insights: {
                            Task {
                                postInsight = await social.postInsights(postID: post.id)
                            }
                        }
                    )
                }

                Button {
                    isPresentingCreatorInsights = true
                } label: {
                    Label("Creator insights", systemImage: "chart.line.uptrend.xyaxis")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendButtonStyle(kind: .secondary))
                .accessibilityLabel("Open your server-authoritative creator insights")
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
    let insights: () -> Void

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
                    } else {
                        Button(action: insights) {
                            Image(systemName: "chart.bar.xaxis")
                                .font(.caption.weight(.bold))
                                .frame(width: 30, height: 30)
                                .background(LegendNextColor.surfaceInset, in: Circle())
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(LegendNextColor.information)
                        .accessibilityLabel("View post insights")
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

                ForEach(post.media.filter(\.isVideo)) { media in
                    LegendSocialMediaVideo(
                        postID: post.id,
                        media: media,
                        music: post.music,
                        social: social)
                }

                if let music = post.music {
                    Label(
                        "\(music.trackTitle) · \(music.artistName)",
                        systemImage: "music.note")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(1)
                    .accessibilityLabel("Music: \(music.trackTitle) by \(music.artistName)")
                }

                HStack(spacing: LegendNextSpacing.sm) {
                    Button(action: react) {
                        Label("\(post.metrics.reactionCount)", systemImage: post.reactedByCurrentActor ? "heart.fill" : "heart")
                            .foregroundStyle(post.reactedByCurrentActor ? LegendNextColor.danger : LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.reactedByCurrentActor ? "Remove appreciation" : "Appreciate this update")

                    Button(action: comment) {
                        Label("\(post.metrics.commentCount)", systemImage: "bubble.right")
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Comment on this update")

                    Button {
                        social.toggleSave(postID: post.id)
                    } label: {
                        Label("\(post.metrics.saveCount)", systemImage: post.savedByCurrentActor ? "bookmark.fill" : "bookmark")
                            .foregroundStyle(post.savedByCurrentActor ? LegendNextColor.gold : LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.savedByCurrentActor ? "Remove saved update" : "Save this update")

                    Button {
                        social.toggleRepost(postID: post.id)
                    } label: {
                        Label("\(post.metrics.repostCount)", systemImage: post.repostedByCurrentActor ? "arrow.2.squarepath" : "arrow.2.squarepath")
                            .foregroundStyle(post.repostedByCurrentActor ? LegendNextColor.information : LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.repostedByCurrentActor ? "Remove repost" : "Repost this update")

                    ShareLink(item: post.body) {
                        Label("\(post.metrics.shareCount)", systemImage: "square.and.arrow.up")
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    .simultaneousGesture(TapGesture().onEnded {
                        social.recordShare(postID: post.id)
                    })
                    .accessibilityLabel("Share this update")

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
        .task(id: post.id) {
            guard !post.media.contains(where: \.isVideo) else { return }
            social.recordView(postID: post.id)
        }
    }

    private var metadata: String {
        let kind = post.contentType == MobileSocialContentType.reel.rawValue ? "Reel" : post.contentType
        return "\(kind) · \(post.author.identity.participantType.rawValue)"
    }
}

struct LegendSocialMediaVideo: View {
    let postID: UUID
    let media: MobileSocialMedia
    let music: MobileSocialMusic?
    @ObservedObject var social: MobileSocialStore

    @State private var player: AVPlayer?
    @State private var isPlaying = false
    @State private var isMuted = false
    @State private var mostRecentRecordedWatchSeconds = 0.0

    var body: some View {
        VStack(spacing: 0) {
            ZStack(alignment: .bottomTrailing) {
                Group {
                    if let player {
                        VideoPlayer(player: player)
                    } else {
                        RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous)
                        .fill(LegendNextColor.surfaceInset)
                        .overlay { ProgressView() }
                    }
                }
                .frame(minHeight: 220)

                HStack(spacing: LegendNextSpacing.xs) {
                    Button {
                        togglePlayback()
                    } label: {
                        Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                            .frame(width: 36, height: 36)
                    }
                    .accessibilityLabel(isPlaying ? "Pause video" : "Play video")

                    Button {
                        isMuted.toggle()
                        player?.isMuted = isMuted
                    } label: {
                        Image(systemName: isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            .frame(width: 36, height: 36)
                    }
                    .accessibilityLabel(isMuted ? "Unmute original video audio" : "Mute original video audio")
                }
                .font(.subheadline.weight(.bold))
                .foregroundStyle(.white)
                .background(.black.opacity(0.48), in: Capsule())
                .padding(LegendNextSpacing.sm)
            }

            if let music {
                Label("Music: \(music.trackTitle) · \(music.artistName)", systemImage: "music.note")
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.top, LegendNextSpacing.xs)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
        .task(id: media.id) {
            guard let url = await social.mediaFile(for: media) else { return }
            let created = AVPlayer(url: url)
            created.isMuted = isMuted
            player = created
            created.play()
            isPlaying = true
        }
        .onReceive(NotificationCenter.default.publisher(for: .AVPlayerItemDidPlayToEndTime)) { notification in
            guard let item = notification.object as? AVPlayerItem,
                  item === player?.currentItem else { return }
            recordPlaybackMetrics(completed: true)
            player?.seek(to: .zero) { _ in
                player?.play()
            }
            isPlaying = true
        }
        .onDisappear {
            recordPlaybackMetrics(completed: false)
            player?.pause()
            isPlaying = false
        }
        .accessibilityLabel(media.accessibilityText ?? "Shared video")
    }

    private func togglePlayback() {
        guard let player else { return }
        if isPlaying {
            recordPlaybackMetrics(completed: false)
            player.pause()
        } else {
            player.play()
        }
        isPlaying.toggle()
    }

    private func recordPlaybackMetrics(completed: Bool) {
        guard let player else { return }

        let watchSeconds = max(0, player.currentTime().seconds)
        let duration = player.currentItem?.duration.seconds
        let completion: Double?
        if completed {
            completion = 100
        } else if let duration, duration.isFinite, duration > 0 {
            completion = min(100, max(0, (watchSeconds / duration) * 100))
        } else {
            completion = nil
        }

        guard completed || watchSeconds > mostRecentRecordedWatchSeconds else { return }

        social.recordView(
            postID: postID,
            watchDurationSeconds: Decimal(watchSeconds),
            watchCompletionPercentage: completion.map { Decimal($0) })
        mostRecentRecordedWatchSeconds = max(mostRecentRecordedWatchSeconds, watchSeconds)
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

struct LegendSocialMediaImage: View {
    let media: MobileSocialMedia
    @ObservedObject var social: MobileSocialStore
    let contentMode: ContentMode
    let placeholderHeight: CGFloat?
    @State private var state = ImageState.loading

    init(
        media: MobileSocialMedia,
        social: MobileSocialStore,
        contentMode: ContentMode = .fit,
        placeholderHeight: CGFloat? = 180
    ) {
        self.media = media
        _social = ObservedObject(wrappedValue: social)
        self.contentMode = contentMode
        self.placeholderHeight = placeholderHeight
    }

    var body: some View {
        Group {
            switch state {
            case .loaded(let image):
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            case .loading:
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .fill(LegendNextColor.surfaceInset)
                .frame(height: placeholderHeight)
                .overlay {
                    ProgressView()
                }
            case .unavailable:
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .fill(LegendNextColor.surfaceInset)
                .frame(height: placeholderHeight)
                .overlay {
                    Button {
                        Task { await loadMedia(forceRefresh: true) }
                    } label: {
                        Label("Image unavailable", systemImage: "photo.badge.exclamationmark")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .padding(.horizontal, LegendNextSpacing.sm)
                            .padding(.vertical, LegendNextSpacing.xs)
                            .background(LegendNextColor.surfaceElevated, in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Retries the secure image download")
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
            await loadMedia()
        }
        .accessibilityLabel(media.accessibilityText ?? "Shared image")
    }

    private func loadMedia(forceRefresh: Bool = false) async {
        state = .loading

        guard let data = await social.mediaData(
            for: media.id,
            forceRefresh: forceRefresh),
              let image = UIImage(data: data) else {
            state = .unavailable
            return
        }

        state = .loaded(image)
    }

    private enum ImageState {
        case loading
        case loaded(UIImage)
        case unavailable
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

private struct LegendCreatorInsightsSheet: View {
    let insights: MobileSocialCreatorInsights
    let profileMetrics: MobileSocialProfileMetrics

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                    LegendNextSectionHeader(
                        eyebrow: "Creator intelligence",
                        title: "Your Legend impact")

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text("Reach and engagement are generated from protected Legend activity.")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)

                            LazyVGrid(
                                columns: [GridItem(.flexible()), GridItem(.flexible())],
                                spacing: LegendNextSpacing.sm
                            ) {
                                LegendSocialInsightMetric(label: "Views", value: "\(insights.totalViews)", symbol: "play.rectangle.fill", color: LegendNextColor.information)
                                LegendSocialInsightMetric(label: "Reach", value: "\(insights.totalReach)", symbol: "person.2.fill", color: LegendNextColor.success)
                                LegendSocialInsightMetric(label: "Followers", value: "\(insights.followerCount)", symbol: "person.badge.plus", color: LegendNextColor.gold)
                                LegendSocialInsightMetric(label: "Engagement", value: socialPercentage(insights.engagementRatePercentage), symbol: "chart.line.uptrend.xyaxis", color: LegendNextColor.navy)
                            }
                        }
                    }

                    LegendNextSurface {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            LegendNextSectionHeader(eyebrow: "Profile", title: "Content and community")
                            LegendSocialInsightRow(label: "Posts", value: "\(profileMetrics.postCount)")
                            LegendSocialInsightRow(label: "Videos", value: "\(profileMetrics.videoCount)")
                            LegendSocialInsightRow(label: "Stories", value: "\(profileMetrics.storyCount)")
                            LegendSocialInsightRow(label: "Following", value: "\(profileMetrics.followingCount)")
                            LegendSocialInsightRow(label: "Profile visits", value: "\(insights.profileVisits)")
                            LegendSocialInsightRow(label: "Followers gained this week", value: "\(insights.followersGained)")
                        }
                    }

                    LegendSocialInsightList(
                        title: "Top posts",
                        emptyMessage: "Publish a post to begin building performance history.",
                        insights: insights.topPosts)
                    LegendSocialInsightList(
                        title: "Top videos",
                        emptyMessage: "Publish a video to begin building video performance history.",
                        insights: insights.topVideos)
                    LegendSocialInsightList(
                        title: "Top stories",
                        emptyMessage: "Publish a story to begin building story performance history.",
                        insights: insights.topStories)
                }
                .padding(LegendNextSpacing.md)
            }
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Creator insights")
            .navigationBarTitleDisplayMode(.inline)
        }
        .accessibilityLabel("Creator insights generated from your Legend activity")
    }
}

private struct LegendPostInsightsSheet: View {
    let insight: MobileSocialPostInsight

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                    LegendNextSectionHeader(
                        eyebrow: insight.contentType,
                        title: "Post insights")

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text("Published \(insight.postedUTC, style: .date)")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                            Text("\(insight.metrics.uniqueViewerCount) reached · \(socialPercentage(insight.engagementRatePercentage)) engagement")
                                .font(LegendNextTypography.bodyEmphasis)
                                .foregroundStyle(LegendNextColor.textPrimary)
                        }
                    }

                    LegendNextSurface {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            LegendSocialInsightRow(label: "Views", value: "\(insight.metrics.viewCount)")
                            LegendSocialInsightRow(label: "Unique viewers", value: "\(insight.metrics.uniqueViewerCount)")
                            LegendSocialInsightRow(label: "Appreciations", value: "\(insight.metrics.reactionCount)")
                            LegendSocialInsightRow(label: "Comments", value: "\(insight.metrics.commentCount)")
                            LegendSocialInsightRow(label: "Replies", value: "\(insight.metrics.replyCount)")
                            LegendSocialInsightRow(label: "Reposts", value: "\(insight.metrics.repostCount)")
                            LegendSocialInsightRow(label: "Saves", value: "\(insight.metrics.saveCount)")
                            LegendSocialInsightRow(label: "Shares", value: "\(insight.metrics.shareCount)")
                            LegendSocialInsightRow(label: "Profile visits", value: "\(insight.metrics.profileVisitCount)")
                            LegendSocialInsightRow(label: "Follows generated", value: "\(insight.metrics.followsGenerated)")

                            if let averageWatchDuration = insight.metrics.averageWatchDurationSeconds {
                                LegendSocialInsightRow(label: "Average watch time", value: "\(socialNumber(averageWatchDuration)) sec")
                            }
                            if let averageWatchCompletion = insight.metrics.averageWatchCompletionPercentage {
                                LegendSocialInsightRow(label: "Average completion", value: socialPercentage(averageWatchCompletion))
                            }
                            if insight.contentType == MobileSocialContentType.story.rawValue {
                                LegendSocialInsightRow(label: "Story exits", value: "\(insight.metrics.storyExitCount)")
                                LegendSocialInsightRow(label: "Taps forward", value: "\(insight.metrics.storyTapForwardCount)")
                                LegendSocialInsightRow(label: "Taps backward", value: "\(insight.metrics.storyTapBackwardCount)")
                            }
                        }
                    }
                }
                .padding(LegendNextSpacing.md)
            }
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Post insights")
            .navigationBarTitleDisplayMode(.inline)
        }
        .accessibilityLabel("Private server-authoritative insights for this post")
    }
}

private struct LegendSocialInsightMetric: View {
    let label: String
    let value: String
    let symbol: String
    let color: Color

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            Image(systemName: symbol)
                .font(.caption.weight(.bold))
                .foregroundStyle(color)
            Text(value)
                .font(.title3.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)
                .lineLimit(1)
            Text(label)
                .font(LegendNextTypography.caption)
                .foregroundStyle(LegendNextColor.textSecondary)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(LegendNextSpacing.sm)
        .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
        .accessibilityElement(children: .combine)
    }
}

private struct LegendSocialInsightRow: View {
    let label: String
    let value: String

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label)
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.textSecondary)
            Spacer()
            Text(value)
                .font(LegendNextTypography.bodyEmphasis)
                .foregroundStyle(LegendNextColor.textPrimary)
                .multilineTextAlignment(.trailing)
        }
    }
}

private struct LegendSocialInsightList: View {
    let title: String
    let emptyMessage: String
    let insights: [MobileSocialPostInsight]

    var body: some View {
        LegendNextSurface {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                Text(title)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)

                if insights.isEmpty {
                    Text(emptyMessage)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                } else {
                    ForEach(insights) { insight in
                        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                            Text(insight.postedUTC, format: .dateTime.month(.abbreviated).day().year())
                                .font(LegendNextTypography.bodyEmphasis)
                                .foregroundStyle(LegendNextColor.textPrimary)
                            Text("\(insight.metrics.uniqueViewerCount) reached · \(insight.metrics.reactionCount) appreciations · \(socialPercentage(insight.engagementRatePercentage)) engagement")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
        }
    }
}

private func socialNumber(_ value: Decimal) -> String {
    NSDecimalNumber(decimal: value).doubleValue.formatted(
        .number.precision(.fractionLength(1)))
}

private func socialPercentage(_ value: Decimal) -> String {
    "\(socialNumber(value))%"
}
