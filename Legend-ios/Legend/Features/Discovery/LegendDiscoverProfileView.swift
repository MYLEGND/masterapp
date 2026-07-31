import SwiftUI

/// The profile a Discover result opens into.
///
/// Reachability is re-checked on the server for this specific profile, so a member
/// outside the caller's scope cannot be opened even with a known identifier. Content
/// counts come from the social authority and are shown only when that authority says
/// this viewer may see them.
struct LegendDiscoverProfileView: View {
    let clientProfileID: UUID
    @ObservedObject var store: MobileDiscoveryStore

    @State private var profile: MobileDiscoveryProfile?
    @State private var loadFailed = false

    var body: some View {
        Group {
            if let profile {
                loaded(profile)
            } else if loadFailed {
                LegendEmptyState(
                    title: "Profile unavailable",
                    message: "This Legend member is not available from your Discover scope.",
                    symbolName: "person.crop.circle.badge.exclamationmark")
            } else {
                LegendLoadingView("Loading profile…")
            }
        }
        .background(LegendNextColor.canvas.ignoresSafeArea())
        .navigationTitle(profile?.summary.displayName ?? "Profile")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: clientProfileID) { await load() }
    }

    private func load() async {
        loadFailed = false
        let loaded = await store.profile(for: clientProfileID)
        if let loaded {
            profile = loaded
        } else {
            loadFailed = true
        }
    }

    private func loaded(_ profile: MobileDiscoveryProfile) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                identityHeader(profile)

                if profile.contentVisibleToCurrentActor {
                    statsRow(profile)
                } else {
                    // A discoverable member whose posts are not visible to this viewer
                    // is a normal state, not an error. Say so instead of showing zeros.
                    Label(
                        "This member's Legend activity becomes visible once you are connected.",
                        systemImage: "lock")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                if let introduction = profile.introduction, !introduction.isEmpty {
                    section("About") {
                        Text(introduction)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                tagSection("Goals", values: profile.summary.goals)
                tagSection("Interests", values: profile.summary.interests)
                tagSection("Journey Circles", values: profile.summary.circleCodes)
                tagSection("Life stage", values: profile.lifeStages)
                tagSection("Looking for", values: profile.connectionTypes)
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xl)
        }
    }

    private func identityHeader(_ profile: MobileDiscoveryProfile) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                LegendProfileAvatar(
                    avatar: profile.summary.avatar,
                    displayName: profile.summary.displayName,
                    size: 72)

                VStack(alignment: .leading, spacing: 3) {
                    Text(profile.summary.displayName)
                        .font(.title3.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)

                    if let location = profile.summary.location, !location.isEmpty {
                        Label(location, systemImage: "mappin.and.ellipse")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    if profile.summary.relationship.followsCurrentActor {
                        Text("Follows you")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.gold)
                    }
                }

                Spacer(minLength: 0)
            }

            if let explanation = profile.summary.matchExplanation, !explanation.isEmpty {
                Text(explanation)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            actionRow(profile)
        }
        .padding(.top, LegendNextSpacing.xs)
    }

    private func actionRow(_ profile: MobileDiscoveryProfile) -> some View {
        let isBusy = store.pendingRelationshipProfileIDs.contains(profile.summary.id)
        return HStack(spacing: LegendNextSpacing.xs) {
            if profile.summary.relationship.canFollow {
                Button {
                    store.toggleFollow(profile.summary)
                    Task { await load() }
                } label: {
                    Label(
                        profile.summary.relationship.followedByCurrentActor
                            ? "Following"
                            : "Follow",
                        systemImage: profile.summary.relationship.followedByCurrentActor
                            ? "checkmark"
                            : "plus")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendInlineButtonStyle(
                    kind: profile.summary.relationship.followedByCurrentActor
                        ? .secondary
                        : .primary))
                .disabled(isBusy)
            }

            if profile.summary.relationship.canRequestConnection {
                Button {
                    store.requestConnection(profile.summary)
                    Task { await load() }
                } label: {
                    Label("Connect", systemImage: "person.badge.plus")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendInlineButtonStyle(kind: .secondary))
                .disabled(isBusy)
            }
        }
    }

    private func statsRow(_ profile: MobileDiscoveryProfile) -> some View {
        HStack(spacing: 0) {
            stat("Posts", profile.postCount)
            stat("Reels", profile.reelCount)
            stat("Stories", profile.storyCount)
            stat("Followers", profile.followerCount)
            stat("Following", profile.followingCount)
        }
        .padding(.vertical, LegendNextSpacing.xs)
        .background(
            LegendNextColor.surfaceInset,
            in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
    }

    private func stat(_ title: String, _ value: Int) -> some View {
        VStack(spacing: 2) {
            Text("\(value)")
                .font(.subheadline.weight(.bold).monospacedDigit())
                .foregroundStyle(LegendNextColor.textPrimary)
            Text(title)
                .font(.caption2)
                .foregroundStyle(LegendNextColor.textSecondary)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(value) \(title)")
    }

    @ViewBuilder
    private func tagSection(_ title: String, values: [String]) -> some View {
        if !values.isEmpty {
            section(title) {
                LegendDiscoverTagCloud(values: values)
            }
        }
    }

    private func section<Content: View>(
        _ title: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            LegendNextSectionHeader(title: title)
            content()
        }
    }
}

private struct LegendDiscoverTagCloud: View {
    let values: [String]

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(values, id: \.self) { value in
                    Text(value)
                        .font(.caption.weight(.medium))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(LegendNextColor.surfaceInset, in: Capsule())
                }
            }
        }
    }
}
