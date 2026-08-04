import AVKit
import Combine
import SwiftUI
import UIKit

// MARK: - Global Share Authority

/// Supplies the already-existing account-scoped MessagingStore to every
/// share surface without creating another messaging object or data authority.
private struct LegendMessagingStoreEnvironmentKey: EnvironmentKey {
    static let defaultValue: MessagingStore? = nil
}

extension EnvironmentValues {
    var legendMessagingStore: MessagingStore? {
        get { self[LegendMessagingStoreEnvironmentKey.self] }
        set { self[LegendMessagingStoreEnvironmentKey.self] = newValue }
    }
}

/// ONE application-wide sharing control for Legend social content.
///
/// Every Story, Post, and Hac share button routes here.
///
/// Internal sharing:
///     canonical MessagingStore recipient directory
///         -> canonical startConversation
///         -> canonical MessagingStore.send
///         -> server-owned inbox reconciliation
///
/// External sharing:
///     native Apple ShareLink
///
/// This type owns presentation only. It owns no recipient cache, conversation
/// cache, inbox, message persistence, social persistence, or API client.
private struct LegendGlobalShareButton<Label: View>: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    let accessibilityLabel: String
    @ViewBuilder let label: () -> Label

    @Environment(\.legendMessagingStore) private var messaging
    @State private var isPresentingShare = false

    var body: some View {
        Button {
            isPresentingShare = true
        } label: {
            label()
        }
        .buttonStyle(.plain)
        .accessibilityLabel(accessibilityLabel)
        .sheet(isPresented: $isPresentingShare) {
            if let messaging {
                LegendGlobalShareSheet(
                    post: post,
                    social: social,
                    messaging: messaging
                )
            } else {
                LegendGlobalShareUnavailableSheet()
            }
        }
    }
}

private struct LegendGlobalShareSheet: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    @ObservedObject var messaging: MessagingStore

    @Environment(\.dismiss) private var dismiss

    @State private var search = ""
    @State private var recipientBeingSent: LogicalParticipantIdentity?

    private var normalizedBody: String {
        post.body.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// One textual representation for the existing message body contract.
    ///
    /// No fake social attachment or parallel message schema is introduced.
    /// When the canonical messaging contract gains a typed social attachment,
    /// that evolution can occur at the messaging authority instead of here.
    private var internalMessageBody: String {
        let heading =
            "Shared a Legend \(post.displayContentType) by \(post.author.displayName)"

        guard !normalizedBody.isEmpty else {
            return heading
        }

        return "\(heading)\n\n\(normalizedBody)"
    }

    private var externalShareBody: String {
        normalizedBody.isEmpty
            ? "Legend \(post.displayContentType) by \(post.author.displayName)"
            : normalizedBody
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                destinationHeader
                internalShareSection
            }
            .background(LegendNextCanvas())
            .navigationTitle("Share")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                    .font(.subheadline.weight(.semibold))
                }
            }
            .task {
                // This is the SAME recipient authority already used by Messages.
                messaging.loadRecipients()
            }
            .onChange(of: search) { _, value in
                messaging.searchRecipients(value)
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }

    private var destinationHeader: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            Text("SEND IN LEGEND")
                .font(.caption2.weight(.bold))
                .tracking(1.0)
                .foregroundStyle(LegendNextColor.gold)

            TextField("Search Legend members", text: $search)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .padding(.horizontal, LegendNextSpacing.md)
                .frame(minHeight: 46)
                .background(
                    LegendNextColor.surfaceElevated,
                    in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.control,
                        style: .continuous
                    )
                )

            recipientScopes

            ShareLink(item: externalShareBody) {
                HStack(spacing: LegendNextSpacing.sm) {
                    Image(systemName: "square.and.arrow.up")
                        .font(.body.weight(.semibold))

                    VStack(alignment: .leading, spacing: 2) {
                        Text("Share outside Legend")
                            .font(.subheadline.weight(.bold))

                        Text("Messages, Mail, AirDrop, and other apps")
                            .font(.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    Spacer(minLength: 0)

                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.textSecondary)
                }
                .foregroundStyle(LegendNextColor.textPrimary)
                .padding(.horizontal, LegendNextSpacing.md)
                .frame(minHeight: 56)
                .background(
                    LegendNextColor.surfaceElevated,
                    in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.control,
                        style: .continuous
                    )
                )
            }
            .simultaneousGesture(
                TapGesture().onEnded {
                    // Preserve the existing external-share metric semantics.
                    social.recordShare(postID: post.id)
                }
            )
            .accessibilityLabel("Share outside Legend")
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.top, LegendNextSpacing.sm)
        .padding(.bottom, LegendNextSpacing.sm)
    }

    @ViewBuilder
    private var recipientScopes: some View {
        if messaging.availableRecipientScopes.count > 1 {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: LegendNextSpacing.xs) {
                    ForEach(messaging.availableRecipientScopes) { scope in
                        Button {
                            messaging.selectRecipientScope(scope)
                            if !search.isEmpty {
                                messaging.searchRecipients(search)
                            }
                        } label: {
                            Text(scope.rawValue)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(
                                    messaging.selectedRecipientScope == scope
                                        ? LegendNextColor.midnight
                                        : LegendNextColor.textSecondary
                                )
                                .padding(.horizontal, LegendNextSpacing.sm)
                                .frame(minHeight: 34)
                                .background(
                                    messaging.selectedRecipientScope == scope
                                        ? AnyShapeStyle(LegendNextGradient.gold)
                                        : AnyShapeStyle(
                                            LegendNextColor.surfaceElevated
                                        ),
                                    in: Capsule()
                                )
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var internalShareSection: some View {
        switch messaging.recipientState {
        case .idle, .loading:
            VStack(spacing: LegendNextSpacing.sm) {
                ProgressView()
                Text("Loading Legend members")
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

        case .unavailable(let failure):
            VStack(spacing: LegendNextSpacing.md) {
                Image(systemName: "person.2.slash")
                    .font(.title2)
                    .foregroundStyle(LegendNextColor.textSecondary)

                Text(failure.title)
                    .font(.headline)

                Text(failure.message)
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .multilineTextAlignment(.center)

                Button("Retry") {
                    messaging.loadRecipients(search: search)
                }
                .buttonStyle(.borderedProminent)
                .tint(LegendNextColor.gold)
            }
            .padding(LegendNextSpacing.lg)
            .frame(maxWidth: .infinity, maxHeight: .infinity)

        case .loaded(let recipients):
            if recipients.isEmpty {
                VStack(spacing: LegendNextSpacing.sm) {
                    Image(systemName: "person.2.slash")
                        .font(.title2)
                        .foregroundStyle(LegendNextColor.textSecondary)

                    Text(search.isEmpty
                         ? "No Legend members available"
                         : "No matching Legend members")
                        .font(.headline)

                    Text(
                        search.isEmpty
                            ? "Try another member category."
                            : "Try a different search."
                    )
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textSecondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(LegendNextSpacing.lg)
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(recipients) { recipient in
                            recipientRow(recipient)

                            Divider()
                                .padding(.leading, 62)
                        }
                    }
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(.bottom, LegendNextSpacing.xl)
                }
                .scrollDismissesKeyboard(.interactively)
            }
        }
    }

    private func recipientRow(
        _ recipient: MessagingRecipient
    ) -> some View {
        Button {
            sendInsideLegend(to: recipient)
        } label: {
            HStack(spacing: LegendNextSpacing.sm) {
                LegendProfileAvatar(
                    avatar: recipient.avatar,
                    displayName: recipient.displayName,
                    size: 44
                )

                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 4) {
                        Text(recipient.displayName)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)

                        if recipient.isVerified == true {
                            Image(systemName: "checkmark.seal.fill")
                                .font(.caption2)
                                .foregroundStyle(LegendNextColor.gold)
                        }
                    }

                    if let email = recipient.email,
                       !email.trimmingCharacters(
                           in: .whitespacesAndNewlines
                       ).isEmpty {
                        Text(email)
                            .font(.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(1)
                    }
                }

                Spacer(minLength: LegendNextSpacing.sm)

                if recipientBeingSent == recipient.identity ||
                   (messaging.isStartingConversation &&
                    recipientBeingSent == recipient.identity) ||
                   (messaging.isSending &&
                    recipientBeingSent == recipient.identity) {
                    ProgressView()
                        .controlSize(.small)
                } else {
                    Image(systemName: "paperplane.fill")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.gold)
                        .frame(width: 36, height: 36)
                        .background(
                            LegendNextColor.gold.opacity(0.10),
                            in: Circle()
                        )
                }
            }
            .padding(.vertical, LegendNextSpacing.sm)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(
            recipientBeingSent != nil ||
            messaging.isStartingConversation ||
            messaging.isSending
        )
        .accessibilityLabel(
            "Send \(post.displayContentType) to \(recipient.displayName)"
        )
    }

    private func sendInsideLegend(
        to recipient: MessagingRecipient
    ) {
        guard recipientBeingSent == nil,
              !messaging.isStartingConversation,
              !messaging.isSending else {
            return
        }

        recipientBeingSent = recipient.identity

        // Canonical direct-conversation authority.
        // Existing conversations are returned/opened by the same server path;
        // new direct conversations are created by that same authority.
        messaging.startConversation(with: recipient) { _ in
            Task { @MainActor in
                // Canonical message-send authority. Successful first sends
                // already reconcile the server-owned Previous Messages inbox.
                let message = await messaging.send(
                    body: internalMessageBody
                )

                recipientBeingSent = nil

                guard message != nil else {
                    return
                }

                // One social share metric path for a successful internal send.
                social.recordShare(postID: post.id)
                dismiss()
            }
        }
    }
}

private struct LegendGlobalShareUnavailableSheet: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            VStack(spacing: LegendNextSpacing.md) {
                Image(systemName: "square.and.arrow.up")
                    .font(.title2)
                    .foregroundStyle(LegendNextColor.textSecondary)

                Text("Sharing unavailable")
                    .font(.headline)

                Text(
                    "The account messaging authority is not available in this session."
                )
                .font(.subheadline)
                .foregroundStyle(LegendNextColor.textSecondary)
                .multilineTextAlignment(.center)
            }
            .padding(LegendNextSpacing.xl)
            .navigationTitle("Share")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                }
            }
        }
        .presentationDetents([.medium])
    }
}

/// The native home is intentionally a composition over the protected mobile
/// projections. It never derives a role, feed audience, finance state, or
/// profile identity in the client.
struct LegendSocialHomeSection<DashboardContent: View>: View {
    @EnvironmentObject private var scrollChrome: LegendScrollChrome

    let session: MobileSession
    @ObservedObject var social: MobileSocialStore
    @ObservedObject var messaging: MessagingStore
    @ObservedObject var activity: LegendDailyActivityStore
    let openJoinedGroup: (UUID) -> Void
    let refreshSocial: () async -> Void
    let dashboardContent: DashboardContent

    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var isPresentingActivity = false
    @State private var commentTarget: MobileSocialPost?
    @State private var postInsight: MobileSocialPostInsight?
    @State private var storyCollection: MobileSocialStoryCollection?
    @State private var selectedPost: MobileSocialPost?
    @State private var publicProfile: LegendPublicProfileRoute?

    init(
        session: MobileSession,
        social: MobileSocialStore,
        messaging: MessagingStore,
        activity: LegendDailyActivityStore,
        openJoinedGroup: @escaping (UUID) -> Void,
        refreshSocial: @escaping () async -> Void,
        @ViewBuilder dashboardContent: () -> DashboardContent
    ) {
        self.session = session
        _social = ObservedObject(wrappedValue: social)
        _messaging = ObservedObject(wrappedValue: messaging)
        _activity = ObservedObject(wrappedValue: activity)
        self.openJoinedGroup = openJoinedGroup
        self.refreshSocial = refreshSocial
        self.dashboardContent = dashboardContent()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
            dashboardContent
            storyContent
            socialFeed
        }
        .sheet(item: $creationRoute) { _ in
            LegendSocialCreationSheet(
                route: $creationRoute,
                social: social)
        }
        .sheet(isPresented: $isPresentingActivity) {
            LegendDailyActivitySheet(
                activity: activity,
                currentIdentity: session.actor.identity,
                social: social)
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
        activity.markTodayViewed()
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
            .padding(.horizontal, LegendNextSpacing.xs)
            .padding(.vertical, LegendNextSpacing.tiny)
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
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {

                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                ForEach(snapshot.promotedGroups) { group in
                    LegendPromotedGroupCard(
                        group: group,
                        isJoining: messaging.isCreatingGroup,
                        join: {
                            guard !group.isJoinedByCurrentActor else { return }
                            Task {
                                if await messaging.joinPromotedGroup(
                                    conversationID: group.conversationID)
                                {
                                    await refreshSocial()
                                    openJoinedGroup(group.conversationID)
                                }
                            }
                        })
                }

                if let publication = social.publication {
                    LegendSocialPublicationBanner(
                        publication: publication,
                        retry: social.retryPublication,
                        dismiss: social.dismissPublication)
                }

                if snapshot.posts.isEmpty && snapshot.promotedGroups.isEmpty {
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
    }

}

/// The feed's single premium entry treatment. It gives the network content a
/// strong visual handoff from Home into live community posts without creating a
/// second styling system for the cards that follow.


/// A safe invitation projection, not a conversation preview. The group must be
/// joined through Messaging before the normal conversation API can reveal any
/// history.
private struct LegendPromotedGroupCard: View {
    let group: MobileSocialPromotedGroup
    let isJoining: Bool
    let join: () -> Void

    var body: some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                groupAvatar

                VStack(alignment: .leading, spacing: LegendNextSpacing.tiny) {
                    Text("FEATURED GROUP")
                        .font(.caption2.weight(.bold))
                        .tracking(1.1)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Text(group.subject)
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(.white)
                        .lineLimit(1)

                    Text("Hosted by \(group.owner.displayName) · \(group.activeMemberCount) members")
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.72))
                        .lineLimit(1)
                }

                Spacer(minLength: 0)

                Button(group.isJoinedByCurrentActor ? "Joined" : "Join") {
                    join()
                }
                .font(.caption.weight(.bold))
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .tint(group.isJoinedByCurrentActor ? .white.opacity(0.18) : LegendNextColor.gold)
                .foregroundStyle(group.isJoinedByCurrentActor ? .white : LegendNextColor.midnight)
                .disabled(group.isJoinedByCurrentActor || isJoining)
                .accessibilityLabel(
                    group.isJoinedByCurrentActor
                        ? "Joined \(group.subject)"
                        : "Join \(group.subject)")
            }
        }
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder
    private var groupAvatar: some View {
        if let data = group.groupAvatar?.imageData,
           let image = UIImage(data: data) {
            Image(uiImage: image)
                .resizable()
                .scaledToFill()
                .frame(width: 44, height: 44)
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        } else {
            Image(systemName: "person.3.fill")
                .font(.title3.weight(.bold))
                .foregroundStyle(LegendNextColor.midnight)
                .frame(width: 44, height: 44)
                .background(
                    LegendNextGradient.gold,
                    in: RoundedRectangle(cornerRadius: 12, style: .continuous)
                )
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
            HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
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
                    font: .caption.weight(.semibold),
                    badgePlacement: .alongsideProfileImage)
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
    @FocusState private var isReplyFocused: Bool

    @State private var itemIndex = 0
    @State private var progress = 0.0
    @State private var storyDurationSeconds = 5.0
    @State private var isPaused = false
    @State private var storyPressStartedAt: Date?
    @State private var isHoldingStory = false
    @State private var hasRecordedDeparture = false
    @State private var replyBody = ""

    private let imageProgressTimer = Timer.publish(
        every: 0.05,
        on: .main,
        in: .common
    ).autoconnect()

    /// The collection establishes Story order for this viewer session.
    /// Mutable engagement state comes back from MobileSocialStore so the
    /// viewer never creates a second reaction/comment source of truth.
    private var collectionItem: MobileSocialPost {
        collection.items[itemIndex]
    }

    private var item: MobileSocialPost {
        guard case .loaded(let snapshot) = social.state else {
            return collectionItem
        }

        return snapshot.stories.first(where: { $0.id == collectionItem.id })
            ?? collectionItem
    }

    private var isOwner: Bool {
        collection.author.identity == currentIdentity
    }

    private var isVideoStory: Bool {
        item.media.contains(where: \.isVideo)
    }

    private var normalizedReplyBody: String {
        replyBody.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                LegendNextColor.midnight
                    .ignoresSafeArea()

                LegendStoryMedia(
                    story: item,
                    social: social,
                    isPaused: isPaused,
                    playbackProgress: { current, duration in
                        guard !isReplyFocused else { return }
                        storyDurationSeconds = max(duration, 0.1)
                        progress = min(
                            max(current / storyDurationSeconds, 0),
                            1
                        )
                    },
                    playbackFinished: {
                        guard !isReplyFocused else { return }
                        moveForward(manual: false)
                    }
                )
                .id(collectionItem.id)
                .frame(
                    width: geometry.size.width,
                    height: geometry.size.height
                )
                .background(LegendNextColor.midnight)

                navigationTapZones

                VStack(spacing: LegendNextSpacing.sm) {
                    progressBars
                    header

                    Spacer(minLength: 0)

                    footer
                }
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(
                    .top,
                    max(geometry.safeAreaInsets.top, LegendNextSpacing.sm)
                )
                .padding(
                    .bottom,
                    max(geometry.safeAreaInsets.bottom, LegendNextSpacing.md)
                )
            }
            .simultaneousGesture(dismissGesture)
        }
        .onAppear {
            recordInitialView()
        }
        .onChange(of: itemIndex) {
            progress = 0
            storyDurationSeconds = 5
            isPaused = false
            storyPressStartedAt = nil
            isHoldingStory = false
            isReplyFocused = false
            replyBody = ""
            recordInitialView()
        }
        .onChange(of: isReplyFocused) { _, focused in
            isPaused = focused
        }
        .onReceive(imageProgressTimer) { _ in
            guard !isPaused,
                  !isReplyFocused,
                  !isVideoStory
            else {
                return
            }

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
        .accessibilityLabel(
            "Story viewer for \(collection.author.displayName)"
        )
    }

    /// Navigation owns only the center media region. It deliberately leaves
    /// the header and footer outside its hit-testing surface so close, reply,
    /// reaction, and share controls cannot be intercepted by Story navigation.
    private var navigationTapZones: some View {
        VStack(spacing: 0) {
            Color.clear
                .frame(height: 104)
                .allowsHitTesting(false)

            HStack(spacing: 0) {
                storyInteractionZone(
                    accessibilityLabel: "Previous story",
                    moveForwardOnTap: false
                )

                storyInteractionZone(
                    accessibilityLabel: "Next story",
                    moveForwardOnTap: true
                )
            }

            Color.clear
                .frame(height: 128)
                .allowsHitTesting(false)
        }
        .padding(.horizontal, 2)
    }

    private func storyInteractionZone(
        accessibilityLabel: String,
        moveForwardOnTap: Bool
    ) -> some View {
        Color.clear
            .contentShape(Rectangle())
            .accessibilityLabel(accessibilityLabel)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        beginStoryPress()
                    }
                    .onEnded { value in
                        endStoryPress(
                            translation: value.translation,
                            moveForwardOnTap: moveForwardOnTap
                        )
                    }
            )
    }

    private func beginStoryPress() {
        guard !isReplyFocused else {
            return
        }

        if storyPressStartedAt == nil {
            storyPressStartedAt = Date()
            isHoldingStory = true
            isPaused = true
        }
    }

    private func endStoryPress(
        translation: CGSize,
        moveForwardOnTap: Bool
    ) {
        guard !isReplyFocused else {
            storyPressStartedAt = nil
            isHoldingStory = false
            isPaused = true
            return
        }

        let startedAt = storyPressStartedAt ?? Date()
        let pressDuration = Date().timeIntervalSince(startedAt)

        storyPressStartedAt = nil
        isHoldingStory = false
        isPaused = false

        let horizontalMovement = abs(translation.width)
        let verticalMovement = abs(translation.height)
        let movedSignificantly =
            horizontalMovement >= 18 ||
            verticalMovement >= 18

        // A held Story resumes exactly where it was.
        // It must never be interpreted as previous/next.
        guard pressDuration < 0.22,
              !movedSignificantly
        else {
            return
        }

        if moveForwardOnTap {
            moveForward(manual: true)
        } else {
            moveBackward()
        }
    }

    private var dismissGesture: some Gesture {
        DragGesture(minimumDistance: 30)
            .onEnded { value in
                guard !isReplyFocused else { return }

                let vertical = value.translation.height
                let horizontal = abs(value.translation.width)

                guard vertical > 70,
                      vertical > horizontal
                else {
                    return
                }

                closeStory(recordExit: true)
            }
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
                                .frame(
                                    width: proxy.size.width
                                        * progressValue(for: index)
                                )
                        }
                }
                .frame(height: 3)
            }
        }
        .accessibilityLabel(
            "Story \(itemIndex + 1) of \(collection.items.count)"
        )
        .allowsHitTesting(false)
    }

    private var header: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            LegendProfileAvatar(
                avatar: collection.author.avatar,
                displayName: collection.author.displayName,
                size: 38
            )

            VStack(alignment: .leading, spacing: 2) {
                if isOwner {
                    Text("Your story")
                        .font(.subheadline.weight(.bold))
                } else {
                    LegendVerifiedName(
                        collection.author.displayName,
                        isVerified: collection.author.isVerified == true,
                        font: .subheadline.weight(.bold),
                        textColor: .white,
                        badgePlacement: .alongsideProfileImage
                    )
                }

                Text(item.postedUTC, style: .relative)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.76))
            }
            .foregroundStyle(.white)

            Spacer(minLength: LegendNextSpacing.sm)

            if isOwner {
                Label(
                    "\(item.metrics.uniqueViewerCount)",
                    systemImage: "eye"
                )
                .font(.caption.weight(.semibold))
                .foregroundStyle(.white)
                .accessibilityLabel(
                    "\(item.metrics.uniqueViewerCount) unique viewers"
                )
            }

            Button {
                closeStory(recordExit: true)
            } label: {
                Image(systemName: "xmark")
                    .font(.body.weight(.bold))
                    .frame(width: 44, height: 44)
                    .background(
                        LegendNextColor.midnight.opacity(0.52),
                        in: Circle()
                    )
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.white)
            .accessibilityLabel("Close story")
        }
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private var footer: some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.sm
        ) {
            if !item.body
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .isEmpty
            {
                Text(item.body)
                    .font(.body.weight(.medium))
                    .foregroundStyle(.white)
                    .lineLimit(4)
                    .shadow(
                        color: LegendNextColor.midnight.opacity(0.45),
                        radius: 4
                    )
            }

            if isOwner {
                ownerFooter
            } else {
                viewerFooter
            }
        }
        .contentShape(Rectangle())
    }

    private var ownerFooter: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Label(
                "\(item.metrics.uniqueViewerCount)",
                systemImage: "eye.fill"
            )
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, LegendNextSpacing.md)
            .frame(minHeight: 44)
            .background(
                LegendNextColor.midnight.opacity(0.52),
                in: Capsule()
            )
            .accessibilityLabel(
                "\(item.metrics.uniqueViewerCount) unique viewers"
            )

            Spacer(minLength: 0)

            storyShareLink
        }
    }

    private var viewerFooter: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            TextField(
                "Reply to \(collection.author.displayName)",
                text: $replyBody
            )
            .focused($isReplyFocused)
            .textFieldStyle(.plain)
            .submitLabel(.send)
            .onSubmit(submitReply)
            .padding(.horizontal, LegendNextSpacing.md)
            .frame(minHeight: 44)
            .background(
                LegendNextColor.midnight.opacity(0.58),
                in: Capsule()
            )
            .overlay {
                Capsule()
                    .strokeBorder(
                        .white.opacity(isReplyFocused ? 0.42 : 0.16),
                        lineWidth: 1
                    )
            }
            .foregroundStyle(.white)
            .accessibilityLabel("Reply to story")

            if !normalizedReplyBody.isEmpty {
                Button(action: submitReply) {
                    Image(systemName: "paperplane.fill")
                        .font(.body.weight(.semibold))
                        .frame(width: 44, height: 44)
                        .background(.white.opacity(0.20), in: Circle())
                        .contentShape(Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .accessibilityLabel("Send story reply")
            }

            Button {
                social.toggleReaction(postID: item.id)
            } label: {
                Image(
                    systemName: item.reactedByCurrentActor
                        ? "heart.fill"
                        : "heart"
                )
                .font(.title3)
                .frame(width: 44, height: 44)
                .background(
                    LegendNextColor.midnight.opacity(0.58),
                    in: Circle()
                )
                .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(
                item.reactedByCurrentActor
                    ? LegendNextColor.danger
                    : .white
            )
            .accessibilityLabel(
                item.reactedByCurrentActor
                    ? "Remove appreciation"
                    : "Appreciate story"
            )

            storyShareLink
        }
    }

    private var storyShareLink: some View {
        LegendGlobalShareButton(
            post: item,
            social: social,
            accessibilityLabel: "Share story"
        ) {
            Image(systemName: "square.and.arrow.up")
                .font(.title3)
                .frame(width: 44, height: 44)
                .background(
                    LegendNextColor.midnight.opacity(0.58),
                    in: Circle()
                )
                .contentShape(Circle())
                .foregroundStyle(.white)
        }
    }

    private func progressValue(for index: Int) -> CGFloat {
        if index < itemIndex {
            return 1
        }

        if index > itemIndex {
            return 0
        }

        return CGFloat(min(max(progress, 0), 1))
    }

    private func moveForward(manual: Bool) {
        guard !isReplyFocused else { return }

        recordCurrent(
            interaction: manual ? "TapForward" : nil,
            completed: true
        )

        guard itemIndex < collection.items.count - 1 else {
            closeStory(recordExit: false)
            return
        }

        itemIndex += 1
    }

    private func moveBackward() {
        guard !isReplyFocused else { return }

        guard itemIndex > 0 else {
            progress = 0
            return
        }

        recordCurrent(interaction: "TapBackward")
        itemIndex -= 1
    }

    private func submitReply() {
        let body = normalizedReplyBody

        guard !body.isEmpty else {
            return
        }

        social.addComment(
            postID: item.id,
            body: body
        )

        replyBody = ""
        isReplyFocused = false
    }

    private func recordInitialView() {
        social.recordView(postID: item.id)
    }

    private func recordCurrent(
        interaction: String?,
        completed: Bool = false
    ) {
        let duration = max(storyDurationSeconds, 0.1)
        let boundedProgress = min(max(progress, 0), 1)

        social.recordView(
            postID: item.id,
            watchDurationSeconds: Decimal(
                completed
                    ? duration
                    : boundedProgress * duration
            ),
            watchCompletionPercentage: Decimal(
                completed
                    ? 100
                    : boundedProgress * 100
            ),
            storyInteractionType: interaction
        )
    }

    private func closeStory(recordExit: Bool) {
        guard !hasRecordedDeparture else {
            dismiss()
            return
        }

        hasRecordedDeparture = true
        isReplyFocused = false

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
    let profilePosts: [MobileSocialPost]

    @Environment(\.dismiss) private var dismiss
    @State private var commentTarget: MobileSocialPost?
    @State private var postInsight: MobileSocialPostInsight?
    @State private var publicProfile: LegendPublicProfileRoute?
    @State private var editingPost: MobileSocialPost?
    @State private var deletionTarget: MobileSocialPost?

    init(
        post: MobileSocialPost,
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        profilePosts: [MobileSocialPost] = []
    ) {
        self.post = post
        self.currentIdentity = currentIdentity
        _social = ObservedObject(wrappedValue: social)
        self.profilePosts = profilePosts
    }

    var body: some View {
        if post.isVideoHac {
            // A Hac is always opened in the dedicated vertical-video surface.
            // Ordinary posts intentionally stay in the post detail presentation.
            LegendHacViewportFeed(
                posts: postsInProfileFeed.filter(\.isVideoHac),
                currentIdentity: currentIdentity,
                social: social,
                initialPostID: post.id,
                presentsDismissControl: true)
        } else {
            standardPostDetail
        }
    }

    private var standardPostDetail: some View {
        LegendScrollView {
            LazyVStack(spacing: LegendNextSpacing.xs) {
                ForEach(postsInProfileFeed) { feedPost in
                    postCard(feedPost)
                }
            }
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

    /// Profile selections open at the tapped item and continue through that
    /// profile's current collection. Other callers retain a single-post view.
    private var postsInProfileFeed: [MobileSocialPost] {
        [post] + profilePosts.filter { $0.id != post.id }
    }

    private func postCard(_ feedPost: MobileSocialPost) -> some View {
        LegendSocialPostCard(
            post: feedPost,
            currentIdentity: currentIdentity,
            social: social,
            react: {
                social.toggleReaction(postID: feedPost.id)
            },
            comment: {
                commentTarget = feedPost
            },
            follow: {
                social.toggleFollow(author: feedPost.author, sourcePostID: feedPost.id)
            },
            insights: {
                Task {
                    postInsight = await social.postInsights(postID: feedPost.id)
                }
            },
            presentation: .detail,
            open: {},
            openProfile: {
                publicProfile = LegendPublicProfileRoute(
                    profile: feedPost.author,
                    isFollowing: feedPost.followedByCurrentActor,
                    isFollowRequestPending: feedPost.followRequestPending ?? false)
            })
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
                            font: .subheadline.weight(.bold),
                            badgePlacement: .alongsideProfileImage
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
                            LegendSocialVideoPoster(
                                media: media,
                                social: social,
                                contentMode: .fill,
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
            // Hacs own the card's full horizontal width. Without this explicit
            // proposal, a vertical AVPlayer can collapse the page to its
            // intrinsic portrait width and leave an empty strip on the right.
            .frame(maxWidth: .infinity)
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

            LegendGlobalShareButton(
                post: post,
                social: social,
                accessibilityLabel: "Share this update"
            ) {
                actionMetricLabel(
                    symbolName: "paperplane",
                    count: post.metrics.shareCount
                )
            }

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
        if let contentType = MobileSocialContentType(rawValue: post.contentType),
           contentType.format.usesFixedCanvasAspectRatio {
            return CGFloat(contentType.format.mediaAspectRatio)
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
                LegendHacViewportFeed(
                    posts: snapshot.hacs.filter(\.isVideoHac),
                    currentIdentity: currentIdentity,
                    social: social,
                    initialPostID: initialPostID,
                    presentsDismissControl: presentsDismissControl)
            }
        }
    }
}

/// The viewport-retention contract for native Hac playback. The feed owns
/// exactly three hardware decoders: the previous, active, and next Hac in the
/// vertical sequence. The next three Hacs' asset metadata is warmed separately,
/// without allocating extra players. Network media itself remains owned by
/// `MobileSocialStore`'s secure disk cache, so FYP and a profile's Hac feed
/// cannot form competing caches.
enum LegendHacPlaybackWindow {
    static let maximumPlayerCount = 3

    static func retainedIndexes(activeIndex: Int, count: Int) -> [Int] {
        guard count > 0 else { return [] }
        let lowerBound = max(0, activeIndex - 1)
        let upperBound = min(count - 1, activeIndex + 1)
        return Array(lowerBound...upperBound)
    }

    static func prefetchIndexes(activeIndex: Int, count: Int) -> [Int] {
        guard count > 0 else { return [] }
        let lowerBound = max(0, activeIndex + 1)
        let upperBound = min(count - 1, activeIndex + 3)
        guard lowerBound <= upperBound else { return [] }
        return Array(lowerBound...upperBound)
    }
}

/// Bridges AVFoundation's custom resource-loader callbacks to the existing
/// authenticated media endpoint. The server already supports HTTP 206 range
/// responses, so this keeps the single MP4 pipeline intact while allowing the
/// player to fetch only the moov atom and the samples it needs for playback.
///
/// The loader is retained for exactly as long as its AVURLAsset is retained by
/// the three-player window. It never caches a second full video file.
private final class LegendHacMediaResourceLoader: NSObject, AVAssetResourceLoaderDelegate, URLSessionDataDelegate {
    let assetURL: URL
    let callbackQueue = DispatchQueue(label: "com.mylegnd.hac-resource-loader")

    private let source: MobileSocialMediaStream
    private let delegateQueue: OperationQueue
    private let lock = NSLock()
    private var session: URLSession!
    private var loadingRequests: [Int: AVAssetResourceLoadingRequest] = [:]

    init(source: MobileSocialMediaStream) {
        self.source = source

        var components = URLComponents(url: source.url, resolvingAgainstBaseURL: false)
        components?.scheme = "legend-media"
        self.assetURL = components?.url ?? source.url

        let queue = OperationQueue()
        queue.name = "com.mylegnd.hac-resource-loader.network"
        queue.maxConcurrentOperationCount = 1
        self.delegateQueue = queue
        super.init()

        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 120
        session = URLSession(
            configuration: configuration,
            delegate: self,
            delegateQueue: delegateQueue)
    }

    deinit {
        session.invalidateAndCancel()
    }

    func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader,
        shouldWaitForLoadingOfRequestedResource loadingRequest: AVAssetResourceLoadingRequest
    ) -> Bool {
        var request = URLRequest(url: source.url)
        request.httpMethod = "GET"
        request.timeoutInterval = 60
        source.headers.forEach { request.setValue($0.value, forHTTPHeaderField: $0.key) }

        let dataRequest = loadingRequest.dataRequest
        let start = dataRequest.map {
            max($0.currentOffset, $0.requestedOffset)
        } ?? 0
        if dataRequest?.requestsAllDataToEndOfResource == true {
            request.setValue("bytes=\(start)-", forHTTPHeaderField: "Range")
        } else {
            // AVFoundation may first request only content information. Fetch a
            // tiny leading range in that case; the response establishes MIME
            // type, full length, and range support without downloading media.
            let requestedLength = max(1, Int64(dataRequest?.requestedLength ?? 2))
            let end = start + requestedLength - 1
            request.setValue("bytes=\(start)-\(end)", forHTTPHeaderField: "Range")
        }

        let task = session.dataTask(with: request)
        lock.lock()
        loadingRequests[task.taskIdentifier] = loadingRequest
        lock.unlock()
        task.resume()
        return true
    }

    func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader,
        didCancel loadingRequest: AVAssetResourceLoadingRequest
    ) {
        let taskIdentifier: Int? = withLock {
            loadingRequests.first(where: { $0.value === loadingRequest })?.key
        }
        guard let taskIdentifier else { return }

        _ = withLock {
            loadingRequests.removeValue(forKey: taskIdentifier)
        }
        session.getAllTasks { tasks in
            tasks.first(where: { $0.taskIdentifier == taskIdentifier })?.cancel()
        }
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
    ) {
        guard let request = loadingRequest(for: dataTask.taskIdentifier),
              let response = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            return
        }

        guard (200...299).contains(response.statusCode) else {
            finish(
                request: request,
                taskIdentifier: dataTask.taskIdentifier,
                error: streamError(
                    code: response.statusCode,
                    message: "Legend could not load this Hac stream."))
            completionHandler(.cancel)
            return
        }

        if let information = request.contentInformationRequest {
            // `contentType` is an AVFoundation file-type identifier, not an
            // HTTP MIME value. Supplying "video/mp4" here makes otherwise
            // valid protected MP4s fail during metadata preparation.
            information.contentType = AVFileType.mp4.rawValue
            information.contentLength = totalLength(from: response)
            information.isByteRangeAccessSupported = response.statusCode == 206
                || response.value(forHTTPHeaderField: "Accept-Ranges")?.lowercased().contains("bytes") == true
        }
        completionHandler(.allow)
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive data: Data
    ) {
        loadingRequest(for: dataTask.taskIdentifier)?.dataRequest?.respond(with: data)
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        guard let request = removeLoadingRequest(for: task.taskIdentifier) else { return }
        if let error {
            request.finishLoading(with: error)
        } else {
            request.finishLoading()
        }
    }

    private func loadingRequest(for taskIdentifier: Int) -> AVAssetResourceLoadingRequest? {
        withLock { loadingRequests[taskIdentifier] }
    }

    private func removeLoadingRequest(for taskIdentifier: Int) -> AVAssetResourceLoadingRequest? {
        withLock { loadingRequests.removeValue(forKey: taskIdentifier) }
    }

    private func finish(
        request: AVAssetResourceLoadingRequest,
        taskIdentifier: Int,
        error: Error
    ) {
        _ = withLock {
            loadingRequests.removeValue(forKey: taskIdentifier)
        }
        request.finishLoading(with: error)
    }

    private func totalLength(from response: HTTPURLResponse) -> Int64 {
        guard let contentRange = response.value(forHTTPHeaderField: "Content-Range"),
              let slash = contentRange.lastIndex(of: "/"),
              let length = Int64(contentRange[contentRange.index(after: slash)...]) else {
            return max(0, response.expectedContentLength)
        }
        return length
    }

    private func streamError(code: Int, message: String) -> NSError {
        NSError(
            domain: "com.mylegnd.hac-stream",
            code: code,
            userInfo: [NSLocalizedDescriptionKey: message])
    }

    private func withLock<T>(_ operation: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return operation()
    }
}

/// One ownership boundary for Hac decoders, playback state, prewarming, and
/// measured watch time. A `LegendHacViewportFeed` owns exactly one coordinator
/// regardless of whether it is opened from FYP or a member profile.
@MainActor
private final class LegendHacPlaybackCoordinator: ObservableObject {
    @Published private(set) var activePostID: UUID?
    @Published private(set) var isPlaying = false
    @Published private(set) var isMuted = false
    @Published private(set) var readyMediaIDs = Set<UUID>()
    @Published private(set) var failures: [UUID: UserFacingFailure] = [:]

    private var players: [UUID: AVPlayer] = [:]
    private var assets: [UUID: AVURLAsset] = [:]
    private var resourceLoaders: [UUID: LegendHacMediaResourceLoader] = [:]
    private var assetPreloadTasks: [UUID: Task<AVURLAsset?, Never>] = [:]
    private var itemStatusObservations: [UUID: NSKeyValueObservation] = [:]
    private var itemEndObservers: [UUID: NSObjectProtocol] = [:]
    private var retainedMediaIDs = Set<UUID>()
    private var retainedAssetIDs = Set<UUID>()
    private var activeMediaID: UUID?
    private var activePost: MobileSocialPost?
    private var socialStore: MobileSocialStore?

    func player(for mediaID: UUID) -> AVPlayer? {
        players[mediaID]
    }

    func failure(for mediaID: UUID) -> UserFacingFailure? {
        failures[mediaID]
    }

    func activate(
        posts: [MobileSocialPost],
        postID: UUID,
        social: MobileSocialStore
    ) async {
        commitActive(posts: posts, postID: postID, social: social)
        await prepareWindow(posts: posts, postID: postID, social: social)
    }

    /// Performs only the asynchronous player-window maintenance after a paging
    /// commit has already started the adjacent preloaded player.
    func refreshWindow(
        posts: [MobileSocialPost],
        postID: UUID,
        social: MobileSocialStore
    ) async {
        await prepareWindow(posts: posts, postID: postID, social: social)
    }

    /// Runs on the same main-actor turn as SwiftUI's paging selection update.
    /// It has no network work, task dispatch, player construction, or layout
    /// work: the already-prerolled neighbor is simply made audible and played.
    func commitActive(
        posts: [MobileSocialPost],
        postID: UUID,
        social: MobileSocialStore
    ) {
        guard let activeIndex = posts.firstIndex(where: { $0.id == postID }),
              let activeMedia = posts[activeIndex].media.first else {
            stop(social: social)
            return
        }

        let previousMediaID = activeMediaID
        if previousMediaID != activeMedia.id {
            recordActivePlayback(with: social)
            if let previousMediaID {
                players[previousMediaID]?.pause()
            }
        }

        socialStore = social
        configureRetentionWindow(posts: posts, activeIndex: activeIndex)
        activePostID = postID
        activePost = posts[activeIndex]
        activeMediaID = activeMedia.id
        isPlaying = true

        // Configure and activate the hardware route before the new player is
        // audible. The session remains alive while adjacent Hacs rotate.
        if !seedAudio(for: activeMedia.id) {
            isPlaying = false
            return
        }

        synchronizePlayerAudibility()
        playActive()
    }

    /// Starts asset metadata work immediately for N+1 through N+3, then creates
    /// player items only for the strict previous/active/next player window.
    /// Every await occurs outside the scroll-commit path.
    private func prepareWindow(
        posts: [MobileSocialPost],
        postID: UUID,
        social: MobileSocialStore
    ) async {
        guard let activeIndex = posts.firstIndex(where: { $0.id == postID }),
              let activeMedia = posts[activeIndex].media.first,
              activeMediaID == activeMedia.id else {
            return
        }

        configureRetentionWindow(posts: posts, activeIndex: activeIndex)

        for index in LegendHacPlaybackWindow.prefetchIndexes(
            activeIndex: activeIndex,
            count: posts.count
        ) {
            guard let media = posts[index].media.first else { continue }
            preloadAsset(for: media, social: social)
        }

        // Active first, then the nearest neighbors. This preserves the strict
        // three-player cap while making both swipe directions ready to commit.
        let playerIndexes = LegendHacPlaybackWindow.retainedIndexes(
            activeIndex: activeIndex,
            count: posts.count)
        let prioritizedIndexes = [activeIndex] + playerIndexes.filter { $0 != activeIndex }
        for index in prioritizedIndexes {
            guard let media = posts[index].media.first else { continue }
            await preparePlayer(for: media, social: social)
        }
    }

    private func configureRetentionWindow(
        posts: [MobileSocialPost],
        activeIndex: Int
    ) {
        retainedMediaIDs = Set(
            LegendHacPlaybackWindow.retainedIndexes(
                activeIndex: activeIndex,
                count: posts.count
            )
            .compactMap { posts[$0].media.first?.id })
        retainedAssetIDs = retainedMediaIDs.union(
            LegendHacPlaybackWindow.prefetchIndexes(
                activeIndex: activeIndex,
                count: posts.count
            )
            .compactMap { posts[$0].media.first?.id })
        releasePlayersOutsideRetainedWindow()
        releaseAssetsOutsideRetainedWindow()
    }

    func togglePlayback() {
        if isPlaying {
            pauseActivePlayback()
        } else {
            playActive()
        }
    }

    func toggleMute() {
        isMuted.toggle()
        synchronizePlayerAudibility()

        if !isMuted,
           isPlaying,
           let activeMediaID {
            _ = seedAudio(for: activeMediaID)
        }
    }

    func retry(
        posts: [MobileSocialPost],
        postID: UUID,
        social: MobileSocialStore
    ) async {
        guard let post = posts.first(where: { $0.id == postID }),
              let media = post.media.first else { return }

        releasePlayer(media.id)
        assets.removeValue(forKey: media.id)
        resourceLoaders.removeValue(forKey: media.id)
        assetPreloadTasks[media.id]?.cancel()
        assetPreloadTasks.removeValue(forKey: media.id)
        failures.removeValue(forKey: media.id)
        _ = await social.mediaStream(for: media, forceRefresh: true)
        await activate(posts: posts, postID: postID, social: social)
    }

    func stop(social: MobileSocialStore) {
        recordActivePlayback(with: social)
        pauseActivePlayback()
        if let activeMediaID {
            LegendSocialAudioSession.endPlayback(for: .hac(activeMediaID))
        }

        Array(players.keys).forEach(releasePlayer)
        players.removeAll()
        itemStatusObservations.removeAll()
        readyMediaIDs.removeAll()
        retainedMediaIDs.removeAll()
        assets.removeAll()
        resourceLoaders.removeAll()
        assetPreloadTasks.removeAll()
        retainedAssetIDs.removeAll()
        activePostID = nil
        activePost = nil
        activeMediaID = nil
        socialStore = nil
    }

    private func preparePlayer(
        for media: MobileSocialMedia,
        social: MobileSocialStore
    ) async {
        guard retainedMediaIDs.contains(media.id), players[media.id] == nil else { return }

        guard let asset = await asset(for: media, social: social) else {
            guard retainedMediaIDs.contains(media.id) else { return }
            failures[media.id] = social.mediaFailure(for: media.id) ?? UserFacingFailure(
                title: "Video unavailable",
                message: "This Hac could not be prepared for playback. Please try again.",
                correlationID: nil)
            return
        }

        guard retainedMediaIDs.contains(media.id) else { return }
        let item = AVPlayerItem(asset: asset)
        item.preferredForwardBufferDuration = 3
        let player = AVPlayer(playerItem: item)
        player.actionAtItemEnd = .none
        player.automaticallyWaitsToMinimizeStalling = false
        player.isMuted = media.id != activeMediaID || isMuted

        players[media.id] = player
        failures.removeValue(forKey: media.id)

        // AVPlayerItem construction does not mean the asset is ready for
        // playback. Drive readiness, preroll, and active playback from the
        // item's authoritative AVFoundation status transition.
        itemStatusObservations[media.id] = item.observe(
            \.status,
            options: [.new, .initial]
        ) { [weak self, weak item] observedItem, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self,
                      let item,
                      observedItem === item,
                      self.players[media.id]?.currentItem === item,
                      self.retainedMediaIDs.contains(media.id) else {
                    return
                }

                switch observedItem.status {
                case .unknown:
                    // Waiting for AVFoundation to finish preparing the item.
                    // Preroll is invalid in this state.
                    self.readyMediaIDs.remove(media.id)

                case .readyToPlay:
                    self.readyMediaIDs.insert(media.id)
                    // Seeking precisely to frame zero and prerolling at the
                    // intended rate warms the decoder/GPU before a paging
                    // gesture commits this player. Neighbors remain muted and
                    // paused until they become the active item.
                    self.preroll(player, mediaID: media.id)

                case .failed:
                    self.readyMediaIDs.remove(media.id)
                    let message = observedItem.error?.localizedDescription
                        ?? "This Hac could not be played. Please try again."
                    self.handlePlaybackFailure(
                        mediaID: media.id,
                        message: message)

                @unknown default:
                    self.readyMediaIDs.remove(media.id)
                }
            }
        }

        itemEndObservers[media.id] = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime,
            object: item,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.loopActiveMediaIfNeeded(media.id)
            }
        }
    }

    /// Warms AVURLAsset metadata before an AVPlayerItem is ever constructed.
    /// Production Hacs use the authenticated range loader below, allowing the
    /// fast-start MP4 to yield its first frame without a complete local-file
    /// download. The existing materialized-file path remains a compatibility
    /// fallback for test and offline API implementations.
    private func preloadAsset(
        for media: MobileSocialMedia,
        social: MobileSocialStore
    ) {
        guard retainedAssetIDs.contains(media.id),
              assets[media.id] == nil,
              assetPreloadTasks[media.id] == nil else {
            return
        }

        assetPreloadTasks[media.id] = Task { [weak self] in
            guard let self else { return nil }

            var asset: AVURLAsset?
            if let stream = await social.mediaStream(for: media) {
                let loader = LegendHacMediaResourceLoader(source: stream)
                let streamingAsset = AVURLAsset(url: loader.assetURL)
                // AVURLAsset does not retain its resource-loader delegate.
                // Keep the delegate in the same bounded window as this asset.
                self.resourceLoaders[media.id] = loader
                streamingAsset.resourceLoader.setDelegate(
                    loader,
                    queue: loader.callbackQueue)
                if await self.loadPlaybackMetadata(for: streamingAsset) {
                    asset = streamingAsset
                } else {
                    // Range playback is the production path. If a legacy
                    // proxy or server configuration cannot satisfy the
                    // AVFoundation resource-loader contract, use the same
                    // authenticated media source through the established
                    // bounded file resolver rather than leaving the Hac
                    // permanently unplayable.
                    self.resourceLoaders.removeValue(forKey: media.id)
                }
            }

            if asset == nil,
               let url = await social.mediaFile(for: media) {
                let fallbackAsset = AVURLAsset(url: url)
                if await self.loadPlaybackMetadata(for: fallbackAsset) {
                    asset = fallbackAsset
                }
            }

            guard let asset else {
                return nil
            }

            guard !Task.isCancelled,
                  self.retainedAssetIDs.contains(media.id) else {
                self.resourceLoaders.removeValue(forKey: media.id)
                return nil
            }

            self.assets[media.id] = asset
            return asset
        }
    }

    private func asset(
        for media: MobileSocialMedia,
        social: MobileSocialStore
    ) async -> AVURLAsset? {
        if let asset = assets[media.id] {
            return asset
        }

        preloadAsset(for: media, social: social)
        guard let task = assetPreloadTasks[media.id] else { return nil }
        return await task.value
    }

    /// The MP4 is fast-start optimized on the server, so metadata can be
    /// requested immediately and asynchronously without scanning the full
    /// source. This is AVFoundation's current typed equivalent of
    /// `loadValuesAsynchronously(forKeys:)`, without deprecated calls.
    private func loadPlaybackMetadata(for asset: AVURLAsset) async -> Bool {
        do {
            let (_, _, isPlayable) = try await asset.load(
                .tracks,
                .duration,
                .isPlayable)
            return isPlayable
        } catch {
            return false
        }
    }

    private func preroll(_ player: AVPlayer, mediaID: UUID) {
        player.seek(
            to: .zero,
            toleranceBefore: .zero,
            toleranceAfter: .zero
        ) { [weak self, weak player] finished in
            guard finished, let player else { return }
            // A rate of one exercises the same hardware decode path used by
            // playback while preserving the paused, muted neighbor state.
            player.preroll(atRate: 1) { [weak self, weak player] finished in
                guard finished else { return }
                Task { @MainActor [weak self, weak player] in
                    guard let self,
                          let player,
                          self.players[mediaID] === player,
                          self.activeMediaID == mediaID,
                          self.isPlaying else {
                        return
                    }
                    self.playActive()
                }
            }
        }
    }

    private func synchronizePlayerAudibility() {
        for (mediaID, player) in players {
            guard mediaID == activeMediaID else {
                player.pause()
                player.isMuted = true
                continue
            }
            player.isMuted = isMuted
        }
    }

    private func playActive() {
        guard let activeMediaID,
              let player = players[activeMediaID],
              let item = player.currentItem,
              item.status == .readyToPlay,
              readyMediaIDs.contains(activeMediaID) else {
            // Activation may legitimately arrive before AVFoundation has
            // prepared the item. The status observer will call playActive()
            // when this same active item reaches .readyToPlay.
            return
        }

        player.isMuted = isMuted
        if !isMuted, !seedAudio(for: activeMediaID) {
            return
        }

        player.play()
        isPlaying = true
    }

    /// Pausing between Hacs never deactivates the shared audio route. Only the
    /// coordinator's terminal `stop` releases that route.
    private func pauseActivePlayback() {
        guard let activeMediaID else { return }
        players[activeMediaID]?.pause()
        isPlaying = false
    }

    private func seedAudio(for mediaID: UUID) -> Bool {
        do {
            try LegendSocialAudioSession.beginPlayback(for: .hac(mediaID))
            return true
        } catch {
            failures[mediaID] = UserFacingFailure(
                title: "Hac audio unavailable",
                message: "Legend could not prepare audio playback for this Hac. Please try again.",
                correlationID: nil)
            return false
        }
    }

    private func loopActiveMediaIfNeeded(_ mediaID: UUID) {
        guard mediaID == activeMediaID,
              isPlaying,
              let player = players[mediaID] else { return }
        recordActivePlayback(completed: true, with: socialStore)
        player.seek(to: .zero) { [weak self] finished in
            guard finished else { return }
            Task { @MainActor [weak self] in
                guard self?.isPlaying == true else { return }
                self?.players[mediaID]?.play()
            }
        }
    }

    private func handlePlaybackFailure(mediaID: UUID, message: String) {
        players[mediaID]?.pause()
        if mediaID == activeMediaID {
            LegendSocialAudioSession.endPlayback(for: .hac(mediaID))
            isPlaying = false
        }
        failures[mediaID] = UserFacingFailure(
            title: "Video unavailable",
            message: message,
            correlationID: nil)
    }

    private func recordActivePlayback(
        completed: Bool = false,
        with social: MobileSocialStore?
    ) {
        guard let social,
              let activePost,
              let activeMediaID,
              let player = players[activeMediaID] else { return }

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

        social.recordView(
            postID: activePost.id,
            watchDurationSeconds: Decimal(watchSeconds),
            watchCompletionPercentage: completion.map { Decimal($0) })
    }

    private func releasePlayersOutsideRetainedWindow() {
        let obsoleteMediaIDs = players.keys.filter { !retainedMediaIDs.contains($0) }
        obsoleteMediaIDs.forEach(releasePlayer)
    }

    private func releaseAssetsOutsideRetainedWindow() {
        let obsoleteAssetIDs = assets.keys.filter { !retainedAssetIDs.contains($0) }
        obsoleteAssetIDs.forEach {
            assets.removeValue(forKey: $0)
            resourceLoaders.removeValue(forKey: $0)
        }

        let obsoleteTaskIDs = assetPreloadTasks.keys.filter { !retainedAssetIDs.contains($0) }
        obsoleteTaskIDs.forEach {
            assetPreloadTasks[$0]?.cancel()
            assetPreloadTasks.removeValue(forKey: $0)
            resourceLoaders.removeValue(forKey: $0)
        }
    }

    private func releasePlayer(_ mediaID: UUID) {
        players[mediaID]?.pause()
        players[mediaID]?.replaceCurrentItem(with: nil)
        players.removeValue(forKey: mediaID)
        itemStatusObservations.removeValue(forKey: mediaID)
        if let observer = itemEndObservers.removeValue(forKey: mediaID) {
            NotificationCenter.default.removeObserver(observer)
        }
        readyMediaIDs.remove(mediaID)
    }
}

/// Shared full-viewport Hac experience for the FYP and a profile's Hac grid.
/// It does not reuse the regular post card, which prevents portrait video from
/// inheriting a narrow card proposal or a second playback implementation.
struct LegendHacViewportFeed: View {
    let posts: [MobileSocialPost]
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore
    let initialPostID: UUID?
    let presentsDismissControl: Bool

    @Environment(\.dismiss) private var dismiss
    @StateObject private var playback = LegendHacPlaybackCoordinator()
    @State private var selectedPostID: UUID?
    @State private var commentTarget: MobileSocialPost?
    @State private var publicProfile: LegendPublicProfileRoute?

    init(
        posts: [MobileSocialPost],
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        initialPostID: UUID? = nil,
        presentsDismissControl: Bool = false
    ) {
        self.posts = posts
        self.currentIdentity = currentIdentity
        _social = ObservedObject(wrappedValue: social)
        self.initialPostID = initialPostID
        self.presentsDismissControl = presentsDismissControl
        _selectedPostID = State(initialValue: initialPostID ?? posts.first?.id)
    }

    var body: some View {
        Group {
            if posts.isEmpty {
                LegendNextEmptyState(
                    title: "No Hacs yet",
                    message: "Video Hacs will appear here as they are shared.",
                    systemImage: "play.rectangle.on.rectangle")
            } else {
                LegendScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(posts) { post in
                            LegendHacViewportPage(
                                post: post,
                                social: social,
                                playback: playback,
                                comment: { commentTarget = post },
                                retry: {
                                    Task {
                                        await playback.retry(
                                            posts: posts,
                                            postID: post.id,
                                            social: social)
                                    }
                                },
                                openProfile: {
                                    publicProfile = LegendPublicProfileRoute(
                                        profile: post.author,
                                        isFollowing: post.followedByCurrentActor,
                                        isFollowRequestPending: post.followRequestPending ?? false)
                                }
                            )
                            .containerRelativeFrame(.vertical)
                            .id(post.id)
                        }
                    }
                    .scrollTargetLayout()
                }
                .scrollTargetBehavior(.paging)
                .scrollPosition(id: $selectedPostID)
                .task {
                    guard let selectedPostID else { return }
                    await playback.activate(
                        posts: posts,
                        postID: selectedPostID,
                        social: social)
                }
                .onChange(of: selectedPostID) { _, postID in
                    guard let postID else { return }

                    // This is intentionally synchronous: the selected
                    // neighbor was preloaded and prerolled before the swipe,
                    // so playback begins in the same run-loop turn that paging
                    // commits rather than after an async layout/task hop.
                    playback.commitActive(
                        posts: posts,
                        postID: postID,
                        social: social)
                    Task {
                        await playback.refreshWindow(
                            posts: posts,
                            postID: postID,
                            social: social)
                    }
                }
                .onAppear {
                    selectInitialPost()
                }
                .onChange(of: posts.map(\.id)) {
                    selectInitialPost()
                }
                .onDisappear {
                    playback.stop(social: social)
                }
            }
        }
        .background(Color.black.ignoresSafeArea())
        // The full Hac canvas owns its top edge. A NavigationStack toolbar
        // would reserve an empty black strip after the permanent LEGEND banner
        // even when the old title content is gone.
        .toolbar(.hidden, for: .navigationBar)
        .overlay(alignment: .topLeading) {
            if presentsDismissControl {
                Button(action: dismiss.callAsFunction) {
                    Image(systemName: "chevron.backward")
                        .font(.headline.weight(.bold))
                        .foregroundStyle(.white)
                        .frame(width: 46, height: 46)
                        .background(.black.opacity(0.58), in: Circle())
                }
                .buttonStyle(.plain)
                .padding(.leading, LegendNextSpacing.md)
                .padding(.top, LegendNextSpacing.md)
                .accessibilityLabel("Back")
            }
        }
        .sheet(item: $commentTarget) { post in
            LegendCommentComposer(
                postID: post.id,
                social: social,
                focusesComposerOnPresentation: true,
                cancel: { commentTarget = nil })
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
        .alert(
            social.actionFailure?.title ?? "Legend update unavailable",
            isPresented: Binding(
                get: { social.actionFailure != nil },
                set: { if !$0 { social.dismissActionFailure() } }
            ),
            actions: {
                Button("OK", role: .cancel) { social.dismissActionFailure() }
            },
            message: {
                Text(social.actionFailure?.message ?? "The request could not be completed.")
            }
        )
    }

    private func selectInitialPost() {
        guard !posts.contains(where: { $0.id == selectedPostID }) else { return }
        selectedPostID = initialPostID.flatMap { requestedID in
            posts.contains(where: { $0.id == requestedID }) ? requestedID : nil
        } ?? posts.first?.id
    }
}

private struct LegendHacViewportPage: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    @ObservedObject var playback: LegendHacPlaybackCoordinator
    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    let comment: () -> Void
    let retry: () -> Void
    let openProfile: () -> Void

    @State private var showsAppreciation = false

    private var video: MobileSocialMedia? {
        post.media.first(where: \.isVideo)
    }

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color.black

                if let video,
                   let player = playback.player(for: video.id) {
                    LegendHacVideoCanvas(player: player)
                        .frame(width: proxy.size.width, height: proxy.size.height)
                } else if let video,
                          let failure = playback.failure(for: video.id) {
                    unavailableVideo(failure)
                } else {
                    LegendNextColor.midnight
                        .overlay {
                            ProgressView()
                                .tint(.white)
                                .scaleEffect(1.15)
                        }
                }

                // This is the only tap target that controls playback. It sits
                // above the video but below all interactive overlays, so liking,
                // commenting, sharing, saving, or opening a profile never pauses
                // the Hac.
                Color.clear
                    .contentShape(Rectangle())
                    .gesture(
                        TapGesture(count: 2)
                            .onEnded { appreciate() }
                            .exclusively(before: TapGesture().onEnded {
                                playback.togglePlayback()
                            })
                    )

                LinearGradient(
                    colors: [.clear, .black.opacity(0.18), .black.opacity(0.76)],
                    startPoint: .center,
                    endPoint: .bottom
                )
                .allowsHitTesting(false)

                LinearGradient(
                    colors: [.black.opacity(0.26), .clear],
                    startPoint: .trailing,
                    endPoint: .leading
                )
                .allowsHitTesting(false)

                hacOverlay(
                    bottomNavigationVisible: scrollChrome.isBottomNavigationVisible,
                    safeAreaBottom: proxy.safeAreaInsets.bottom)
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity,
                        alignment: .bottom)
                    .padding(.horizontal, LegendNextSpacing.md)
                    .padding(
                        .bottom,
                        overlayBottomInset(
                            bottomNavigationVisible: scrollChrome.isBottomNavigationVisible,
                            safeAreaBottom: proxy.safeAreaInsets.bottom))

                if showsAppreciation {
                    Image(systemName: "heart.fill")
                        .font(.system(size: 82, weight: .bold))
                        .foregroundStyle(.white)
                        .shadow(color: .black.opacity(0.32), radius: 12, y: 4)
                        .transition(.scale.combined(with: .opacity))
                        .allowsHitTesting(false)
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Hac by \(post.author.displayName)")
    }

    private func hacOverlay(
        bottomNavigationVisible: Bool,
        safeAreaBottom: CGFloat
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            HStack(alignment: .bottom, spacing: LegendNextSpacing.sm) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    Button(action: openProfile) {
                        HStack(spacing: LegendNextSpacing.xs) {
                            LegendProfileAvatar(
                                avatar: post.author.avatar,
                                displayName: post.author.displayName,
                                size: 38)
                            LegendVerifiedName(
                                post.author.displayName,
                                isVerified: post.author.isVerified == true,
                                font: .subheadline.weight(.bold),
                                badgePlacement: .alongsideProfileImage)
                                .foregroundStyle(.white)
                        }
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Open \(post.author.displayName)'s profile")

                    if !post.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text(post.body)
                            .font(.subheadline)
                            .foregroundStyle(.white)
                            .lineLimit(3)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if let music = post.music {
                        Label(
                            "\(music.trackTitle) · \(music.artistName)",
                            systemImage: "music.note")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white.opacity(0.92))
                        .lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                hacActionRail
            }

            if !bottomNavigationVisible {
                HStack(spacing: LegendNextSpacing.sm) {
                    Button(action: comment) {
                        HStack(spacing: LegendNextSpacing.xs) {
                            Image(systemName: "bubble.right")
                            Text("Add comment…")
                                .lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .font(.body.weight(.medium))
                        .foregroundStyle(.white.opacity(0.96))
                        .padding(.horizontal, LegendNextSpacing.md)
                        .frame(maxWidth: .infinity, minHeight: 48)
                        .background(.black.opacity(0.62), in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Add a comment")

                    Button {
                        social.toggleSave(postID: post.id)
                    } label: {
                        Image(systemName: post.savedByCurrentActor ? "bookmark.fill" : "bookmark")
                            .font(.system(size: 20, weight: .semibold))
                            .foregroundStyle(post.savedByCurrentActor ? LegendNextColor.gold : .white)
                            .frame(width: 48, height: 48)
                            .background(LegendNextColor.navy.opacity(0.82), in: Circle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.savedByCurrentActor ? "Remove saved Hac" : "Save Hac")
                }
            }
        }
        .padding(.bottom, safeAreaBottom == 0 ? 0 : LegendNextSpacing.micro)
    }

    private var hacActionRail: some View {
        VStack(spacing: LegendNextSpacing.sm) {
                hacAction(
                    symbol: post.reactedByCurrentActor ? "heart.fill" : "heart",
                    count: post.metrics.reactionCount,
                    title: post.reactedByCurrentActor ? "Remove appreciation" : "Appreciate",
                    tint: post.reactedByCurrentActor ? LegendNextColor.danger : .white,
                    action: appreciate)

                hacAction(
                    symbol: "bubble.right",
                    count: post.metrics.commentCount,
                    title: "Comment",
                    action: comment)

                hacAction(
                    symbol: "arrow.2.squarepath",
                    count: post.metrics.repostCount,
                    title: post.repostedByCurrentActor ? "Remove repost" : "Repost",
                    tint: post.repostedByCurrentActor ? LegendNextColor.information : .white,
                    action: { social.toggleRepost(postID: post.id) })

                LegendGlobalShareButton(
                    post: post,
                    social: social,
                    accessibilityLabel: "Share Hac"
                ) {
                    hacActionLabel(
                        symbol: "paperplane",
                        count: post.metrics.shareCount,
                        tint: .white)
                }

                hacAction(
                    symbol: playback.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                    count: nil,
                    title: playback.isMuted ? "Unmute Hac" : "Mute Hac",
                    action: playback.toggleMute)
        }
    }

    private func overlayBottomInset(
        bottomNavigationVisible: Bool,
        safeAreaBottom: CGFloat
    ) -> CGFloat {
        // The permanent app tab bar is visually over the Hac canvas. Keep the
        // creator and action rail above it; when it hides on a downward scroll,
        // the compact comment composer can take the true bottom position.
        bottomNavigationVisible
            ? 104 + safeAreaBottom
            : max(LegendNextSpacing.sm, safeAreaBottom)
    }

    private func hacAction(
        symbol: String,
        count: Int?,
        title: String,
        tint: Color = .white,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            hacActionLabel(symbol: symbol, count: count, tint: tint)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
    }

    private func hacActionLabel(
        symbol: String,
        count: Int?,
        tint: Color
    ) -> some View {
        VStack(spacing: 3) {
            Image(systemName: symbol)
                .font(.system(size: 20, weight: .semibold))
                .frame(width: 42, height: 42)
                .background(LegendNextColor.navy.opacity(0.72), in: Circle())
            if let count, count > 0 {
                Text(count.formatted())
                    .font(.caption2.weight(.bold))
                    .monospacedDigit()
            }
        }
        .foregroundStyle(tint)
    }

    private func appreciate() {
        social.toggleReaction(postID: post.id)
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        withAnimation(.spring(response: 0.24, dampingFraction: 0.62)) {
            showsAppreciation = true
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.62) {
            withAnimation(.easeOut(duration: 0.16)) {
                showsAppreciation = false
            }
        }
    }

    private func unavailableVideo(_ failure: UserFacingFailure) -> some View {
        VStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: "video.slash")
                .font(.title2.weight(.semibold))
            Text(failure.message)
                .font(LegendNextTypography.supporting)
                .multilineTextAlignment(.center)
            Button("Retry video", action: retry)
            .buttonStyle(LegendNextButtonStyle(kind: .secondary))
        }
        .foregroundStyle(.white)
        .padding(LegendNextSpacing.lg)
    }
}

private final class LegendHacPlayerLayerView: UIView {
    override class var layerClass: AnyClass { AVPlayerLayer.self }

    var playerLayer: AVPlayerLayer {
        guard let layer = layer as? AVPlayerLayer else {
            fatalError("Legend Hac player requires AVPlayerLayer")
        }
        return layer
    }
}

/// AVPlayerLayer is used instead of `VideoPlayer` for Hacs so the canvas owns
/// the complete viewport with `.resizeAspectFill`; the native player controls
/// cannot constrain portrait video to the leading side of a card.
private struct LegendHacVideoCanvas: UIViewRepresentable {
    let player: AVPlayer

    func makeUIView(context: Context) -> LegendHacPlayerLayerView {
        let view = LegendHacPlayerLayerView()
        view.backgroundColor = .black
        view.playerLayer.videoGravity = .resizeAspectFill
        view.playerLayer.player = player
        return view
    }

    func updateUIView(_ view: LegendHacPlayerLayerView, context: Context) {
        view.playerLayer.videoGravity = .resizeAspectFill
        view.playerLayer.player = player
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
    @State private var isMuted = false
    @State private var mostRecentRecordedWatchSeconds = 0.0
    @State private var playbackFailure: UserFacingFailure?
    @State private var playerStatusObservation: NSKeyValueObservation?

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
                    if let playbackFailure {
                        videoUnavailable(presentation: playbackFailure)
                    } else if let player {
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

                if playbackFailure == nil {
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
        .frame(maxWidth: .infinity)
        .task(id: media.id) {
            await loadPlayer()
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
        .onReceive(NotificationCenter.default.publisher(
            for: LegendSocialAudioSession.activeHacDidChange
        )) { notification in
            guard let activeMediaID = notification.userInfo?["mediaID"] as? String,
                  activeMediaID != media.id.uuidString else { return }
            player?.pause()
            isPlaying = false
        }
        .onDisappear {
            recordPlaybackMetrics(completed: false)
            player?.pause()
            isPlaying = false
            playerStatusObservation = nil
            LegendSocialAudioSession.endPlayback(for: .hac(media.id))
        }
        .accessibilityLabel(media.accessibilityText ?? "Shared video")
    }

    private func togglePlayback() {
        guard let player else { return }
        if isPlaying {
            recordPlaybackMetrics(completed: false)
            player.pause()
            LegendSocialAudioSession.endPlayback(for: .hac(media.id))
        } else {
            do {
                try LegendSocialAudioSession.beginPlayback(for: .hac(media.id))
            } catch {
                playbackFailure = UserFacingFailure(
                    title: "Hac audio unavailable",
                    message: "Legend could not prepare audio playback for this Hac. Please try again.",
                    correlationID: nil)
                return
            }
            player.play()
        }
        isPlaying.toggle()
    }

    private func loadPlayer(forceRefresh: Bool = false) async {
        playbackFailure = nil
        playerStatusObservation = nil
        player?.pause()
        LegendSocialAudioSession.endPlayback(for: .hac(media.id))

        guard let url = await social.mediaFile(
            for: media,
            forceRefresh: forceRefresh) else {
            playbackFailure = social.mediaFailure(for: media.id) ?? UserFacingFailure(
                title: "Video unavailable",
                message: "This video could not be prepared for playback. Please try again.",
                correlationID: nil)
            return
        }

        do {
            try LegendSocialAudioSession.beginPlayback(for: .hac(media.id))
        } catch {
            playbackFailure = UserFacingFailure(
                title: "Hac audio unavailable",
                message: "Legend could not prepare audio playback for this Hac. Please try again.",
                correlationID: nil)
            return
        }

        let item = AVPlayerItem(url: url)
        playerStatusObservation = item.observe(
            \.status,
            options: [.new, .initial]
        ) { observedItem, _ in
            guard observedItem.status == .failed else { return }
            let message = observedItem.error?.localizedDescription
                ?? "This video could not be played. Please try again."
            Task { @MainActor in
                player?.pause()
                isPlaying = false
                LegendSocialAudioSession.endPlayback(for: .hac(media.id))
                playbackFailure = UserFacingFailure(
                    title: "Video unavailable",
                    message: message,
                    correlationID: nil)
            }
        }

        let created = AVPlayer(playerItem: item)
        created.isMuted = isMuted
        player = created
        created.play()
        isPlaying = true
    }

    private func videoUnavailable(
        presentation: UserFacingFailure
    ) -> some View {
        VStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: "video.slash")
                .font(.title2.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)
            Text(presentation.message)
                .font(.caption)
                .foregroundStyle(LegendNextColor.textSecondary)
                .multilineTextAlignment(.center)
            Button("Retry video") {
                Task { await loadPlayer(forceRefresh: true) }
            }
            .font(.caption.weight(.bold))
            .foregroundStyle(LegendNextColor.royal)
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
}

/// The one still-image renderer for published Hacs outside the fullscreen
/// player. A Hac's creator-selected JPEG is delivered by the protected social
/// media API, so home and profile surfaces do not spin up their own AVPlayer
/// just to render a preview.
struct LegendSocialVideoPoster: View {
    let media: MobileSocialMedia
    @ObservedObject var social: MobileSocialStore
    let contentMode: ContentMode
    let usesRoundedCorners: Bool

    @State private var poster: UIImage?
    @State private var finishedLoading = false

    init(
        media: MobileSocialMedia,
        social: MobileSocialStore,
        contentMode: ContentMode = .fill,
        usesRoundedCorners: Bool = true
    ) {
        self.media = media
        _social = ObservedObject(wrappedValue: social)
        self.contentMode = contentMode
        self.usesRoundedCorners = usesRoundedCorners
    }

    var body: some View {
        Group {
            if let poster {
                Image(uiImage: poster)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            } else {
                ZStack {
                    LinearGradient(
                        colors: [
                            LegendNextColor.navy,
                            LegendNextColor.midnight
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing)

                    if !finishedLoading && media.hasPreviewImage {
                        ProgressView()
                            .tint(.white)
                    } else {
                        Image(systemName: "play.rectangle.fill")
                            .font(.title2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.goldBright)
                    }
                }
            }
        }
        .clipShape(
            RoundedRectangle(
                cornerRadius: usesRoundedCorners ? LegendNextRadius.control : 0,
                style: .continuous))
        .frame(maxWidth: .infinity)
        .task(id: media.id) {
            poster = nil
            finishedLoading = false
            guard media.hasPreviewImage,
                  let data = await social.previewData(for: media.id),
                  let image = UIImage(data: data) else {
                finishedLoading = true
                return
            }
            poster = image
            finishedLoading = true
        }
        .accessibilityLabel(media.accessibilityText ?? "Hac preview")
    }
}

private enum ImageState {
    case loading
    case loaded(UIImage)
    case unavailable
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
    private let focusesComposerOnPresentation: Bool

    @FocusState private var composerFocused: Bool
    @State private var draft = ""
    @State private var replyTarget: MobileSocialComment?
    @State private var selectedDetent: PresentationDetent = .medium

    init(
        postID: UUID,
        social: MobileSocialStore,
        focusesComposerOnPresentation: Bool = false,
        cancel: @escaping () -> Void
    ) {
        self.postID = postID
        _social = ObservedObject(wrappedValue: social)
        self.focusesComposerOnPresentation = focusesComposerOnPresentation
        self.cancel = cancel
        _selectedDetent = State(initialValue: focusesComposerOnPresentation ? .large : .medium)
    }

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
        .onAppear {
            guard focusesComposerOnPresentation else { return }
            composerFocused = true
        }
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
                        font: .subheadline.weight(.bold),
                        badgePlacement: .alongsideProfileImage
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
