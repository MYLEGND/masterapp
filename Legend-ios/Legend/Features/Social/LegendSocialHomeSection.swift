import AVKit
import Combine
import SwiftUI
import UIKit

/// The native home is intentionally a composition over the protected mobile
/// projections. It never derives a role, feed audience, finance state, or
/// profile identity in the client.
struct LegendSocialHomeSection<DashboardContent: View>: View {
    @EnvironmentObject private var scrollChrome: LegendScrollChrome

    let session: MobileSession
    let home: MobileHomeResponse
    @ObservedObject var social: MobileSocialStore
    let openMessages: () -> Void
    let openCircles: () -> Void
    let refreshSocial: () async -> Void
    let dashboardContent: DashboardContent

    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var isPresentingActivity = false
    @State private var activitySeenThroughUTC: Date?
    private let activitySeenKey: String
    @State private var commentTarget: MobileSocialPost?
    @State private var postInsight: MobileSocialPostInsight?
    @State private var storyCollection: MobileSocialStoryCollection?
    @State private var selectedPost: MobileSocialPost?
    @State private var publicProfile: LegendPublicProfileRoute?

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

        let activitySeenKey =
            "legend.social.activity.seen." +
            session.actor.identity.participantType.rawValue +
            "." +
            session.actor.identity.userID

        self.activitySeenKey = activitySeenKey
        _activitySeenThroughUTC = State(
            initialValue: UserDefaults.standard.object(
                forKey: activitySeenKey
            ) as? Date
        )

        self.home = home
        _social = ObservedObject(wrappedValue: social)
        self.openMessages = openMessages
        self.openCircles = openCircles
        self.refreshSocial = refreshSocial
        self.dashboardContent = dashboardContent()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
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
        .sheet(item: $postInsight) { insight in
            LegendPostInsightsSheet(insight: insight)
        }
        .sheet(item: $publicProfile) { route in
            NavigationStack {
                LegendPublicProfileView(
                    profile: route.profile,
                    currentIdentity: session.actor.identity,
                    social: social,
                    isFollowing: route.isFollowing,
                    isFollowRequestPending: route.isFollowRequestPending)
            }
        }
.sheet(item: $commentTarget) { post in
            LegendCommentComposer(
                postID: post.id,
                social: social,
                cancel: { commentTarget = nil }
            )
        }
        .fullScreenCover(item: $storyCollection, onDismiss: {
            Task { await refreshSocial() }
        }) { collection in
            LegendStoryViewer(
                collection: collection,
                currentIdentity: session.actor.identity,
                social: social)
        }
        .navigationDestination(
            isPresented: Binding(
                get: { selectedPost != nil },
                set: { if !$0 { selectedPost = nil } }
            )
        ) {
            if let selectedPost {
                LegendPostDetailView(
                    post: selectedPost,
                    currentIdentity: session.actor.identity,
                    social: social)
            }
        }
        .onAppear {
            handleHomeChromeAction(scrollChrome.pendingHomeAction)
        }
        .onChange(of: scrollChrome.pendingHomeAction) { _, request in
            handleHomeChromeAction(request)
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

    private func handleHomeChromeAction(
        _ request: LegendHomeChromeActionRequest?
    ) {
        guard let request else { return }
        defer { scrollChrome.completeHomeAction(request) }

        switch request.kind {
        case .create:
            creationRoute = .menu
        case .activity:
            openActivity()
        }
    }

    private func openActivity() {
        if let latestActivityUTC = activity.map(\.occurredUTC).max() {
            activitySeenThroughUTC = latestActivityUTC
            UserDefaults.standard.set(
                latestActivityUTC,
                forKey: activitySeenKey
            )
        }

        isPresentingActivity = true
    }

    private var failurePresentation: Binding<Bool> {
        Binding(
            get: { social.actionFailure != nil },
            set: { if !$0 { social.dismissActionFailure() } })
    }

    @ViewBuilder
    private var storyContent: some View {
        if case .loaded(let snapshot) = social.state {
            LegendNextSurface(style: .plain, padding: LegendNextSpacing.sm) {
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
    }

    @ViewBuilder
    private var socialFeed: some View {
        switch social.state {
        case .idle, .loading:
            LegendSocialLoadingSection()

        case .unavailable(let failure):
            LegendNextErrorState(
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
                    .padding(.vertical, LegendNextSpacing.tiny)
                }
                .buttonStyle(LegendNextSurfaceButtonStyle())
                .accessibilityLabel(
                    "Open Journey Circles. \(journey.connectedPeerCount) connected profiles"
                )
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
                        },
                        presentation: .preview,
                        open: {
                            selectedPost = post
                        },
                        openProfile: {
                            publicProfile = LegendPublicProfileRoute(
                                profile: post.author,
                                isFollowing: post.followedByCurrentActor,
                                isFollowRequestPending: post.followRequestPending ?? false)
                        }
                    )
                }
            }
        }
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
                        showsVerifiedBadge: collection.author.isVerified == true,
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
                showsVerifiedBadge: false,
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
    let showsVerifiedBadge: Bool
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
                LegendVerifiedName(
                    title,
                    isVerified: showsVerifiedBadge,
                    font: .caption.weight(.semibold))
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
                LegendNextColor.midnight.ignoresSafeArea()

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
                    .id(item.id)
                    .frame(
                        width: geometry.size.width,
                        height: geometry.size.height)
                    .background(LegendNextColor.midnight)

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
                if isOwner {
                    Text("Your story")
                        .font(.subheadline.weight(.bold))
                } else {
                    LegendVerifiedName(
                        collection.author.displayName,
                        isVerified: collection.author.isVerified == true,
                        font: .subheadline.weight(.bold),
                        textColor: .white)
                }
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
                    .background(LegendNextColor.midnight.opacity(0.42), in: Circle())
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
                    .shadow(color: LegendNextColor.midnight.opacity(0.45), radius: 4)
            }

            HStack(spacing: LegendNextSpacing.sm) {
                TextField("Reply to \(collection.author.displayName)", text: $replyBody)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.vertical, 11)
                    .background(LegendNextColor.midnight.opacity(0.46), in: Capsule())
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
                        .background(LegendNextColor.midnight.opacity(0.46), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(item.reactedByCurrentActor ? LegendNextColor.danger : .white)
                .accessibilityLabel(item.reactedByCurrentActor ? "Remove appreciation" : "Appreciate story")

                ShareLink(item: "Legend story: \(item.body)") {
                    Image(systemName: "square.and.arrow.up")
                        .frame(width: 40, height: 40)
                        .background(LegendNextColor.midnight.opacity(0.46), in: Circle())
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
                    placeholderHeight: 520,
                    usesRoundedCorners: false)
            } else {
                LegendNextColor.midnight.overlay {
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
            LegendNextColor.midnight.overlay { LegendSkeletonShape(cornerRadius: 0).opacity(0.35) }
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

private enum LegendSocialPostPresentation {
    case preview
    case detail
    case immersive

    var includesSaveAction: Bool {
        self != .preview
    }

    var opensForYou: Bool {
        self == .preview
    }
}

/// Keeps participant-role labels inside the agent's CRM context only. Public
/// and client social surfaces identify the post type, never the author's
/// account type.
enum LegendSocialPostMetadata {
    static func summary(
        contentType: String,
        authorParticipantType: ParticipantType,
        viewerParticipantType: ParticipantType
    ) -> String {
        let kind = MobileSocialContentType.displayName(for: contentType)
        guard viewerParticipantType == .agent else { return kind }
        return "\(kind) · \(authorParticipantType.rawValue)"
    }
}

/// The only destination for a selected published update. It renders the exact
/// post that was tapped and deliberately never delegates ordinary posts to the
/// vertical HACS feed, which is reserved for video discovery.
struct LegendPostDetailView: View {
    let post: MobileSocialPost
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore

    @Environment(\.dismiss) private var dismiss
    @State private var commentTarget: MobileSocialPost?
    @State private var postInsight: MobileSocialPostInsight?
    @State private var publicProfile: LegendPublicProfileRoute?
    @State private var editingPost: MobileSocialPost?
    @State private var deletionTarget: MobileSocialPost?

    var body: some View {
        LegendScrollView {
            LegendSocialPostCard(
                post: post,
                currentIdentity: currentIdentity,
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
                },
                presentation: .detail,
                open: {},
                openProfile: {
                    publicProfile = LegendPublicProfileRoute(
                        profile: post.author,
                        isFollowing: post.followedByCurrentActor,
                        isFollowRequestPending: post.followRequestPending ?? false)
                }
            )
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.vertical, LegendNextSpacing.md)
        }
        .background(LegendNextCanvas())
        .navigationTitle(post.displayContentType)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if post.author.identity == currentIdentity {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button {
                            editingPost = post
                        } label: {
                            Label("Edit \(post.displayContentType)", systemImage: "pencil")
                        }

                        Button(role: .destructive) {
                            deletionTarget = post
                        } label: {
                            Label("Delete \(post.displayContentType)", systemImage: "trash")
                        }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                            .font(.body.weight(.semibold))
                    }
                    .accessibilityLabel("\(post.displayContentType) options")
                }
            }
        }
        .sheet(item: $commentTarget) { target in
            LegendCommentComposer(
                postID: target.id,
                social: social,
                cancel: { commentTarget = nil }
            )
        }
        .sheet(item: $postInsight) { insight in
            LegendPostInsightsSheet(insight: insight)
        }
        .sheet(item: $publicProfile) { route in
            NavigationStack {
                LegendPublicProfileView(
                    profile: route.profile,
                    currentIdentity: currentIdentity,
                    social: social,
                    isFollowing: route.isFollowing,
                    isFollowRequestPending: route.isFollowRequestPending)
            }
        }
        .sheet(item: $editingPost) { editablePost in
            LegendSocialPostEditor(
                post: editablePost,
                social: social,
                onSaved: { editingPost = nil }
            )
        }
        .confirmationDialog(
            "Delete this \(deletionTargetDisplayName)?",
            isPresented: Binding(
                get: { deletionTarget != nil },
                set: { if !$0 { deletionTarget = nil } }
            ),
            titleVisibility: .visible
        ) {
            if let deletionTarget {
                Button("Delete \(deletionTarget.displayContentType)", role: .destructive) {
                    Task {
                        guard await social.deletePost(postID: deletionTarget.id) else { return }
                        self.deletionTarget = nil
                        dismiss()
                    }
                }
            }

            Button("Cancel", role: .cancel) {
                deletionTarget = nil
            }
        } message: {
            Text("This removes the \(deletionTargetDisplayName) from your Legend profile and feed.")
        }
        .alert(
            social.actionFailure?.title ?? "Legend update unavailable",
            isPresented: Binding(
                get: { social.actionFailure != nil },
                set: { if !$0 { social.dismissActionFailure() } }
            ),
            actions: {
                Button("OK", role: .cancel) {
                    social.dismissActionFailure()
                }
            },
            message: {
                Text(social.actionFailure?.message ?? "The request could not be completed.")
            }
        )
    }

    private var deletionTargetDisplayName: String {
        deletionTarget?.displayContentType ?? post.displayContentType
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
    let presentation: LegendSocialPostPresentation
    let open: () -> Void
    let openProfile: () -> Void

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.sm)

            mediaPresentation
                .padding(.horizontal, LegendNextSpacing.xs)
                .clipShape(
                    RoundedRectangle(
                        cornerRadius: LegendNextRadius.control,
                        style: .continuous
                    )
                )

            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                actionBar
                metadataContent
            }
            .padding(.horizontal, LegendNextSpacing.md)
            .padding(.vertical, LegendNextSpacing.sm)
        }
        .background {
            LinearGradient(
                colors: [
                    LegendNextColor.surface,
                    LegendNextColor.surfaceElevated.opacity(0.82)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
        }
        .clipShape(
            RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous)
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous)
            .strokeBorder(
                LegendNextColor.premiumBorder(for: colorScheme),
                lineWidth: 1
            )
        }
        .shadow(
            color: LegendNextColor.elevatedShadow(for: colorScheme),
            radius: LegendNextElevation.cardRadius - 4,
            y: LegendNextElevation.cardY - 3
        )
        .padding(.horizontal, presentation == .immersive ? LegendNextSpacing.sm : 0)
        .padding(.vertical, LegendNextSpacing.micro)
        .task(id: post.id) {
            guard !post.media.contains(where: \.isVideo) else { return }
            social.recordView(postID: post.id)
        }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
            Button(action: openProfile) {
                HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                    LegendProfileAvatar(
                        avatar: post.author.avatar,
                        displayName: post.author.displayName,
                        size: 42)

                    VStack(alignment: .leading, spacing: 2) {
                        LegendVerifiedName(
                            post.author.displayName,
                            isVerified: post.author.isVerified == true,
                            font: .subheadline.weight(.bold)
                        )
                        Text(metadata)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(1)
                    }
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Open \(post.author.displayName)'s profile")

            Spacer(minLength: LegendNextSpacing.xs)

            if post.author.identity != currentIdentity {
                Button(post.followedByCurrentActor ? "Following" : "Follow", action: follow)
                    .font(.caption.weight(.bold))
                    .foregroundStyle(post.followedByCurrentActor ? LegendNextColor.textSecondary : .white)
                    .padding(.horizontal, 11)
                    .padding(.vertical, 7)
                    .background(
                        post.followedByCurrentActor
                            ? LegendNextColor.surfaceInset
                            : LegendNextColor.navy,
                        in: Capsule()
                    )
                    .overlay {
                        Capsule().strokeBorder(
                            post.followedByCurrentActor
                                ? LegendNextColor.premiumBorder(for: colorScheme)
                                : Color.white.opacity(0.12),
                            lineWidth: 1
                        )
                    }
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
                .accessibilityLabel("View \(post.displayContentType) insights")
            }
        }
    }

    @ViewBuilder
    private var mediaPresentation: some View {
        if !post.media.isEmpty {
            TabView {
                ForEach(post.media, id: \.id) { (media: MobileSocialMedia) in
                    Group {
                        if media.isImage {
                            LegendSocialMediaImage(
                                media: media,
                                social: social,
                                contentMode: .fill,
                                placeholderHeight: nil,
                                usesRoundedCorners: false)
                        } else if media.isVideo {
                            LegendSocialMediaVideo(
                                postID: post.id,
                                media: media,
                                music: post.music,
                                social: social,
                                usesRoundedCorners: false)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .onTapGesture(count: 2, perform: react)
                    .onTapGesture {
                        guard presentation.opensForYou else { return }
                        open()
                    }
                    .accessibilityHint("Double tap to appreciate this update")
                }
            }
            .tabViewStyle(.page(indexDisplayMode: post.media.count > 1 ? .automatic : .never))
            .aspectRatio(mediaAspectRatio, contentMode: .fit)
            .clipped()
        }
    }

    private var actionBar: some View {
        HStack(spacing: 2) {
            Button(action: react) {
                actionMetricLabel(
                    symbolName: post.reactedByCurrentActor
                        ? "heart.fill"
                        : "heart",
                    count: post.metrics.reactionCount,
                    isActive: post.reactedByCurrentActor,
                    activeColor: LegendNextColor.danger
                )
            }
            .buttonStyle(.plain)
            .accessibilityLabel(
                post.reactedByCurrentActor
                    ? "Remove appreciation"
                    : "Appreciate this update"
            )

            Button(action: comment) {
                actionMetricLabel(
                    symbolName: "bubble.left",
                    count: post.metrics.commentCount
                )
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Comment on this update")

            if presentation.includesSaveAction {
                Button {
                    social.toggleSave(postID: post.id)
                } label: {
                    actionMetricLabel(
                        symbolName: post.savedByCurrentActor
                            ? "bookmark.fill"
                            : "bookmark",
                        count: post.metrics.saveCount,
                        isActive: post.savedByCurrentActor,
                        activeColor: LegendNextColor.gold
                    )
                }
                .buttonStyle(.plain)
                .accessibilityLabel(
                    post.savedByCurrentActor
                        ? "Remove saved update"
                        : "Save this update"
                )
            }

            Button {
                social.toggleRepost(postID: post.id)
            } label: {
                actionMetricLabel(
                    symbolName: "arrow.2.squarepath",
                    count: post.metrics.repostCount,
                    isActive: post.repostedByCurrentActor,
                    activeColor: LegendNextColor.information
                )
            }
            .buttonStyle(.plain)
            .accessibilityLabel(
                post.repostedByCurrentActor
                    ? "Remove repost"
                    : "Repost this update"
            )

            ShareLink(
                item: post.body.isEmpty
                    ? "Legend \(post.displayContentType) by \(post.author.displayName)"
                    : post.body
            ) {
                actionMetricLabel(
                    symbolName: "paperplane",
                    count: post.metrics.shareCount
                )
            }
            .simultaneousGesture(
                TapGesture().onEnded {
                    social.recordShare(postID: post.id)
                }
            )
            .accessibilityLabel("Share this update")

            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 2)
    }

    private func actionMetricLabel(
        symbolName: String,
        count: Int,
        isActive: Bool = false,
        activeColor: Color = LegendNextColor.gold
    ) -> some View {
        HStack(spacing: 5) {
            Image(systemName: symbolName)
                .font(
                    .system(
                        size: 18,
                        weight: isActive ? .semibold : .medium
                    )
                )
                .symbolRenderingMode(.monochrome)
                .frame(width: 21, height: 21)

            if count > 0 {
                Text("\(count)")
                    .font(
                        .system(
                            size: 13,
                            weight: .semibold,
                            design: .rounded
                        )
                    )
                    .monospacedDigit()
                    .contentTransition(.numericText())
            }
        }
        .foregroundStyle(
            isActive
                ? activeColor
                : LegendNextColor.textPrimary
        )
        .frame(minHeight: 36)
        .padding(.horizontal, count > 0 ? 5 : 3)
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private var metadataContent: some View {
        if !post.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                LegendVerifiedName(
                    post.author.displayName,
                    isVerified: post.author.isVerified == true,
                    font: LegendNextTypography.supporting.weight(.bold))
                Text(post.body)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textPrimary)
            }
                .fixedSize(horizontal: false, vertical: true)
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

        if !post.comments.isEmpty {
            ForEach(post.comments.suffix(2)) { comment in
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    LegendVerifiedName(
                        comment.author.displayName,
                        isVerified: comment.author.isVerified == true,
                        font: LegendNextTypography.supporting.weight(.bold))
                    Text(comment.body)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
            }
        }

        Text(post.postedUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
            .font(.caption2)
            .foregroundStyle(LegendNextColor.textSecondary)
    }

    private var mediaAspectRatio: CGFloat {
        if post.contentType == MobileSocialContentType.story.rawValue ||
            post.contentType == MobileSocialContentType.hac.rawValue {
            return 9 / 16
        }

        guard let aspectRatio = post.media.first?.aspectRatio else { return 1 }
        let value = CGFloat(truncating: NSDecimalNumber(decimal: aspectRatio))
        return min(max(value, 0.5), 2)
    }

    private var metadata: String {
        LegendSocialPostMetadata.summary(
            contentType: post.contentType,
            authorParticipantType: post.author.identity.participantType,
            viewerParticipantType: currentIdentity.participantType)
    }
}

struct LegendForYouView: View {
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore
    let initialPostID: UUID?
    let presentsDismissControl: Bool

    @Environment(\.dismiss) private var dismiss
    @State private var selectedPostID: UUID?
    @State private var commentTarget: MobileSocialPost?
    @State private var postInsight: MobileSocialPostInsight?
    @State private var editingPost: MobileSocialPost?
    @State private var deletionTarget: MobileSocialPost?
    @State private var publicProfile: LegendPublicProfileRoute?

    init(
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        initialPostID: UUID? = nil,
        presentsDismissControl: Bool = false
    ) {
        self.currentIdentity = currentIdentity
        _social = ObservedObject(wrappedValue: social)
        self.initialPostID = initialPostID
        self.presentsDismissControl = presentsDismissControl
        _selectedPostID = State(initialValue: initialPostID)
    }

    var body: some View {
        Group {
            switch social.state {
            case .idle, .loading:
                LegendScreenSkeleton(accessibilityMessage: "Loading your For You feed") {
                    LegendFeedPostSkeleton()
                }

            case .unavailable(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: { social.load() }
                )
                .padding(LegendNextSpacing.sm)

            case .loaded(let snapshot):
                feed(snapshot.hacs.filter(\.isVideoHac))
            }
        }
        .background(LegendNextCanvas())
        .navigationTitle("For You")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if presentsDismissControl {
                ToolbarItem(placement: .cancellationAction) {
                    Button {
                        dismiss()
                    } label: {
                        Image(systemName: "chevron.backward")
                    }
                    .accessibilityLabel("Back to home feed")
                }
            }

            if let selectedPost,
               selectedPost.author.identity == currentIdentity {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button {
                            editingPost = selectedPost
                        } label: {
                            Label("Edit \(selectedPost.displayContentType)", systemImage: "pencil")
                        }

                        Button(role: .destructive) {
                            deletionTarget = selectedPost
                        } label: {
                            Label("Delete \(selectedPost.displayContentType)", systemImage: "trash")
                        }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                            .font(.body.weight(.semibold))
                    }
                    .accessibilityLabel("\(selectedPost.displayContentType) options")
                }
            }
        }
.sheet(item: $commentTarget) { post in
            LegendCommentComposer(
                postID: post.id,
                social: social,
                cancel: { commentTarget = nil }
            )
        }
        .sheet(item: $postInsight) { insight in
            LegendPostInsightsSheet(insight: insight)
        }
        .sheet(item: $publicProfile) { route in
            NavigationStack {
                LegendPublicProfileView(
                    profile: route.profile,
                    currentIdentity: currentIdentity,
                    social: social,
                    isFollowing: route.isFollowing,
                    isFollowRequestPending: route.isFollowRequestPending)
            }
        }
        .sheet(item: $editingPost) { post in
            LegendSocialPostEditor(
                post: post,
                social: social,
                onSaved: { editingPost = nil }
            )
        }
        .confirmationDialog(
            "Delete this \(deletionTargetDisplayName)?",
            isPresented: Binding(
                get: { deletionTarget != nil },
                set: { if !$0 { deletionTarget = nil } }
            ),
            titleVisibility: .visible
        ) {
            if let post = deletionTarget {
                Button("Delete \(post.displayContentType)", role: .destructive) {
                    Task {
                        if await social.deletePost(postID: post.id) {
                            deletionTarget = nil
                        }
                    }
                }
            }

            Button("Cancel", role: .cancel) {
                deletionTarget = nil
            }
        } message: {
            Text("This removes the \(deletionTargetDisplayName) from your Legend profile and feed.")
        }
        .alert(
            social.actionFailure?.title ?? "Legend update unavailable",
            isPresented: Binding(
                get: { social.actionFailure != nil },
                set: { if !$0 { social.dismissActionFailure() } }
            ),
            actions: {
                Button("OK", role: .cancel) {
                    social.dismissActionFailure()
                }
            },
            message: {
                Text(social.actionFailure?.message ?? "The request could not be completed.")
            }
        )
    }

    @ViewBuilder
    private func feed(_ posts: [MobileSocialPost]) -> some View {
        if posts.isEmpty {
            LegendNextEmptyState(
                title: "No updates yet",
                message: "Video Hacs matched to your Legend activity will appear here.",
                systemImage: "play.rectangle.on.rectangle"
            )
        } else {
            TabView(selection: $selectedPostID) {
                ForEach(posts) { post in
                    LegendScrollView {
                        LegendSocialPostCard(
                            post: post,
                            currentIdentity: currentIdentity,
                            social: social,
                            react: {
                                social.toggleReaction(postID: post.id)
                            },
                            comment: {
                                commentTarget = post
                            },
                            follow: {
                                social.toggleFollow(
                                    author: post.author,
                                    sourcePostID: post.id
                                )
                            },
                            insights: {
                                Task {
                                    postInsight = await social.postInsights(postID: post.id)
                                }
                            },
                            presentation: .immersive,
                            open: {},
                            openProfile: {
                                publicProfile = LegendPublicProfileRoute(
                                    profile: post.author,
                                    isFollowing: post.followedByCurrentActor,
                                    isFollowRequestPending: post.followRequestPending ?? false)
                            }
                        )
                        .padding(.bottom, LegendNextSpacing.xl)
                    }
                    .scrollIndicators(.hidden)
                    .tag(Optional(post.id))
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .onAppear {
                selectInitialPost(from: posts)
            }
            .onChange(of: posts.map(\.id)) {
                selectInitialPost(from: posts)
            }
        }
    }

    private func selectInitialPost(from posts: [MobileSocialPost]) {
        guard posts.contains(where: { $0.id == selectedPostID }) else {
            selectedPostID = initialPostID.flatMap { requestedID in
                posts.contains(where: { $0.id == requestedID }) ? requestedID : nil
            } ?? posts.first?.id
            return
        }
    }

    private var selectedPost: MobileSocialPost? {
        guard case .loaded(let snapshot) = social.state else { return nil }
        return snapshot.hacs.first { $0.id == selectedPostID }
    }

    private var deletionTargetDisplayName: String {
        deletionTarget?.displayContentType ?? "Post"
    }

}

struct LegendSocialPostEditor: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    let onSaved: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var caption: String
    @State private var isSaving = false

    init(
        post: MobileSocialPost,
        social: MobileSocialStore,
        onSaved: @escaping () -> Void
    ) {
        self.post = post
        _social = ObservedObject(wrappedValue: social)
        self.onSaved = onSaved
        _caption = State(initialValue: post.body)
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                TextEditor(text: $caption)
                    .font(LegendNextTypography.body)
                    .padding(LegendNextSpacing.sm)
                    .frame(minHeight: 180)
                    .background(
                        LegendNextColor.surfaceInset,
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous
                        )
                    )
                    .accessibilityLabel("\(post.displayContentType) caption")

                if post.media.isEmpty {
                    Text("A text post needs a caption.")
                        .font(.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer()
            }
            .padding(LegendNextSpacing.sm)
            .background(LegendNextColor.canvas)
            .navigationTitle("Edit \(post.displayContentType)")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        dismiss()
                    }
                }

                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") {
                        isSaving = true
                        Task {
                            if await social.updatePost(postID: post.id, body: caption) {
                                dismiss()
                                onSaved()
                            }
                            isSaving = false
                        }
                    }
                    .disabled(
                        isSaving ||
                        (post.media.isEmpty && caption.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        ).isEmpty)
                    )
                }
            }
        }
        .legendNextSheetChrome()
    }
}

struct LegendSocialMediaVideo: View {
    let postID: UUID
    let media: MobileSocialMedia
    let music: MobileSocialMusic?
    @ObservedObject var social: MobileSocialStore
    let usesRoundedCorners: Bool

    @State private var player: AVPlayer?
    @State private var isPlaying = false
    @State private var isMuted = true
    @State private var mostRecentRecordedWatchSeconds = 0.0

    init(
        postID: UUID,
        media: MobileSocialMedia,
        music: MobileSocialMusic?,
        social: MobileSocialStore,
        usesRoundedCorners: Bool = true
    ) {
        self.postID = postID
        self.media = media
        self.music = music
        _social = ObservedObject(wrappedValue: social)
        self.usesRoundedCorners = usesRoundedCorners
    }

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
                        .overlay { LegendSkeletonShape(cornerRadius: LegendNextRadius.control) }
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
                .background(LegendNextColor.midnight.opacity(0.48), in: Capsule())
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
        .clipShape(
            RoundedRectangle(
                cornerRadius: usesRoundedCorners
                    ? LegendNextRadius.control
                    : 0,
                style: .continuous))
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
            LegendNextSectionHeader(title: "From your Legend network")
            HStack(spacing: LegendNextSpacing.md) {
                ForEach(0 ..< 4, id: \.self) { _ in
                    Circle()
                        .fill(LegendNextColor.surfaceInset)
                        .frame(width: 58, height: 58)
                }
            }
            LegendNextSurface {
                HStack(spacing: LegendNextSpacing.sm) {
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
                Text("Share a focused update with people in your Legend network.")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                Button("Create update", action: createPost)
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
            }
        }
    }
}

struct LegendSocialMediaImage: View {
    let media: MobileSocialMedia
    @ObservedObject var social: MobileSocialStore
    let contentMode: ContentMode
    let placeholderHeight: CGFloat?
    let usesRoundedCorners: Bool
    @State private var state = ImageState.loading

    init(
        media: MobileSocialMedia,
        social: MobileSocialStore,
        contentMode: ContentMode = .fit,
        placeholderHeight: CGFloat? = 180,
        usesRoundedCorners: Bool = true
    ) {
        self.media = media
        _social = ObservedObject(wrappedValue: social)
        self.contentMode = contentMode
        self.placeholderHeight = placeholderHeight
        self.usesRoundedCorners = usesRoundedCorners
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
                    LegendSkeletonShape(cornerRadius: LegendNextRadius.control)
                }
            case .unavailable:
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .fill(LegendNextColor.surfaceInset)
                .frame(height: placeholderHeight)
                .overlay {
                    VStack(spacing: LegendNextSpacing.xs) {
                        Label(
                            mediaFailure.message,
                            systemImage: "photo.badge.exclamationmark")
                            .font(.caption.weight(.semibold))
                            .multilineTextAlignment(.center)
                            .foregroundStyle(LegendNextColor.textSecondary)

                        Button("Retry media") {
                            Task { await loadMedia(forceRefresh: true) }
                        }
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.navy)
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .padding(.vertical, LegendNextSpacing.xs)
                        .background(LegendNextColor.surfaceElevated, in: Capsule())
                    }
                    .padding(LegendNextSpacing.sm)
                }
            }
        }
        .clipShape(
            RoundedRectangle(
                cornerRadius: usesRoundedCorners
                    ? LegendNextRadius.control
                    : 0,
                style: .continuous))
        .task(id: media.id) {
            await loadMedia()
        }
        .accessibilityLabel(media.accessibilityText ?? "Shared image")
    }

    private func loadMedia(forceRefresh: Bool = false) async {
        state = .loading

        guard let data = await social.mediaData(
            for: media.id,
            forceRefresh: forceRefresh) else {
            state = .unavailable
            return
        }

        guard let image = UIImage(data: data) else {
            social.recordUnreadableImage(assetID: media.id)
            state = .unavailable
            return
        }

        state = .loaded(image)
    }

    private var mediaFailure: UserFacingFailure {
        social.mediaFailure(for: media.id) ?? UserFacingFailure(
            title: "Media unavailable",
            message: "This protected image could not be displayed. Try again shortly.",
            correlationID: nil)
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
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                Image(systemName: publication.stage.systemImage)
                    .font(.body.weight(.semibold))
                    .foregroundStyle(accent)
                    .frame(width: 34, height: 34)
                    .background(accent.opacity(0.12), in: Circle())

                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(detail)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }

                Spacer(minLength: LegendNextSpacing.xs)

                controls
            }

            progressLine
        }
        .padding(LegendNextSpacing.md)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous)
            .stroke(accent.opacity(0.34), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Publication status: \(title). \(detail)")
    }

    @ViewBuilder
    private var controls: some View {
        if publication.stage == .failed {
            // Discard must be reachable: a parked failure used to hold the upload
            // slot and disable the composer with no way to clear it.
            HStack(spacing: LegendNextSpacing.xs) {
                Button("Discard", action: dismiss)
                    .font(.caption.weight(.semibold))
                    .buttonStyle(.bordered)
                    .tint(LegendNextColor.textSecondary)
                    .accessibilityLabel("Discard this failed upload")

                Button("Retry", action: retry)
                    .font(.caption.weight(.bold))
                    .buttonStyle(.borderedProminent)
                    .tint(LegendNextColor.navy)
            }
        } else if publication.stage == .published {
            Button(action: dismiss) {
                Image(systemName: "xmark")
                    .font(.caption.weight(.bold))
                    .frame(width: 32, height: 32)
                    .background(
                        LegendNextColor.surfaceInset,
                        in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendNextColor.textSecondary)
            .accessibilityLabel("Dismiss upload confirmation")
        }
    }

    private var progressLine: some View {
        GeometryReader { proxy in
            Capsule()
                .fill(LegendNextColor.surfaceInset)
                .overlay(alignment: .leading) {
                    Capsule()
                        .fill(accent)
                        .frame(
                            width: proxy.size.width * CGFloat(publication.uploadProgress))
                }
        }
        .frame(height: 5)
        .accessibilityLabel("Upload progress \(Int((publication.uploadProgress * 100).rounded())) percent")
    }

    private var title: String {
        let content = publication.contentType.displayName
        switch publication.stage {
        case .preparing: return "Preparing \(content)"
        case .uploading: return "Uploading \(content)"
        case .processing: return "Publishing \(content)"
        case .published: return "\(content) shared"
        case .failed: return "\(content) needs attention"
        }
    }

    private var detail: String {
        switch publication.stage {
        case .preparing:
            "Your secure upload is about to begin."
        case .uploading:
            "\(Int((publication.uploadProgress * 100).rounded()))% securely transferred. You can keep browsing."
        case .processing:
            "Transfer complete. Legend is validating and publishing it."
        case .published:
            "Your \(publication.contentType.displayName.lowercased()) is now in the authorized Legend feed."
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
    let postID: UUID
    @ObservedObject var social: MobileSocialStore
    let cancel: () -> Void

    @FocusState private var composerFocused: Bool
    @State private var draft = ""
    @State private var replyTarget: MobileSocialComment?
    @State private var selectedDetent: PresentationDetent = .medium

    private var post: MobileSocialPost? {
        guard case .loaded(let snapshot) = social.state else {
            return nil
        }

        return (snapshot.posts + snapshot.stories + snapshot.hacs).first {
            $0.id == postID
        }
    }

    private var comments: [MobileSocialComment] {
        post?.comments ?? []
    }

    private var rootComments: [MobileSocialComment] {
        comments
            .filter { $0.parentCommentID == nil }
            .sorted { $0.createdUTC < $1.createdUTC }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                LegendNextSheetHeader(
                    eyebrow: "Legend network",
                    title: "Conversation",
                    detail: post.map { "Discussing \($0.author.displayName)'s update" },
                    dismiss: cancel
                )
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.top, LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.md)

                if let post {
                    LegendNextInsetSurface {
                        postPreview(post)
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.bottom, LegendNextSpacing.sm)

                    commentsSection
                } else {
                    LegendListSkeleton(rows: 4)
                        .padding(LegendNextSpacing.sm)
                        .accessibilityLabel("Loading comments")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }

                composer
            }
            .background(
                LegendNextCanvas()
            )
            .toolbar(.hidden, for: .navigationBar)
        }
        .presentationDetents(
            [.medium, .large],
            selection: $selectedDetent
        )
        .presentationDragIndicator(.visible)
        .presentationCornerRadius(LegendNextRadius.sheet)
        .legendNextBrandedSheetAppearance()
    }

    private func postPreview(
        _ post: MobileSocialPost
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            HStack(spacing: LegendNextSpacing.sm) {
                LegendProfileAvatar(
                    avatar: post.author.avatar,
                    displayName: post.author.displayName,
                    size: 34
                )

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    LegendVerifiedName(
                        post.author.displayName,
                        isVerified: post.author.isVerified == true,
                        font: .subheadline.weight(.bold)
                    )

                    Text(post.postedUTC, style: .relative)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Label(
                    "\(comments.count)",
                    systemImage: "bubble.left"
                )
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)
                .padding(.horizontal, 9)
                .padding(.vertical, 5)
                .background(
                    LegendNextColor.surfaceInset,
                    in: Capsule()
                )
            }

            let caption = post.body.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

            if !caption.isEmpty {
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    LegendVerifiedName(
                        post.author.displayName,
                        isVerified: post.author.isVerified == true,
                        font: LegendNextTypography.supporting.weight(.bold))
                    Text(caption)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textPrimary)
                }
                .lineLimit(selectedDetent == .large ? 3 : 2)
                .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    @ViewBuilder
    private var commentsSection: some View {
        if rootComments.isEmpty {
            VStack(spacing: LegendNextSpacing.xs) {
                Image(systemName: "bubble.left")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(LegendNextColor.gold)

                Text("Start the conversation")
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text("Be the first to leave a comment.")
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(LegendNextSpacing.md)
        } else {
            ScrollViewReader { proxy in
                LegendScrollView(tracksNavigationChrome: false) {
                    LazyVStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.sm
                    ) {
                        ForEach(rootComments) { comment in
                            commentThread(comment)
                                .id(comment.id)
                        }
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.vertical, LegendNextSpacing.sm)
                }
                .scrollDismissesKeyboard(.interactively)
                .scrollIndicators(.hidden)
                .onChange(of: comments.map(\.id)) {
                    guard let newest = comments.last else {
                        return
                    }

                    withAnimation(LegendNextMotion.tab) {
                        proxy.scrollTo(
                            newest.parentCommentID ?? newest.id,
                            anchor: .bottom
                        )
                    }
                }
            }
        }
    }

    private func commentThread(
        _ comment: MobileSocialComment
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            commentRow(comment, isReply: false)

            let replies = comments
                .filter {
                    $0.parentCommentID == comment.id
                }
                .sorted {
                    $0.createdUTC < $1.createdUTC
                }

            if !replies.isEmpty {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    ForEach(replies) { reply in
                        commentRow(reply, isReply: true)
                    }
                }
                .padding(.leading, 38)
            }
        }
    }

    private func commentRow(
        _ comment: MobileSocialComment,
        isReply: Bool
    ) -> some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.xs
        ) {
            LegendProfileAvatar(
                avatar: comment.author.avatar,
                displayName: comment.author.displayName,
                size: isReply ? 27 : 33
            )

            VStack(
                alignment: .leading,
                spacing: 3
            ) {
                HStack(
                    alignment: .firstTextBaseline,
                    spacing: LegendNextSpacing.xs
                ) {
                    LegendVerifiedName(
                        comment.author.displayName,
                        isVerified: comment.author.isVerified == true,
                        font: .caption.weight(.bold)
                    )

                    Text(comment.createdUTC, style: .relative)
                        .font(.caption2)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Text(comment.body)
                    .font(
                        isReply
                            ? LegendNextTypography.supporting
                            : LegendNextTypography.body
                    )
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)

                Button {
                    replyTarget = comment
                    composerFocused = true
                } label: {
                    Text("Reply")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textSecondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(
                    "Reply to \(comment.author.displayName)"
                )
            }

            Spacer(minLength: 0)
        }
    }

    private var composer: some View {
        VStack(spacing: 0) {
            Rectangle()
                .fill(LegendNextColor.separator)
                .frame(height: 0.5)

            if let replyTarget {
                HStack(spacing: LegendNextSpacing.xs) {
                    Image(systemName: "arrowshape.turn.up.left")
                        .font(.caption.weight(.semibold))

                    Text(
                        "Replying to \(replyTarget.author.displayName)"
                    )
                    .font(LegendNextTypography.caption)
                    .lineLimit(1)

                    Spacer(minLength: LegendNextSpacing.xs)

                    Button {
                        self.replyTarget = nil
                    } label: {
                        Image(systemName: "xmark")
                            .font(.caption.weight(.bold))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Cancel reply")
                }
                .foregroundStyle(LegendNextColor.textSecondary)
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, 6)
                .background(LegendNextColor.surfaceInset)
            }

            HStack(
                alignment: .bottom,
                spacing: LegendNextSpacing.xs
            ) {
                TextField(
                    replyTarget == nil
                        ? "Add a comment…"
                        : "Reply to \(replyTarget?.author.displayName ?? "")…",
                    text: $draft
                )
                .focused($composerFocused)
                .font(LegendNextTypography.body)
                .foregroundStyle(LegendNextColor.textPrimary)
                .lineLimit(1)
                .textInputAutocapitalization(.sentences)
                .submitLabel(.send)
                .onSubmit(send)
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, 9)
                .background(
                    LegendNextColor.surfaceInset,
                    in: RoundedRectangle(
                        cornerRadius: 19,
                        style: .continuous
                    )
                )

                if !composerFocused {
                    Button(action: send) {
                        Image(systemName: "arrow.up")
                            .font(.subheadline.weight(.bold))
                            .foregroundStyle(
                                canSend
                                    ? LegendNextColor.navy
                                    : LegendNextColor.textSecondary.opacity(0.4)
                            )
                            .frame(width: 38, height: 38)
                            .background(
                                canSend
                                    ? LegendNextColor.goldBright
                                    : LegendNextColor.surfaceInset,
                                in: Circle()
                            )
                    }
                    .buttonStyle(.plain)
                    .disabled(!canSend)
                    .accessibilityLabel("Send comment")
                }
            }
            .padding(.horizontal, LegendNextSpacing.md)
            .padding(.vertical, LegendNextSpacing.xs)
            .background(LegendNextColor.surfaceElevated)
        }
    }

    private var canSend: Bool {
        !draft
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            .isEmpty
    }

    private func send() {
        let body = draft.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        guard !body.isEmpty else {
            return
        }

        let parentCommentID =
            replyTarget?.parentCommentID
            ?? replyTarget?.id

        draft = ""
        replyTarget = nil

        social.addComment(
            postID: postID,
            body: body,
            parentCommentID: parentCommentID
        )

        composerFocused = true
    }
}

private struct LegendActivitySheet: View {
    let activity: [MobileSocialActivity]
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Legend network",
                        title: "Activity",
                        detail: "Recent interactions across your Legend updates.",
                        dismiss: { dismiss() }
                    )

                if activity.isEmpty {
                    LegendNextEmptyState(
                        title: "No activity yet",
                        message: "Appreciations, comments, and follows on your Legend updates appear here.",
                        systemImage: "heart")
                } else {
                    ForEach(activity) { item in
                        LegendContactCard(
                            displayName: item.actor.displayName,
                            subtitle: item.actor.identity.participantType == .agent
                                ? item.actor.roleLabel
                                : item.summary,
                            detail: item.actor.identity.participantType == .agent
                                ? item.summary
                                : nil,
                            isVerified: item.actor.isVerified == true,
                            avatar: {
                                LegendProfileAvatar(
                                    avatar: item.actor.avatar,
                                    displayName: item.actor.displayName,
                                    size: 42)
                            },
                            action: {
                                Text(item.occurredUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                                    .font(.caption2)
                                    .foregroundStyle(LegendNextColor.contactSupporting)
                            }
                        )
                    }
                }
            }
            .padding(LegendNextSpacing.md)
            .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome()
    }

}

struct LegendCreatorInsightsSheet: View {
    let insights: MobileSocialCreatorInsights
    let profileMetrics: MobileSocialProfileMetrics
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Creator intelligence",
                        title: "Your Legend impact",
                        detail: "Reach and engagement generated from protected Legend activity.",
                        dismiss: { dismiss() }
                    )

                    LegendNextSurface(style: .brandBlue) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
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

                    LegendNextSurface(style: .brandBlue) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            LegendNextSectionHeader(eyebrow: "Profile", title: "Content and community")
                            LegendNextKeyValueRow(label: "Posts", value: "\(profileMetrics.postCount)")
                            LegendNextKeyValueRow(label: "Hacs", value: "\(profileMetrics.videoCount)")
                            LegendNextKeyValueRow(label: "Stories", value: "\(profileMetrics.storyCount)")
                            LegendNextKeyValueRow(label: "Following", value: "\(profileMetrics.followingCount)")
                            LegendNextKeyValueRow(label: "Profile visits", value: "\(insights.profileVisits)")
                            LegendNextKeyValueRow(label: "Followers gained this week", value: "\(insights.followersGained)")
                        }
                    }

                    LegendSocialInsightList(
                        title: "Top posts",
                        emptyMessage: "Publish a post to begin building performance history.",
                        insights: insights.topPosts)
                    LegendSocialInsightList(
                        title: "Top Hacs",
                        emptyMessage: "Publish a Hac to begin building Hac performance history.",
                        insights: insights.topVideos)
                    LegendSocialInsightList(
                        title: "Top stories",
                        emptyMessage: "Publish a story to begin building story performance history.",
                        insights: insights.topStories)
                }
                .padding(LegendNextSpacing.md)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome()
        .accessibilityLabel("Creator insights generated from your Legend activity")
    }
}

struct LegendFollowRequestsSheet: View {
    @ObservedObject var social: MobileSocialStore
    @Environment(\.dismiss) private var dismiss
    @State private var state: MobileDataLoadState<[MobileSocialFollowRequest]> = .idle
    @State private var updatingRequestIDs: Set<UUID> = []

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    LegendNextSheetHeader(
                        eyebrow: "Account privacy",
                        title: "Follow requests",
                        detail: "Choose who can see updates from your private Legend account.",
                        dismiss: { dismiss() })

                    content
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.md)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome()
        .task { await loadRequests() }
    }

    @ViewBuilder
    private var content: some View {
        switch state {
        case .idle, .loading:
            ProgressView("Loading follow requests")
                .frame(maxWidth: .infinity, minHeight: 160)

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Try again",
                retry: { Task { await loadRequests() } })

        case .loaded(let requests):
            if requests.isEmpty {
                LegendNextEmptyState(
                    title: "No follow requests",
                    message: "New requests to your private account will appear here.",
                    systemImage: "person.badge.clock")
                    .frame(maxWidth: .infinity, minHeight: 180)
            } else {
                ForEach(requests) { request in
                    LegendContactCard(
                        displayName: request.profile.displayName,
                        subtitle: request.profile.identity.participantType == .agent
                            ? request.profile.roleLabel
                            : request.profile.username.map { "@\($0)" },
                        detail: "Requested \(request.requestedUTC.formatted(date: .abbreviated, time: .omitted))",
                        isVerified: request.profile.isVerified == true,
                        avatar: {
                            LegendProfileAvatar(
                                avatar: request.profile.avatar,
                                displayName: request.profile.displayName,
                                size: 44)
                        },
                        action: {
                            VStack(spacing: 6) {
                                Button("Approve") { decide(request, approve: true) }
                                    .buttonStyle(LegendNextButtonStyle(kind: .primary, isFullWidth: false, controlHeight: 30))
                                Button("Decline") { decide(request, approve: false) }
                                    .buttonStyle(LegendNextButtonStyle(kind: .secondary, isFullWidth: false, controlHeight: 30))
                            }
                            .disabled(updatingRequestIDs.contains(request.id))
                        }
                    )
                }
            }
        }
    }

    private func loadRequests() async {
        state = .loading
        state = await social.incomingFollowRequests()
    }

    private func decide(_ request: MobileSocialFollowRequest, approve: Bool) {
        guard !updatingRequestIDs.contains(request.id) else { return }
        updatingRequestIDs.insert(request.id)
        Task {
            defer { updatingRequestIDs.remove(request.id) }
            guard await social.decideFollowRequest(id: request.id, approve: approve) else { return }
            guard case .loaded(let requests) = state else { return }
            state = .loaded(requests.filter { $0.id != request.id })
        }
    }
}

private struct LegendPostInsightsSheet: View {
    let insight: MobileSocialPostInsight
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Legend performance",
                        title: "\(insight.displayContentType) insights",
                        detail: "Published \(insight.postedUTC.formatted(date: .abbreviated, time: .omitted))",
                        dismiss: { dismiss() }
                    )

                    LegendNextSurface(style: .navy, padding: LegendNextSpacing.md) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text("AUDIENCE REACH")
                                .font(LegendNextTypography.eyebrow)
                                .tracking(0.9)
                                .foregroundStyle(LegendNextColor.goldBright)

                            Text("\(insight.metrics.uniqueViewerCount) reached")
                                .font(LegendNextTypography.title)
                                .foregroundStyle(.white)

                            Text("\(socialPercentage(insight.engagementRatePercentage)) engagement")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(.white.opacity(0.74))
                        }
                    }

                    LegendNextSurface(style: .brandBlue) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            LegendNextSectionHeader(
                                eyebrow: "Legend analytics",
                                title: "Engagement detail"
                            )
                            LegendNextKeyValueRow(label: "Views", value: "\(insight.metrics.viewCount)")
                            LegendNextKeyValueRow(label: "Unique viewers", value: "\(insight.metrics.uniqueViewerCount)")
                            LegendNextKeyValueRow(label: "Appreciations", value: "\(insight.metrics.reactionCount)")
                            LegendNextKeyValueRow(label: "Comments", value: "\(insight.metrics.commentCount)")
                            LegendNextKeyValueRow(label: "Replies", value: "\(insight.metrics.replyCount)")
                            LegendNextKeyValueRow(label: "Reposts", value: "\(insight.metrics.repostCount)")
                            LegendNextKeyValueRow(label: "Saves", value: "\(insight.metrics.saveCount)")
                            LegendNextKeyValueRow(label: "Shares", value: "\(insight.metrics.shareCount)")
                            LegendNextKeyValueRow(label: "Profile visits", value: "\(insight.metrics.profileVisitCount)")
                            LegendNextKeyValueRow(label: "Follows generated", value: "\(insight.metrics.followsGenerated)")

                            if let averageWatchDuration = insight.metrics.averageWatchDurationSeconds {
                                LegendNextKeyValueRow(label: "Average watch time", value: "\(socialNumber(averageWatchDuration)) sec")
                            }
                            if let averageWatchCompletion = insight.metrics.averageWatchCompletionPercentage {
                                LegendNextKeyValueRow(label: "Average completion", value: socialPercentage(averageWatchCompletion))
                            }
                            if insight.contentType == MobileSocialContentType.story.rawValue {
                                LegendNextKeyValueRow(label: "Story exits", value: "\(insight.metrics.storyExitCount)")
                                LegendNextKeyValueRow(label: "Taps forward", value: "\(insight.metrics.storyTapForwardCount)")
                                LegendNextKeyValueRow(label: "Taps backward", value: "\(insight.metrics.storyTapBackwardCount)")
                            }
                        }
                    }
                }
                .padding(LegendNextSpacing.md)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome()
        .accessibilityLabel("Private \(insight.displayContentType) insights")
    }
}

private struct LegendSocialInsightMetric: View {
    let label: String
    let value: String
    let symbol: String
    let color: Color

    var body: some View {
        LegendNextInsetSurface(style: .brandBlue) {
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
        }
        .accessibilityElement(children: .combine)
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
