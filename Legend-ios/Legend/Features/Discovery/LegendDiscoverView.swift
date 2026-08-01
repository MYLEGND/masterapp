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
    @FocusState private var searchIsFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            discoverControls
            content
        }
            .background(LegendNextColor.midnight.ignoresSafeArea())
            .toolbar(.hidden, for: .navigationBar)
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
                    isFollowRequestPending: route.isFollowRequestPending,
                    journeyConnectionID: route.journeyConnectionID,
                    disconnectConnection: { connectionID in
                        await journeyCircles.disconnectConnectionConfirmed(id: connectionID)
                    })
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

    private var hasJourneyProfile: Bool {
        if case .loaded(let dashboard) = journeyCircles.state {
            return dashboard.profile != nil
        }
        return false
    }

    private var discoverControls: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            HStack(spacing: LegendNextSpacing.xs) {
                Image(systemName: "magnifyingglass")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(
                        searchIsFocused
                            ? LegendNextColor.goldBright
                            : .white.opacity(0.70)
                    )

                TextField(searchPrompt, text: $store.searchText)
                    .font(.body)
                    .foregroundStyle(.white)
                    .focused($searchIsFocused)

                if store.isSearching {
                    ProgressView()
                        .controlSize(.small)
                        .tint(LegendNextColor.goldBright)
                } else if !store.searchText.isEmpty {
                    Button {
                        store.searchText = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.white.opacity(0.68))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Clear search")
                }
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .frame(minHeight: LegendNextSize.controlHeight)
            .frame(maxWidth: .infinity)
            .background(LegendNextColor.navy, in: Capsule())
            .overlay {
                Capsule()
                    .strokeBorder(LegendNextColor.navyElevated.opacity(0.66), lineWidth: 1)
            }

            if currentSession.actor.identity.participantType == .client {
                Button {
                    isEditingJourneyProfile = true
                } label: {
                    Image(systemName: hasJourneyProfile
                        ? "line.3.horizontal.decrease.circle"
                        : "person.crop.circle.badge.plus")
                        .font(.system(size: 17, weight: .semibold))
                        .foregroundStyle(LegendNextColor.goldBright)
                        .frame(
                            width: LegendNextSize.controlHeight,
                            height: LegendNextSize.controlHeight)
                        .background(LegendNextColor.navy, in: Circle())
                        .overlay {
                            Circle().strokeBorder(
                                LegendNextColor.navyElevated.opacity(0.66),
                                lineWidth: 1)
                        }
                }
                .buttonStyle(.plain)
                .accessibilityLabel(hasJourneyProfile
                    ? "Manage your Discover profile"
                    : "Set up your Discover profile")
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.micro)
        .padding(.bottom, LegendNextSpacing.xs)
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
            LegendScrollView {
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
            open: { publicProfile = publicRoute(for: result) })
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
                isPrivate: result.isPrivate,
                isVerified: result.isVerified,
                roleLabel: result.identity.participantType == .agent
                    ? result.roleLabel
                    : nil,
                publicPhone: result.publicPhone),
            isFollowing: result.relationship.followedByCurrentActor,
            isFollowRequestPending: result.relationship.followRequestPending ?? false)
    }
}

/// One compact directory row. Relationship changes happen from the opened profile,
/// so this surface stays focused on discovery and never owns a second control path.
private struct LegendDiscoverResultCard: View {
    let result: MobileDiscoveryResult
    let open: () -> Void

    var body: some View {
        LegendContactCard(
            displayName: result.displayName,
            nameStatus: result.relationship.connectionStatus == .accepted
                ? "Connected"
                : nil,
            subtitle: subtitle,
            detail: detail,
            isVerified: result.isVerified == true,
            avatar: {
                LegendProfileAvatar(
                    avatar: result.avatar,
                    displayName: result.displayName,
                    size: 46)
            },
            action: {
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.contactAction)
            }
        )
        .onTapGesture(perform: open)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Open \(result.displayName)'s profile")
    }

    private var subtitle: String? {
        if result.identity.participantType == .agent {
            return result.roleLabel
        }

        guard let username = result.username?.trimmingCharacters(in: .whitespacesAndNewlines),
              !username.isEmpty else {
            return nil
        }
        return "@\(username)"
    }

    private var detail: String? {
        if result.relationship.followsCurrentActor {
            return result.relationship.followedByCurrentActor
                ? "You follow each other"
                : "Follows you"
        }
        return result.identity.participantType == .agent
            ? nil
            : result.supportingLine
    }
}
