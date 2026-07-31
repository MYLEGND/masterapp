import SwiftUI

/// The Legend Discover surface.
///
/// Every result comes from the centralized discovery endpoint. Typing performs a
/// debounced server-side search across the whole directory; scrolling pages more in.
/// Nothing is filtered or ranked on the device, so what appears here is exactly what
/// the server authorized.
struct LegendDiscoverView: View {
    let currentSession: MobileSession
    @ObservedObject var store: MobileDiscoveryStore
    @ObservedObject var journeyCircles: MobileJourneyCirclesStore
    @ObservedObject var social: MobileSocialStore

    @State private var isEditingJourneyProfile = false
    @State private var publicProfile: LegendPublicProfileRoute?

    var body: some View {
        content
            .background(LegendNextColor.midnight.ignoresSafeArea())
            .navigationTitle("Discover")
            .navigationBarTitleDisplayMode(.inline)
            .toolbarBackground(LegendNextColor.midnight, for: .navigationBar)
            .toolbarBackground(.visible, for: .navigationBar)
            .toolbarColorScheme(.dark, for: .navigationBar)
            .toolbar { toolbarContent }
            .searchable(
                text: $store.searchText,
                placement: .navigationBarDrawer(displayMode: .always),
                prompt: searchPrompt)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            .refreshable { await store.refresh() }
            .task { store.load() }
            .preferredColorScheme(.dark)
            .sheet(isPresented: $isEditingJourneyProfile) {
                if case .loaded(let dashboard) = journeyCircles.state {
                    LegendJourneyProfileEditor(dashboard: dashboard, store: journeyCircles)
                }
            }
            .navigationDestination(item: $publicProfile) { route in
                LegendPublicProfileView(
                    profile: route.profile,
                    currentIdentity: currentSession.actor.identity,
                    social: social,
                    isFollowing: route.isFollowing,
                    isFollowRequestPending: route.isFollowRequestPending)
            }
            .alert(
                store.actionFailure?.title ?? "Discover unavailable",
                isPresented: Binding(
                    get: { store.actionFailure != nil },
                    set: { if !$0 { store.dismissActionFailure() } }),
                actions: {
                    Button("OK", role: .cancel) { store.dismissActionFailure() }
                },
                message: {
                    Text(store.actionFailure?.message ?? "The request could not be completed.")
                })
    }

    private var searchPrompt: String {
        store.scope == .ownedClients
            ? "Search clients and agents"
            : "Search people, goals, interests"
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        // The Journey Circles profile controls a client's own discoverability, so the
        // entry point belongs here. It is meaningless in the agent scope.
        if currentSession.actor.identity.participantType == .client {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    isEditingJourneyProfile = true
                } label: {
                    Image(systemName: hasJourneyProfile
                        ? "line.3.horizontal.decrease.circle"
                        : "person.crop.circle.badge.plus")
                        .font(.system(size: 17, weight: .semibold))
                }
                .accessibilityLabel(hasJourneyProfile
                    ? "Manage your Discover profile"
                    : "Set up your Discover profile")
            }
        }
    }

    private var hasJourneyProfile: Bool {
        if case .loaded(let dashboard) = journeyCircles.state {
            return dashboard.profile != nil
        }
        return false
    }

    @ViewBuilder
    private var content: some View {
        switch store.state {
        case .idle, .loading:
            LegendScreenSkeleton(accessibilityMessage: "Loading Discover") {
                LegendListSkeleton(rows: 7)
            }

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: { Task { await store.refresh() } })
                .padding(LegendNextSpacing.sm)

        case .loaded(let results):
            resultsList(results)
        }
    }

    @ViewBuilder
    private func resultsList(_ results: [MobileDiscoveryResult]) -> some View {
        if results.isEmpty && store.recommendations.isEmpty {
            emptyState
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    header

                    if !store.recommendations.isEmpty {
                        directorySectionHeader("Recommended for you", detail: "Selected for your Legend")
                        ForEach(store.recommendations) { result in
                            resultCard(result, loadsMore: false)
                        }
                    }

                    if !directoryResults.isEmpty {
                        if store.scope == .ownedClients {
                            let clients = directoryResults.filter {
                                $0.identity.participantType == .client
                            }
                            let agents = directoryResults.filter {
                                $0.identity.participantType == .agent
                            }

                            if !clients.isEmpty {
                                directorySectionHeader("Your clients", detail: nil)
                                ForEach(clients) { result in
                                    resultCard(result, loadsMore: true)
                                }
                            }

                            if !agents.isEmpty {
                                directorySectionHeader("Legend agents", detail: "Search and follow your professional peers")
                                ForEach(agents) { result in
                                    resultCard(result, loadsMore: true)
                                }
                            }
                        } else {
                            directorySectionHeader("Explore Legend", detail: "Active member and agent profiles")
                            ForEach(directoryResults) { result in
                                resultCard(result, loadsMore: true)
                            }
                        }
                    }

                    if store.isLoadingMore {
                        LegendListSkeleton(rows: 2)
                            .padding(.top, LegendNextSpacing.xs)
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .scrollDismissesKeyboard(.interactively)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            HStack(alignment: .firstTextBaseline) {
                Text(resultsSummary)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)

                Spacer(minLength: LegendNextSpacing.xs)

                if store.isSearching {
                    // A quiet, non-spinning hint: results are already on screen and
                    // are being replaced, not awaited.
                    Text("Updating…")
                        .font(.caption2)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .transition(.opacity)
                }
            }

        }
        .padding(.top, LegendNextSpacing.xs)
        .padding(.bottom, LegendNextSpacing.micro)
    }

    private var resultsSummary: String {
        let count = store.totalCount
        let noun = count == 1 ? "member" : "members"
        if !store.searchText.isEmpty {
            return "\(count) \(noun) matching your search"
        }
        return store.scope == .ownedClients
            ? "\(count) \(noun) across your clients and Legend agents"
            : "\(count) \(noun) in your Legend community"
    }

    private var emptyState: some View {
        LegendNextEmptyState(
            title: store.searchText.isEmpty ? "No members yet" : "No members found",
            message: emptyMessage,
            systemImage: store.searchText.isEmpty ? "person.3" : "magnifyingglass")
    }

    private var emptyMessage: String {
        if !store.searchText.isEmpty {
            return "Try another name, goal, interest, or location."
        }
        return store.scope == .ownedClients
            ? "Your clients and active Legend agents will appear here."
            : "Active Legend members and agents will appear here."
    }

    private var directoryResults: [MobileDiscoveryResult] {
        let recommendationIDs = Set(store.recommendations.map(\.id))
        return store.results.filter { !recommendationIDs.contains($0.id) }
    }

    private func directorySectionHeader(_ title: String, detail: String?) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.subheadline.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)

            if let detail {
                Text(detail)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
        }
        .padding(.top, LegendNextSpacing.sm)
        .padding(.bottom, LegendNextSpacing.micro)
    }

    private func resultCard(
        _ result: MobileDiscoveryResult,
        loadsMore: Bool
    ) -> some View {
        LegendDiscoverResultCard(
            result: result,
            isBusy: store.pendingRelationshipProfileIDs.contains(result.id),
            open: { publicProfile = publicRoute(for: result) },
            toggleFollow: { store.toggleFollow(result) },
            requestConnection: { store.requestConnection(result) })
            .onAppear {
                if loadsMore {
                    store.loadMoreIfNeeded(currentItem: result)
                }
            }
    }

    private func publicRoute(
        for result: MobileDiscoveryResult
    ) -> LegendPublicProfileRoute {
        LegendPublicProfileRoute(
            profile: MobileSocialAuthor(
                identity: result.identity,
                profileID: result.clientProfileID.uuidString,
                displayName: result.displayName,
                avatar: result.avatar,
                username: result.username,
                bio: result.bio,
                website: result.website,
                location: result.location,
                publicEmail: result.publicEmail,
                isPrivate: result.isPrivate),
            isFollowing: result.relationship.followedByCurrentActor,
            isFollowRequestPending: result.relationship.followRequestPending ?? false)
    }
}

/// One directory row: identity, why they are relevant, and the two relationship
/// actions Legend supports (a one-way follow and a two-way connection request).
private struct LegendDiscoverResultCard: View {
    let result: MobileDiscoveryResult
    let isBusy: Bool
    let open: () -> Void
    let toggleFollow: () -> Void
    let requestConnection: () -> Void

    var body: some View {
        LegendNextSurface {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                Button(action: open) {
                    HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                        LegendProfileAvatar(
                            avatar: result.avatar,
                            displayName: result.displayName,
                            size: 52)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(result.displayName)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(LegendNextColor.textPrimary)
                                .lineLimit(1)

                            if let username = result.username, !username.isEmpty {
                                Text("@\(username)")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(LegendNextColor.gold)
                            }

                            if result.identity.participantType == .agent {
                                Text("Legend Agent")
                                    .font(.caption2.weight(.bold))
                                    .foregroundStyle(LegendNextColor.gold)
                            }

                            if let supporting = result.supportingLine {
                                Text(supporting)
                                    .font(LegendNextTypography.supporting)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .lineLimit(2)
                                    .fixedSize(horizontal: false, vertical: true)
                            }

                            if let location = result.location,
                               !location.isEmpty,
                               result.supportingLine != location {
                                Label(location, systemImage: "mappin.and.ellipse")
                                    .font(.caption)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .lineLimit(1)
                            }

                            relationshipCaption
                        }

                        Spacer(minLength: LegendNextSpacing.xs)

                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textSecondary.opacity(0.5))
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Open \(result.displayName)'s profile")

                if !highlights.isEmpty {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(highlights, id: \.self) { highlight in
                                Text(highlight)
                                    .font(.caption.weight(.medium))
                                    .padding(.horizontal, 9)
                                    .padding(.vertical, 5)
                                    .background(LegendNextColor.surfaceInset, in: Capsule())
                            }
                        }
                    }
                }

                actionRow
            }
        }
    }

    @ViewBuilder
    private var relationshipCaption: some View {
        if result.relationship.followsCurrentActor {
            Text(result.relationship.followedByCurrentActor
                ? "You follow each other"
                : "Follows you")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(LegendNextColor.gold)
        }
    }

    private var highlights: [String] {
        Array((result.goals + result.interests + result.circleCodes).prefix(3))
    }

    @ViewBuilder
    private var actionRow: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Spacer(minLength: 0)

            if result.relationship.canFollow {
                Button(action: toggleFollow) {
                    Label(
                        followTitle,
                        systemImage: result.relationship.followedByCurrentActor
                            ? "checkmark"
                            : result.relationship.followRequestPending == true ? "clock" : "plus")
                }
                .buttonStyle(LegendNextButtonStyle(
                    kind: result.relationship.followedByCurrentActor || result.relationship.followRequestPending == true ? .secondary : .primary,
                    isFullWidth: false,
                    controlHeight: 34))
                .disabled(isBusy)
                .accessibilityLabel(result.relationship.followedByCurrentActor
                    ? "Unfollow \(result.displayName)"
                    : result.relationship.followRequestPending == true
                        ? "Cancel your follow request to \(result.displayName)"
                        : "Follow \(result.displayName)")
            }

            connectionControl
        }
    }

    private var followTitle: String {
        if result.relationship.followedByCurrentActor { return "Following" }
        if result.relationship.followRequestPending == true { return "Requested" }
        return result.isPrivate == true ? "Request" : "Follow"
    }

    @ViewBuilder
    private var connectionControl: some View {
        switch result.relationship.connectionStatus {
        case .none where result.relationship.canRequestConnection:
            Button(action: requestConnection) {
                Label("Connect", systemImage: "person.badge.plus")
            }
            .buttonStyle(LegendNextButtonStyle(
                kind: .secondary,
                isFullWidth: false,
                controlHeight: 34))
            .disabled(isBusy)
            .accessibilityLabel("Request a connection with \(result.displayName)")

        case .pending:
            Label("Request sent", systemImage: "clock")
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)

        case .accepted:
            Label("Connected", systemImage: "checkmark.seal.fill")
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.success)

        default:
            EmptyView()
        }
    }
}
