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

    @State private var isEditingJourneyProfile = false
    @State private var openProfileID: UUID?

    var body: some View {
        content
            .background(LegendNextColor.canvas.ignoresSafeArea())
            .navigationTitle("Discover")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { toolbarContent }
            .searchable(
                text: $store.searchText,
                placement: .navigationBarDrawer(displayMode: .always),
                prompt: searchPrompt)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            .refreshable { await store.refresh() }
            .task { store.load() }
            .sheet(isPresented: $isEditingJourneyProfile) {
                if case .loaded(let dashboard) = journeyCircles.state {
                    LegendJourneyProfileEditor(dashboard: dashboard, store: journeyCircles)
                }
            }
            .navigationDestination(item: $openProfileID) { profileID in
                LegendDiscoverProfileView(
                    clientProfileID: profileID,
                    store: store)
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
            ? "Search your clients"
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
            LegendErrorCard(
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
        if results.isEmpty {
            emptyState
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    header

                    ForEach(results) { result in
                        LegendDiscoverResultCard(
                            result: result,
                            isBusy: store.pendingRelationshipProfileIDs.contains(result.id),
                            open: { openProfileID = result.id },
                            toggleFollow: { store.toggleFollow(result) },
                            requestConnection: { store.requestConnection(result) })
                            .onAppear { store.loadMoreIfNeeded(currentItem: result) }
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

            // Compatibility ordering is a convenience, not a gate. This makes the full
            // directory reachable in one tap.
            if store.scope == .community && store.searchText.isEmpty {
                Button {
                    store.showDirectory(!store.isBrowsingEveryone)
                } label: {
                    Label(
                        store.isBrowsingEveryone
                            ? "Show suggested first"
                            : "Browse everyone A–Z",
                        systemImage: store.isBrowsingEveryone
                            ? "sparkles"
                            : "list.bullet")
                        .font(.caption.weight(.semibold))
                }
                .buttonStyle(.plain)
                .foregroundStyle(LegendNextColor.gold)
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
            ? "\(count) \(noun) in your book of business"
            : "\(count) \(noun) in your Legend community"
    }

    private var emptyState: some View {
        LegendEmptyState(
            title: store.searchText.isEmpty ? "No members yet" : "No members found",
            message: emptyMessage,
            symbolName: store.searchText.isEmpty ? "person.3" : "magnifyingglass")
    }

    private var emptyMessage: String {
        if !store.searchText.isEmpty {
            return "Try another name, goal, interest, or location."
        }
        return store.scope == .ownedClients
            ? "Clients you own will appear here."
            : "Members appear here once they join the Legend community and make themselves discoverable."
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
            if result.relationship.canFollow {
                Button(action: toggleFollow) {
                    Label(
                        result.relationship.followedByCurrentActor ? "Following" : "Follow",
                        systemImage: result.relationship.followedByCurrentActor
                            ? "checkmark"
                            : "plus")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendInlineButtonStyle(
                    kind: result.relationship.followedByCurrentActor ? .secondary : .primary))
                .disabled(isBusy)
                .accessibilityLabel(result.relationship.followedByCurrentActor
                    ? "Unfollow \(result.displayName)"
                    : "Follow \(result.displayName)")
            }

            connectionControl
        }
    }

    @ViewBuilder
    private var connectionControl: some View {
        switch result.relationship.connectionStatus {
        case .none where result.relationship.canRequestConnection:
            Button(action: requestConnection) {
                Label("Connect", systemImage: "person.badge.plus")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(LegendInlineButtonStyle(kind: .secondary))
            .disabled(isBusy)
            .accessibilityLabel("Request a connection with \(result.displayName)")

        case .pending:
            Label("Request sent", systemImage: "clock")
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)
                .frame(maxWidth: .infinity)

        case .accepted:
            Label("Connected", systemImage: "checkmark.seal.fill")
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.success)
                .frame(maxWidth: .infinity)

        default:
            EmptyView()
        }
    }
}
