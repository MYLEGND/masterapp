import AVKit
import Combine
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
    let refreshSocial: () async -> Void
    let dashboardContent: DashboardContent

    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var isPresentingActivity = false
    @State private var isPresentingCreatorInsights = false
    @State private var commentTarget: MobileSocialPost?
    @State private var commentBody = ""
    @State private var postInsight: MobileSocialPostInsight?
    @State private var storyCollection: MobileSocialStoryCollection?

    init(
        session: MobileSession,
        home: MobileHomeResponse,
        social: MobileSocialStore,
        openMessages: @escaping () -> Void,
        openCircles: @escaping () -> Void,
        refreshSocial: @escaping () async -> Void,
        @ViewBuilder dashboardContent: () -> DashboardContent
    ) {
        self.session = session
        self.home = home
        _social = ObservedObject(wrappedValue: social)
        self.openMessages = openMessages
        self.openCircles = openCircles
        self.refreshSocial = refreshSocial
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
        .fullScreenCover(item: $storyCollection, onDismiss: {
            Task { await refreshSocial() }
        }) { collection in
            LegendStoryViewer(
                collection: collection,
                currentIdentity: session.actor.identity,
                social: social)
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
                collections: MobileSocialStoryCollection.grouped(
                    from: snapshot.stories
                ),
                createStory: {
                    creationRoute = .composer(.story)
                },
                selectStory: { collection in
                    storyCollection = collection
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

            if let publication = social.publication {
                LegendSocialPublicationBanner(
                    publication: publication,
                    retry: social.retryPublication,
                    dismiss: social.dismissPublication)
            }

            if snapshot.posts.isEmpty {
                LegendSocialEmptyFeed {
                    creationRoute = .menu
                }
            } else {
                ForEach(snapshot.posts) { post in
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
    let collections: [MobileSocialStoryCollection]
    let createStory: () -> Void
    let selectStory: (MobileSocialStoryCollection) -> Void

    private var currentActorCollection: MobileSocialStoryCollection? {
        collections.first { $0.author.identity == currentActor.identity }
    }

    private var otherCollections: [MobileSocialStoryCollection] {
        collections.filter { $0.author.identity != currentActor.identity }
    }

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: LegendNextSpacing.md) {
                currentStoryControl

                ForEach(otherCollections) { collection in
                    LegendStoryCircle(
                        collection: collection,
                        title: collection.author.displayName,
                        action: { selectStory(collection) })
                }
            }
            .padding(.horizontal, 2)
        }
        .accessibilityLabel("Legend stories")
    }

    @ViewBuilder
    private var currentStoryControl: some View {
        if let currentActorCollection {
            LegendStoryCircle(
                collection: currentActorCollection,
                title: "Your story",
                action: { selectStory(currentActorCollection) })
        } else {
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
            .accessibilityLabel("Create your story")
        }
    }
}

private struct LegendStoryCircle: View {
    let collection: MobileSocialStoryCollection
    let title: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: LegendNextSpacing.xs) {
                LegendProfileAvatar(
                    avatar: collection.author.avatar,
                    displayName: collection.author.displayName,
                    size: 58)
                    .padding(3)
                    .overlay { Circle().stroke(LegendNextColor.gold, lineWidth: 2) }
                Text(title)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(1)
            }
            .frame(width: 72)
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Open \(title), \(collection.items.count) story \(collection.items.count == 1 ? "item" : "items")")
    }
}

private struct LegendStoryViewer: View {
    let collection: MobileSocialStoryCollection
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore

    @Environment(\.dismiss) private var dismiss
    @State private var itemIndex = 0
    @State private var progress = 0.0
    @State private var storyDurationSeconds = 5.0
    @State private var isPaused = false
    @State private var hasRecordedDeparture = false
    @State private var replyBody = ""

    private let imageProgressTimer = Timer.publish(
        every: 0.05,
        on: .main,
        in: .common
    ).autoconnect()

    private var item: MobileSocialPost {
        collection.items[itemIndex]
    }

    private var isOwner: Bool {
        collection.author.identity == currentIdentity
    }

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                Color.black.ignoresSafeArea()

                LegendStoryMedia(
                    story: item,
                    social: social,
                    isPaused: isPaused,
                    playbackProgress: { current, duration in
                        storyDurationSeconds = max(duration, 0.1)
                        progress = min(max(current / storyDurationSeconds, 0), 1)
                    },
                    playbackFinished: {
                        moveForward(manual: false)
                    })
                    .frame(
                        width: geometry.size.width,
                        height: geometry.size.height)
                    .background(Color.black)

                HStack(spacing: 0) {
                    Color.clear
                        .contentShape(Rectangle())
                        .onTapGesture { moveBackward() }
                    Color.clear
                        .contentShape(Rectangle())
                        .onTapGesture { moveForward(manual: true) }
                }

                VStack(spacing: LegendNextSpacing.sm) {
                    progressBars
                    header
                    Spacer()
                    footer
                }
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.lg)
            }
            .contentShape(Rectangle())
            .simultaneousGesture(
                DragGesture(minimumDistance: 30)
                    .onEnded { value in
                        if value.translation.height > 70 {
                            closeStory(recordExit: true)
                        }
                    })
            .onLongPressGesture(
                minimumDuration: 0.12,
                maximumDistance: 12,
                pressing: { isPaused = $0 },
                perform: {})
        }
        .onAppear {
            recordInitialView()
        }
        .onChange(of: itemIndex) {
            progress = 0
            storyDurationSeconds = 5
            isPaused = false
            recordInitialView()
        }
        .onReceive(imageProgressTimer) { _ in
            guard !isPaused, !item.media.contains(where: \.isVideo) else { return }
            progress += 0.05 / storyDurationSeconds
            if progress >= 1 {
                moveForward(manual: false)
            }
        }
        .onDisappear {
            if !hasRecordedDeparture {
                recordCurrent(interaction: "Exit")
            }
        }
        .interactiveDismissDisabled(false)
        .accessibilityLabel("Story viewer for \(collection.author.displayName)")
    }

    private var progressBars: some View {
        HStack(spacing: 4) {
            ForEach(collection.items.indices, id: \.self) { index in
                GeometryReader { proxy in
                    Capsule()
                        .fill(.white.opacity(0.32))
                        .overlay(alignment: .leading) {
                            Capsule()
                                .fill(.white)
                                .frame(width: proxy.size.width * progressValue(for: index))
                        }
                }
                .frame(height: 3)
            }
        }
        .accessibilityLabel("Story \(itemIndex + 1) of \(collection.items.count)")
    }

    private var header: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            LegendProfileAvatar(
                avatar: collection.author.avatar,
                displayName: collection.author.displayName,
                size: 38)
            VStack(alignment: .leading, spacing: 2) {
                Text(isOwner ? "Your story" : collection.author.displayName)
                    .font(.subheadline.weight(.bold))
                Text(item.postedUTC, style: .relative)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.76))
            }
            .foregroundStyle(.white)

            Spacer()

            if isOwner {
                Label("\(item.metrics.uniqueViewerCount)", systemImage: "eye")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.white)
                    .accessibilityLabel("\(item.metrics.uniqueViewerCount) unique viewers")
            }

            Button {
                closeStory(recordExit: true)
            } label: {
                Image(systemName: "xmark")
                    .font(.subheadline.weight(.bold))
                    .frame(width: 36, height: 36)
                    .background(.black.opacity(0.42), in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.white)
            .accessibilityLabel("Close story")
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            if !item.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Text(item.body)
                    .font(.body.weight(.medium))
                    .foregroundStyle(.white)
                    .lineLimit(4)
                    .shadow(color: .black.opacity(0.45), radius: 4)
            }

            HStack(spacing: LegendNextSpacing.sm) {
                TextField("Reply to \(collection.author.displayName)", text: $replyBody)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.vertical, 11)
                    .background(.black.opacity(0.46), in: Capsule())
                    .foregroundStyle(.white)
                    .accessibilityLabel("Reply to story")

                Button(action: submitReply) {
                    Image(systemName: "paperplane.fill")
                        .frame(width: 40, height: 40)
                        .background(.white.opacity(0.18), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .disabled(replyBody.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                .accessibilityLabel("Send story reply")

                Button {
                    social.toggleReaction(postID: item.id)
                } label: {
                    Image(systemName: item.reactedByCurrentActor ? "heart.fill" : "heart")
                        .frame(width: 40, height: 40)
                        .background(.black.opacity(0.46), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(item.reactedByCurrentActor ? LegendNextColor.danger : .white)
                .accessibilityLabel(item.reactedByCurrentActor ? "Remove appreciation" : "Appreciate story")

                ShareLink(item: "Legend story: \(item.body)") {
                    Image(systemName: "square.and.arrow.up")
                        .frame(width: 40, height: 40)
                        .background(.black.opacity(0.46), in: Circle())
                }
                .simultaneousGesture(TapGesture().onEnded {
                    social.recordShare(postID: item.id)
                })
                .foregroundStyle(.white)
                .accessibilityLabel("Share story")
            }
        }
    }

    private func progressValue(for index: Int) -> CGFloat {
        if index < itemIndex { return 1 }
        if index > itemIndex { return 0 }
        return CGFloat(progress)
    }

    private func moveForward(manual: Bool) {
        recordCurrent(interaction: manual ? "TapForward" : nil, completed: true)
        guard itemIndex < collection.items.count - 1 else {
            closeStory(recordExit: false)
            return
        }
        itemIndex += 1
    }

    private func moveBackward() {
        guard itemIndex > 0 else {
            progress = 0
            return
        }
        recordCurrent(interaction: "TapBackward")
        itemIndex -= 1
    }

    private func submitReply() {
        let body = replyBody.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty else { return }
        social.addComment(postID: item.id, body: body)
        replyBody = ""
    }

    private func recordInitialView() {
        social.recordView(postID: item.id)
    }

    private func recordCurrent(
        interaction: String?,
        completed: Bool = false
    ) {
        social.recordView(
            postID: item.id,
            watchDurationSeconds: Decimal(completed ? storyDurationSeconds : progress * storyDurationSeconds),
            watchCompletionPercentage: Decimal(completed ? 100 : progress * 100),
            storyInteractionType: interaction)
    }

    private func closeStory(recordExit: Bool) {
        hasRecordedDeparture = true
        if recordExit {
            recordCurrent(interaction: "Exit")
        }
        dismiss()
    }
}

private struct LegendStoryMedia: View {
    let story: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    let isPaused: Bool
    let playbackProgress: (Double, Double) -> Void
    let playbackFinished: () -> Void

    @State private var player: AVPlayer?
    @State private var progressObserver: Any?

    var body: some View {
        Group {
            if let video = story.media.first(where: \.isVideo) {
                videoPresentation(video)
            } else if let image = story.media.first(where: \.isImage) {
                LegendSocialMediaImage(
                    media: image,
                    social: social,
                    contentMode: .fit,
                    placeholderHeight: 520)
            } else {
                Color.black.overlay {
                    Text(story.body)
                        .font(.title2.weight(.semibold))
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                        .padding(LegendNextSpacing.xl)
                }
            }
        }
        .onChange(of: isPaused) {
            if isPaused {
                player?.pause()
            } else {
                player?.play()
            }
        }
        .onDisappear {
            removeProgressObserver()
            player?.pause()
        }
    }

    @ViewBuilder
    private func videoPresentation(_ media: MobileSocialMedia) -> some View {
        if let player {
            VideoPlayer(player: player)
                .aspectRatio(contentMode: .fit)
                .onReceive(
                    NotificationCenter.default.publisher(
                        for: .AVPlayerItemDidPlayToEndTime)
                ) { notification in
                    guard let endedItem = notification.object as? AVPlayerItem,
                          endedItem == player.currentItem else { return }
                    playbackFinished()
                }
        } else {
            Color.black.overlay { ProgressView().tint(.white) }
                .task(id: media.id) {
                    guard let fileURL = await social.mediaFile(for: media) else { return }
                    let createdPlayer = AVPlayer(url: fileURL)
                    createdPlayer.isMuted = false
                    player = createdPlayer
                    installProgressObserver(for: createdPlayer)
                    if !isPaused {
                        createdPlayer.play()
                    }
                }
        }
    }

    private func installProgressObserver(for player: AVPlayer) {
        removeProgressObserver()
        progressObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.05, preferredTimescale: 600),
            queue: .main
        ) { [weak player] current in
            guard let player,
                  let item = player.currentItem else { return }
            let duration = item.duration.seconds
            guard duration.isFinite, duration > 0 else { return }
            playbackProgress(current.seconds, duration)
        }
    }

    private func removeProgressObserver() {
        guard let progressObserver else { return }
        player?.removeTimeObserver(progressObserver)
        self.progressObserver = nil
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

private struct LegendSocialPublicationBanner: View {
    let publication: MobileSocialPublication
    let retry: () -> Void
    let dismiss: () -> Void

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: publication.stage.systemImage)
                .font(.body.weight(.semibold))
                .foregroundStyle(accent)

            VStack(alignment: .leading, spacing: 2) {
                Text(publication.stage.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                Text(detail)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(2)
            }

            Spacer(minLength: LegendNextSpacing.xs)

            if publication.stage == .failed {
                Button("Retry", action: retry)
                    .font(.caption.weight(.bold))
                    .buttonStyle(.borderedProminent)
                    .tint(LegendNextColor.navy)
            } else if publication.stage == .published {
                Button(action: dismiss) {
                    Image(systemName: "xmark")
                        .font(.caption.weight(.bold))
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.textSecondary)
                .accessibilityLabel("Dismiss upload confirmation")
            } else {
                ProgressView()
                    .tint(accent)
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Publication status: \(publication.stage.title). \(detail)")
    }

    private var detail: String {
        switch publication.stage {
        case .preparing:
            "Your update will appear here after the secure upload begins."
        case .uploading:
            "You can keep browsing while this update is securely transferred."
        case .processing:
            "Legend is confirming your update with the server."
        case .published:
            "Your update is now in the authorized Legend feed."
        case .failed:
            publication.failureMessage ?? "Tap Retry to try this secure upload again."
        }
    }

    private var accent: Color {
        switch publication.stage {
        case .failed:
            LegendNextColor.danger
        case .published:
            LegendNextColor.success
        default:
            LegendNextColor.gold
        }
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
