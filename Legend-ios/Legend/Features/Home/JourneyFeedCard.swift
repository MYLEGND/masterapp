import SwiftUI

struct JourneyFeedView: View {
    let posts: [LegendJourneyPost]
    let onCreate: () -> Void
    let onOpenDiscussion: (LegendJourneyPost) -> Void

    @State private var selectedFilter:
        LegendHomeFeedFilter = .forYou

    private var visiblePosts: [LegendJourneyPost] {
        switch selectedFilter {
        case .forYou:
            return posts.filter {
                $0.filter == .forYou
                    || $0.filter == .circles
                    || $0.filter == .guidance
            }

        case .circles:
            return posts.filter {
                $0.filter == .circles
            }

        case .guidance:
            return posts.filter {
                $0.filter == .guidance
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.md) {
            filterControl

            if visiblePosts.isEmpty {
                LegendEmptyState(
                    title: "Nothing here yet",
                    message:
                        "Updates connected to this part of your journey will appear here.",
                    symbolName: "sparkles"
                )
            } else {
                LazyVStack(spacing: LegendSpacing.md) {
                    ForEach(visiblePosts) { post in
                        JourneyFeedCard(
                            post: post,
                            onCelebrate: {
                                celebrate(post)
                            },
                            onDiscuss: {
                                onOpenDiscussion(post)
                            },
                            onSave: {
                                save(post)
                            }
                        )
                    }
                }
            }
        }
    }

    private var filterControl: some View {
        HStack(spacing: LegendSpacing.xxs) {
            ForEach(LegendHomeFeedFilter.allCases) { filter in
                Button {
                    guard selectedFilter != filter else {
                        return
                    }

                    UISelectionFeedbackGenerator()
                        .selectionChanged()

                    withAnimation(LegendMotion.standard) {
                        selectedFilter = filter
                    }
                } label: {
                    Text(filter.rawValue)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(
                            selectedFilter == filter
                                ? Color.white
                                : LegendPalette.secondaryLabel
                        )
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 9)
                        .background(
                            selectedFilter == filter
                                ? LegendPalette.primaryNavy
                                : Color.clear,
                            in: Capsule()
                        )
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(
                    selectedFilter == filter
                        ? .isSelected
                        : []
                )
            }
        }
        .padding(4)
        .background(
            LegendPalette.insetSurface,
            in: Capsule()
        )
        .overlay {
            Capsule()
                .stroke(
                    LegendPalette.separator.opacity(0.3),
                    lineWidth: 1
                )
        }
    }

    private func celebrate(
        _ post: LegendJourneyPost
    ) {
        UINotificationFeedbackGenerator()
            .notificationOccurred(.success)
    }

    private func save(
        _ post: LegendJourneyPost
    ) {
        UIImpactFeedbackGenerator(style: .light)
            .impactOccurred()
    }
}

struct JourneyFeedCard: View {
    let post: LegendJourneyPost
    let onCelebrate: () -> Void
    let onDiscuss: () -> Void
    let onSave: () -> Void

    var body: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.md) {
                authorHeader

                VStack(alignment: .leading, spacing: LegendSpacing.xs) {
                    Label {
                        Text(post.title)
                            .font(LegendTypography.section)
                            .foregroundStyle(LegendPalette.label)
                            .fixedSize(
                                horizontal: false,
                                vertical: true
                            )
                    } icon: {
                        Image(systemName: post.kind.systemImageName)
                            .foregroundStyle(LegendPalette.gold)
                    }

                    Text(post.body)
                        .font(LegendTypography.body)
                        .foregroundStyle(
                            LegendPalette.secondaryLabel
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                }

                if !post.detailPoints.isEmpty {
                    detailPanel
                }

                Divider()
                    .overlay(
                        LegendPalette.separator.opacity(0.35)
                    )

                engagementBar
            }
        }
        .accessibilityElement(children: .contain)
    }

    private var authorHeader: some View {
        HStack(alignment: .center, spacing: LegendSpacing.sm) {
            authorAvatar

            VStack(alignment: .leading, spacing: 2) {
                Text(post.authorName)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(LegendPalette.label)
                    .lineLimit(1)

                HStack(spacing: LegendSpacing.xxs) {
                    Text(post.authorContext)
                        .lineLimit(1)

                    Text("•")
                        .accessibilityHidden(true)

                    Text(post.timestampText)
                }
                .font(LegendTypography.metadata)
                .foregroundStyle(
                    LegendPalette.secondaryLabel
                )
            }

            Spacer(minLength: LegendSpacing.xs)

            Button(action: onSave) {
                Image(
                    systemName:
                        post.isSaved
                        ? "bookmark.fill"
                        : "bookmark"
                )
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(
                    post.isSaved
                        ? LegendPalette.gold
                        : LegendPalette.secondaryLabel
                )
                .frame(width: 36, height: 36)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(
                post.isSaved
                    ? "Saved"
                    : "Save post"
            )
        }
    }

    private var authorAvatar: some View {
        ZStack {
            Circle()
                .fill(LegendPalette.primaryNavy)
                .frame(width: 44, height: 44)

            Image(systemName: post.kind.systemImageName)
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(.white)
        }
        .overlay {
            Circle()
                .stroke(
                    LegendPalette.gold.opacity(0.65),
                    lineWidth: 1
                )
        }
        .accessibilityHidden(true)
    }

    private var detailPanel: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
            ForEach(
                Array(post.detailPoints.enumerated()),
                id: \.offset
            ) { _, point in
                HStack(alignment: .top, spacing: LegendSpacing.xs) {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.caption)
                        .foregroundStyle(LegendPalette.gold)
                        .padding(.top, 2)
                        .accessibilityHidden(true)

                    Text(point)
                        .font(LegendTypography.metadata)
                        .foregroundStyle(
                            LegendPalette.secondaryLabel
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                }
            }
        }
        .padding(LegendSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            LegendPalette.insetSurface,
            in: RoundedRectangle(
                cornerRadius: LegendRadius.control,
                style: .continuous
            )
        )
    }

    private var engagementBar: some View {
        HStack(spacing: LegendSpacing.xs) {
            engagementButton(
                title: celebrationTitle,
                symbolName: "hands.clap",
                action: onCelebrate
            )

            engagementButton(
                title: discussionTitle,
                symbolName: "bubble.left",
                action: onDiscuss
            )

            Spacer(minLength: 0)

            Button(action: onSave) {
                Image(systemName: "square.and.arrow.up")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(
                        LegendPalette.secondaryLabel
                    )
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Share post")
        }
    }

    private func engagementButton(
        title: String,
        symbolName: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Label(title, systemImage: symbolName)
                .font(.caption.weight(.semibold))
                .foregroundStyle(
                    LegendPalette.secondaryLabel
                )
                .padding(.horizontal, LegendSpacing.sm)
                .frame(height: 36)
                .background(
                    LegendPalette.insetSurface,
                    in: Capsule()
                )
        }
        .buttonStyle(.plain)
    }

    private var celebrationTitle: String {
        post.celebrateCount == 0
            ? "Celebrate"
            : "\(post.celebrateCount)"
    }

    private var discussionTitle: String {
        post.discussionCount == 0
            ? "Discuss"
            : "\(post.discussionCount)"
    }
}
