import SwiftUI

/// The native home is intentionally a composition over the protected mobile
/// projections. It never derives a role, feed audience, finance state, or
/// profile identity in the client.
struct LegendSocialHomeSection: View {
    let session: MobileSession
    let home: MobileHomeResponse
    @ObservedObject var social: MobileSocialStore
    let openMessages: () -> Void
    let openCircles: () -> Void

    @State private var composerType: MobileSocialContentType = .post
    @State private var composerBody = ""
    @State private var isPresentingComposer = false
    @State private var isPresentingActivity = false
    @State private var commentTarget: MobileSocialPost?
    @State private var commentBody = ""

    var body: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.md) {
            topBar
            rolePulse
            socialContent
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
        HStack(spacing: LegendSpacing.sm) {
            Button {
                composerType = .story
                isPresentingComposer = true
            } label: {
                Image(systemName: "plus")
                    .font(.title3.weight(.semibold))
                    .frame(width: 42, height: 42)
                    .background(LegendPalette.elevatedSurface, in: Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendPalette.label)
            .accessibilityLabel("Create a Legend story")

            Spacer(minLength: LegendSpacing.sm)

            Text("LEGEND")
                .font(.system(size: 25, weight: .black, design: .rounded))
                .tracking(1.7)
                .foregroundStyle(LegendPalette.label)
                .accessibilityAddTraits(.isHeader)

            Spacer(minLength: LegendSpacing.sm)

            Button { isPresentingActivity = true } label: {
                ZStack(alignment: .topTrailing) {
                    Image(systemName: "heart")
                        .font(.title3.weight(.semibold))
                        .frame(width: 42, height: 42)
                        .background(LegendPalette.elevatedSurface, in: Circle())
                    if activityCount > 0 {
                        Text("\(min(activityCount, 99))")
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .padding(5)
                            .background(LegendPalette.critical, in: Circle())
                            .offset(x: 4, y: -4)
                    }
                }
            }
            .buttonStyle(.plain)
            .foregroundStyle(LegendPalette.label)
            .accessibilityLabel("Open activity, \(activityCount) recent interactions")
        }
        .padding(.top, LegendSpacing.xs)
    }

    @ViewBuilder
    private var rolePulse: some View {
        if session.actor.identity.participantType == .agent {
            LegendCard(style: .navy) {
                VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("AGENT COMMAND")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendPalette.gold)
                        Spacer()
                        LegendProfileAvatar(
                            avatar: session.actor.avatar,
                            displayName: session.actor.displayName,
                            size: 32)
                    }
                    Text(session.actor.displayName)
                        .font(LegendTypography.hero)
                        .foregroundStyle(.white)
                        .lineLimit(1)
                    HStack(spacing: LegendSpacing.sm) {
                        LegendSocialMetric(title: "Clients", value: "\(home.activeClientCount)")
                        LegendSocialMetric(title: "Actions", value: "\(home.actions.count)")
                        LegendSocialMetric(title: "Appointments", value: "\(home.upcomingAppointments.count)")
                    }
                }
            }
        } else {
            LegendCard(style: .navy) {
                VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("YOUR WEEK")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendPalette.gold)
                        Spacer()
                        Button(action: openMessages) {
                            Label("\(home.messaging.unreadCount)", systemImage: "message.fill")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(.white)
                                .padding(.horizontal, 9)
                                .padding(.vertical, 6)
                                .background(.white.opacity(0.14), in: Capsule())
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Open messages, \(home.messaging.unreadCount) unread")
                    }
                    if let financial = home.financial {
                        LegendClientWeekPlan(financial: financial)
                    } else {
                        Text("Your secure financial snapshot will appear here after it is saved in the client portal.")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(.white.opacity(0.78))
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var socialContent: some View {
        switch social.state {
        case .idle, .loading:
            LegendSocialLoadingSection()
        case .unavailable(let failure):
            LegendErrorCard(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: social.load)
        case .loaded(let snapshot):
            if session.actor.identity.participantType == .client,
               let journey = home.journey {
                Button(action: openCircles) {
                    HStack(spacing: LegendSpacing.sm) {
                        Image(systemName: "person.3.fill")
                            .foregroundStyle(LegendPalette.gold)
                        Text("Journey Circles")
                            .font(.subheadline.weight(.semibold))
                        Spacer()
                        Text("\(journey.connectedPeerCount) connected")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendPalette.secondaryLabel)
                    }
                    .padding(LegendSpacing.sm)
                    .background(LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Open Journey Circles. \(journey.connectedPeerCount) connected profiles")
            }

            LegendStoryRail(
                currentActor: session.actor,
                stories: snapshot.stories,
                createStory: {
                    composerType = .story
                    isPresentingComposer = true
                })

            LegendSectionHeader("From your authorized Legend network", detail: "\(snapshot.posts.count) updates")

            if snapshot.posts.isEmpty {
                LegendSocialEmptyFeed(createPost: {
                    composerType = .post
                    isPresentingComposer = true
                })
            } else {
                ForEach(snapshot.posts) { post in
                    LegendSocialPostCard(
                        post: post,
                        currentIdentity: session.actor.identity,
                        react: { social.toggleReaction(postID: post.id) },
                        comment: { commentTarget = post },
                        follow: { social.toggleFollow(author: post.author) })
                }
            }
        }
    }

    private func shareUpdate() {
        let body = composerBody.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty else { return }
        social.createPost(type: composerType, body: body)
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

private struct LegendClientWeekPlan: View {
    let financial: MobileFinancialSnapshotResponse

    var body: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
            if let position = financial.position {
                HStack(spacing: LegendSpacing.sm) {
                    LegendSocialMetric(title: "Health", value: "\(position.healthScore)")
                    LegendSocialMetric(
                        title: "Net worth",
                        value: position.netWorth.formatted(.currency(code: "USD")))
                    LegendSocialMetric(
                        title: "Lifestyle",
                        value: position.annualLifestyleRemaining.formatted(.currency(code: "USD")))
                }
            }

            if !financial.upcomingBills.isEmpty {
                Text("NEXT BILLS")
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(LegendPalette.gold)
                    .padding(.top, LegendSpacing.xxs)
                ForEach(financial.upcomingBills.prefix(3)) { bill in
                    HStack {
                        Text(bill.displayName)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)
                            .lineLimit(1)
                        Spacer(minLength: LegendSpacing.sm)
                        Text(bill.nextExpectedDateUTC, format: .dateTime.weekday(.abbreviated).month(.abbreviated).day())
                            .font(LegendTypography.metadata)
                            .foregroundStyle(.white.opacity(0.72))
                        Text(bill.amount.formatted(.currency(code: "USD")))
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)
                    }
                }
            } else if financial.position == nil {
                Text("Complete your Financial Health Snapshot in the client portal to connect your next steps.")
                    .font(LegendTypography.metadata)
                    .foregroundStyle(.white.opacity(0.78))
            }
        }
    }
}

private struct LegendSocialMetric: View {
    let title: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title.uppercased())
                .font(.caption2.weight(.bold))
                .foregroundStyle(.white.opacity(0.6))
                .lineLimit(1)
            Text(value)
                .font(.subheadline.weight(.bold))
                .foregroundStyle(.white)
                .lineLimit(1)
                .minimumScaleFactor(0.78)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct LegendStoryRail: View {
    let currentActor: MobileActor
    let stories: [MobileSocialPost]
    let createStory: () -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: LegendSpacing.md) {
                Button(action: createStory) {
                    VStack(spacing: LegendSpacing.xs) {
                        ZStack(alignment: .bottomTrailing) {
                            LegendProfileAvatar(
                                avatar: currentActor.avatar,
                                displayName: currentActor.displayName,
                                size: 58)
                                .padding(3)
                                .overlay { Circle().stroke(LegendPalette.gold, lineWidth: 2) }
                            Image(systemName: "plus")
                                .font(.caption.weight(.black))
                                .foregroundStyle(.white)
                                .frame(width: 22, height: 22)
                                .background(LegendPalette.primaryNavy, in: Circle())
                                .overlay { Circle().stroke(.white, lineWidth: 1.5) }
                        }
                        Text("Your story")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendPalette.label)
                            .lineLimit(1)
                    }
                    .frame(width: 72)
                }
                .buttonStyle(.plain)

                ForEach(stories) { story in
                    VStack(spacing: LegendSpacing.xs) {
                        LegendProfileAvatar(
                            avatar: story.author.avatar,
                            displayName: story.author.displayName,
                            size: 58)
                            .padding(3)
                            .overlay { Circle().stroke(LegendPalette.gold, lineWidth: 2) }
                        Text(story.author.displayName)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendPalette.label)
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
    let react: () -> Void
    let comment: () -> Void
    let follow: () -> Void

    var body: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                HStack(alignment: .top, spacing: LegendSpacing.sm) {
                    LegendProfileAvatar(avatar: post.author.avatar, displayName: post.author.displayName, size: 42)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(post.author.displayName)
                            .font(.subheadline.weight(.bold))
                            .foregroundStyle(LegendPalette.label)
                            .lineLimit(1)
                        Text(metadata)
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .lineLimit(1)
                    }
                    Spacer(minLength: LegendSpacing.xs)
                    if post.author.identity != currentIdentity {
                        Button(post.followedByCurrentActor ? "Following" : "Follow", action: follow)
                            .font(.caption.weight(.bold))
                            .foregroundStyle(post.followedByCurrentActor ? LegendPalette.secondaryLabel : LegendPalette.primaryNavy)
                            .padding(.horizontal, 9)
                            .padding(.vertical, 6)
                            .background(LegendPalette.insetSurface, in: Capsule())
                            .buttonStyle(.plain)
                    }
                }

                Text(post.body)
                    .font(LegendTypography.body)
                    .foregroundStyle(LegendPalette.label)
                    .fixedSize(horizontal: false, vertical: true)

                HStack(spacing: LegendSpacing.md) {
                    Button(action: react) {
                        Label("\(post.reactionCount)", systemImage: post.reactedByCurrentActor ? "heart.fill" : "heart")
                            .foregroundStyle(post.reactedByCurrentActor ? LegendPalette.critical : LegendPalette.secondaryLabel)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(post.reactedByCurrentActor ? "Remove appreciation" : "Appreciate this update")

                    Button(action: comment) {
                        Label("\(post.commentCount)", systemImage: "bubble.right")
                            .foregroundStyle(LegendPalette.secondaryLabel)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Comment on this update")

                    Spacer()

                    Text(post.postedUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                        .font(.caption2)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }

                if !post.comments.isEmpty {
                    Divider()
                    ForEach(post.comments.suffix(2)) { comment in
                        Text("\(comment.author.displayName): \(comment.body)")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
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
        VStack(alignment: .leading, spacing: LegendSpacing.sm) {
            LegendSectionHeader("From your authorized Legend network")
            HStack(spacing: LegendSpacing.md) {
                ForEach(0 ..< 4, id: \.self) { _ in
                    Circle()
                        .fill(LegendPalette.insetSurface)
                        .frame(width: 58, height: 58)
                }
            }
            LegendCard {
                HStack(spacing: LegendSpacing.sm) {
                    ProgressView()
                    Text("Loading your secure feed…")
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }
            }
        }
        .redacted(reason: .placeholder)
    }
}

private struct LegendSocialEmptyFeed: View {
    let createPost: () -> Void

    var body: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                Label("Start the conversation", systemImage: "sparkles")
                    .font(LegendTypography.section)
                    .foregroundStyle(LegendPalette.label)
                Text("Share a focused update with the people already authorized in your Legend network.")
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                Button("Create update", action: createPost)
                    .buttonStyle(LegendButtonStyle(kind: .primary))
            }
        }
    }
}

private struct LegendSocialComposer: View {
    @Binding var type: MobileSocialContentType
    @Binding var messageBody: String
    let submit: () -> Void
    let cancel: () -> Void

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendSpacing.md) {
                Picker("Update type", selection: $type) {
                    ForEach(MobileSocialContentType.allCases) { option in
                        Text(option.displayName).tag(option)
                    }
                }
                .pickerStyle(.segmented)

                TextEditor(text: $messageBody)
                    .font(LegendTypography.body)
                    .padding(LegendSpacing.sm)
                    .frame(minHeight: 170)
                    .background(LegendPalette.insetSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
                    .accessibilityLabel("Legend update")

                Text("Shared only with your server-authorized Legend network.")
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)

                Spacer()
            }
            .padding(LegendSpacing.md)
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("New Legend update")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: cancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Share", action: submit)
                        .disabled(messageBody.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
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
            VStack(alignment: .leading, spacing: LegendSpacing.md) {
                Text("Reply to \(authorName)")
                    .font(LegendTypography.section)
                TextEditor(text: $messageBody)
                    .font(LegendTypography.body)
                    .padding(LegendSpacing.sm)
                    .frame(minHeight: 130)
                    .background(LegendPalette.insetSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
                    .accessibilityLabel("Comment")
                Spacer()
            }
            .padding(LegendSpacing.md)
            .background(LegendPalette.canvas.ignoresSafeArea())
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
                        HStack(spacing: LegendSpacing.sm) {
                            LegendProfileAvatar(avatar: item.actor.avatar, displayName: item.actor.displayName, size: 38)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.actor.displayName)
                                    .font(.subheadline.weight(.semibold))
                                Text(detail(for: item.kind))
                                    .font(LegendTypography.metadata)
                                    .foregroundStyle(LegendPalette.secondaryLabel)
                            }
                            Spacer()
                            Text(item.occurredUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                                .font(.caption2)
                                .foregroundStyle(LegendPalette.secondaryLabel)
                        }
                    }
                    .listStyle(.plain)
                }
            }
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("Activity")
            .navigationBarTitleDisplayMode(.inline)
        }
    }

    private func detail(for kind: String) -> String {
        switch kind {
        case "reaction": "appreciated your update"
        case "comment": "commented on your update"
        case "follow": "followed your Legend profile"
        default: "interacted with your update"
        }
    }
}
