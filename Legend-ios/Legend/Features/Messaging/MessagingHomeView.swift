import ImageIO
import QuickLook
import SwiftUI
import UIKit
import PhotosUI
import UniformTypeIdentifiers


private func publicAgentRoleLabel(
    roleLabel: String?
) -> String? {
    guard let roleLabel = roleLabel?
        .trimmingCharacters(in: .whitespacesAndNewlines),
          !roleLabel.isEmpty else {
        return nil
    }

    return roleLabel
}

private func publicRelationshipLabel(
    _ value: String?
) -> String? {
    guard let normalized = value?
        .trimmingCharacters(in: .whitespacesAndNewlines),
          !normalized.isEmpty else {
        return nil
    }

    let internalLabels = Set([
        "agent",
        "client",
        "legend agent",
        "legend client"
    ])

    guard !internalLabels.contains(normalized.lowercased()) else {
        return nil
    }

    return normalized
}

private func legendMessagingGroupImageRequest(
    from data: Data?
) -> MessagingGroupImageRequest? {
    guard let data,
          let sourceImage = UIImage(data: data),
          sourceImage.size.width > 0,
          sourceImage.size.height > 0 else { return nil }

    let maximumBytes = 512 * 1_024
    let compressionQualities: [CGFloat] = [0.82, 0.72, 0.62, 0.52, 0.42, 0.32]
    let maximumSides: [CGFloat] = [640, 560, 480, 400, 320]

    for maximumSide in maximumSides {
        let scale = min(
            1,
            maximumSide / max(sourceImage.size.width, sourceImage.size.height))
        let targetSize = CGSize(
            width: max(1, sourceImage.size.width * scale),
            height: max(1, sourceImage.size.height * scale))

        // Rendering normalizes orientation and strips source-format metadata while
        // keeping the existing JPEG transport contract used by messaging.
        let normalized = UIGraphicsImageRenderer(size: targetSize).image { _ in
            sourceImage.draw(in: CGRect(origin: .zero, size: targetSize))
        }

        for quality in compressionQualities {
            guard let compressed = normalized.jpegData(compressionQuality: quality) else {
                continue
            }

            if compressed.count <= maximumBytes {
                return MessagingGroupImageRequest(
                    contentType: "image/jpeg",
                    base64Content: compressed.base64EncodedString())
            }
        }
    }

    return nil
}

private struct LegendMessagingGroupAvatar: View {
    let avatar: ProfileAvatar?
    let size: CGFloat

    var body: some View {
        LegendAvatarImageContent(avatar: avatar) {
            Image(systemName: "person.3.fill")
                .font(.system(
                    size: size * 0.38,
                    weight: .semibold))
                .foregroundStyle(LegendNextColor.midnight)
                .background(
                    LegendNextGradient.gold,
                    in: Circle())
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
    }
}

private struct LegendVerificationProfileRoute: Identifiable {
    let participant: MessagingParticipant
    let review: VerificationReview

    var id: UUID { review.id }

    var profile: MobileSocialAuthor {
        MobileSocialAuthor(
            identity: participant.identity,
            profileID: participant.profileID,
            displayName: participant.displayName,
            avatar: participant.avatar,
            isVerified: participant.isVerified,
            roleLabel: participant.roleLabel)
    }
}

// MARK: - Messages Inbox

struct MessagingHomeView: View {
    @ObservedObject var store: MessagingStore
    let openConversation: (UUID) -> Void

    @Environment(\.colorScheme) private var colorScheme
    @State private var inboxSearch = ""
    @State private var isPresentingNewConversation = false
    @State private var isPresentingCallDirectory = false
    @State private var conversationPendingRemoval: ConversationSummary?

    var body: some View {
        ZStack {
            LegendNextCanvas()

            content
        }
        .toolbar(.hidden, for: .navigationBar)
        .sheet(isPresented: $isPresentingNewConversation) {
            LegendRecipientPicker(
                store: store,
                initialSearch: inboxSearch,
                selectConversation: { conversationID in
                    isPresentingNewConversation = false
                    openConversation(conversationID)
                },
                dismiss: {
                    isPresentingNewConversation = false
                }
            )
            .legendNextSheetChrome(detents: [.large])
        }
        .sheet(isPresented: $isPresentingCallDirectory) {
            LegendRecipientPicker(
                store: store,
                initialSearch: inboxSearch,
                forCalling: true,
                selectConversation: openConversation,
                dismiss: { isPresentingCallDirectory = false })
                .legendNextSheetChrome(detents: [.large])
        }

        .alert(
            LegendLocalized("Remove conversation?"),
            isPresented: Binding(
                get: { conversationPendingRemoval != nil },
                set: { if !$0 { conversationPendingRemoval = nil } }
            )
        ) {
            Button(LegendLocalized("Cancel"), role: .cancel) {
                conversationPendingRemoval = nil
            }
            Button(LegendLocalized("Remove"), role: .destructive) {
                if let conversationPendingRemoval {
                    store.removeConversation(conversationID: conversationPendingRemoval.id)
                }
                conversationPendingRemoval = nil
            }
        } message: {
            Text(LegendLocalized("This removes the conversation from your inbox only. A new message will bring it back."))
        }
        .refreshable {
            _ = await store.refresh()
        }
    }

    @ViewBuilder
    private var content: some View {
        switch store.state {
        case .idle, .loading:
            inboxLoadingState

        case .loaded(let conversations):
            inbox(conversations)

        case .unavailable(let message):
            inboxFailure(
                title: LegendLocalized("Messaging is unavailable"),
                message: message,
                symbol: "lock.message",
                canRetry: false
            )

        case .unauthorized(let failure),
             .forbidden(let failure),
             .offline(let failure),
             .failed(let failure):
            inboxFailure(
                title: failure.title,
                message: failure.message,
                symbol: "exclamationmark.bubble",
                canRetry: true
            )
        }
    }

    private func inbox(
        _ conversations: [ConversationSummary]
    ) -> some View {
        let query = inboxSearch.trimmingCharacters(in: .whitespacesAndNewlines)
        let matches = conversations.filter { query.isEmpty || $0.title.localizedCaseInsensitiveContains(query) }
        return LegendScrollView {
            LazyVStack(spacing: 0) {
                inboxHeader
                LegendMessagingSearchField(search: $inboxSearch, placeholder: LegendSharedDesign.copy("search.messaging"))
                if !query.isEmpty {
                    Button {
                        isPresentingNewConversation = true
                    } label: {
                        Label(LegendLocalized("Search profiles to message or call"), systemImage: "person.crop.circle.badge.magnifyingglass")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.gold)
                            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                }

                if let refreshFailure = store.refreshFailure {
                    LegendMessagingStatusBanner(
                        symbol: "exclamationmark.arrow.triangle.2.circlepath",
                        title: LegendLocalized("Messages could not refresh"),
                        message: refreshFailure.message
                    )
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                    .padding(.top, LegendNextSpacing.sm)
                }

                if matches.isEmpty && !query.isEmpty {
                    LegendMessagingEmptyState(symbol: "magnifyingglass", title: LegendLocalized("No matching conversations"),
                        message: LegendLocalized("Search profiles to start a message or call."), actionTitle: nil, action: nil)
                } else if conversations.isEmpty {
                    inboxEmptyState
                        .padding(.top, LegendNextSpacing.display)
                } else {
                    conversationSection(matches)
                        .padding(.top, LegendNextSpacing.intermediate)
                }
            }
            .padding(.bottom, 118)
        }
        .scrollIndicators(.hidden)
    }

    private var inboxHeader: some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.card,
            padding: LegendNextSpacing.md
        ) {
            HStack(alignment: .center, spacing: LegendNextSpacing.md) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    Text(LegendLocalized("Messages"))
                        .font(LegendNextTypography.section)
                        .foregroundStyle(.white)
                }

                Spacer(minLength: LegendNextSpacing.sm)

                HStack(spacing: LegendNextSpacing.sm) {
                    Button {
                        isPresentingNewConversation = true
                        store.loadRecipients()
                    } label: {
                        Image(systemName: "square.and.pencil")
                            .font(.system(size: 19, weight: .semibold))
                            .foregroundStyle(LegendNextColor.midnight)
                            .frame(width: 48, height: 48)
                            .background(LegendNextGradient.gold, in: Circle())
                            .overlay {
                                Circle()
                                    .stroke(.white.opacity(0.30), lineWidth: 1)
                            }
                            .shadow(
                                color: LegendNextColor.gold.opacity(0.25),
                                radius: 12,
                                y: 6
                            )
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())
                    .accessibilityLabel(LegendLocalized("Start a new conversation", context: "accessibility copy"))

                    Button {
                        isPresentingCallDirectory = true
                    } label: {
                        Image(systemName: "phone.fill")
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(.white)
                            .frame(width: 48, height: 48)
                            .background(.white.opacity(0.12), in: Circle())
                            .overlay {
                                Circle()
                                    .stroke(LegendNextColor.gold.opacity(0.66), lineWidth: 1)
                            }
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())
                    .accessibilityLabel(LegendLocalized("Call a connection", context: "accessibility copy"))
                }
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.md)
        .padding(.bottom, LegendNextSpacing.xs)
    }

    private var pinnedColumns: [GridItem] {
        Array(
            repeating: GridItem(
                .flexible(),
                spacing: LegendNextSpacing.sm,
                alignment: .top
            ),
            count: 3
        )
    }

    private func conversationSection(
        _ conversations: [ConversationSummary]
    ) -> some View {
        let pinnedConversations = conversations.filter { $0.isPinned }
        let recentConversations = conversations.filter { !$0.isPinned }

        return VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.md
        ) {
            if store.isRefreshing {
                HStack(spacing: LegendNextSpacing.xs) {
                    ProgressView()
                        .controlSize(.small)
                        .tint(LegendNextColor.gold)

                    Text(LegendLocalized("Updating"))
                        .font(.caption.weight(.medium))
                        .foregroundStyle(
                            LegendNextColor.textSecondary
                        )
                }
                .padding(
                    .horizontal,
                    LegendNextSpacing.pageHorizontal
                )
            }

            if !pinnedConversations.isEmpty {
                pinnedConversationGrid(
                    pinnedConversations
                )
            }

            if !recentConversations.isEmpty {
                LazyVStack(
                    spacing: LegendNextSpacing.sm
                ) {
                    ForEach(
                        recentConversations
                    ) { conversation in
                        conversationButton(
                            conversation
                        )
                    }
                }
                .padding(
                    .horizontal,
                    LegendNextSpacing.pageHorizontal
                )
            }

            if store.hasMoreConversations {
                Button {
                    store.loadMoreConversations()
                } label: {
                    HStack(
                        spacing: LegendNextSpacing.xs
                    ) {
                        if store.isLoadingMoreConversations {
                            ProgressView()
                                .controlSize(.small)
                                .tint(
                                    LegendNextColor.gold
                                )
                        }

                        Text(
                            store.isLoadingMoreConversations
                                ? LegendLocalized("Loading earlier conversations")
                                : LegendLocalized("Load earlier conversations")
                        )
                    }
                }
                .buttonStyle(
                    LegendNextButtonStyle(
                        kind: .secondary,
                        controlHeight: 38
                    )
                )
                .disabled(
                    store.isLoadingMoreConversations
                )
                .padding(
                    .horizontal,
                    LegendNextSpacing.pageHorizontal
                )
                .accessibilityHint(
                    LegendLocalized("Loads the next oldest conversations without delaying the latest messages.", context: "accessibility copy")
                )
            }
        }
    }

    private func pinnedConversationGrid(
        _ conversations: [ConversationSummary]
    ) -> some View {
        LazyVGrid(
            columns: pinnedColumns,
            alignment: .center,
            spacing: LegendNextSpacing.md
        ) {
            ForEach(conversations) { conversation in
                pinnedConversationButton(
                    conversation
                )
            }
        }
        .padding(
            .horizontal,
            LegendNextSpacing.pageHorizontal
        )
        .accessibilityElement(
            children: .contain
        )
        .accessibilityLabel(
            LegendLocalized("Pinned conversations", context: "accessibility copy")
        )
    }

    private func pinnedConversationButton(
        _ conversation: ConversationSummary
    ) -> some View {
        Button {
            openConversation(conversation.id)
        } label: {
            VStack(
                spacing: LegendNextSpacing.xs
            ) {
                ZStack(alignment: .topTrailing) {
                    Group {
                        if conversation.conversationType == "Group" {
                            LegendMessagingGroupAvatar(
                                avatar: conversation.groupAvatar,
                                size: 72
                            )
                        } else {
                            LegendMessagingAvatar(
                                participant:
                                    conversation.counterparty,
                                size: 72,
                                showsGoldRing:
                                    conversation.unreadCount > 0
                            )
                        }
                    }
                    .frame(
                        width: 72,
                        height: 72
                    )

                    if conversation.unreadCount > 0 {
                        Text(
                            conversation.unreadCount > 99
                                ? "99+"
                                : "\(conversation.unreadCount)"
                        )
                        .font(
                            .caption2.weight(.bold)
                        )
                        .foregroundStyle(.white)
                        .frame(
                            minWidth: 22,
                            minHeight: 22
                        )
                        .padding(
                            .horizontal,
                            conversation.unreadCount > 9
                                ? 4
                                : 0
                        )
                        .background(
                            Color(
                                uiColor: .systemRed
                            ),
                            in: Capsule()
                        )
                        .offset(
                            x: 7,
                            y: -6
                        )
                        .accessibilityLabel(
                            LegendLocalized("{value1} unread messages", context: "accessibility copy", arguments: ["value1": String(describing: (conversation.unreadCount))])
                        )
                    }
                }
                .frame(
                    width: 88,
                    height: 78
                )

                Text(conversation.title)
                    .font(
                        .caption.weight(.semibold)
                    )
                    .foregroundStyle(
                        LegendNextColor.textPrimary
                    )
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.82)
                    .frame(
                        maxWidth: .infinity,
                        minHeight: 32,
                        alignment: .top
                    )
            }
            .frame(
                maxWidth: .infinity,
                alignment: .top
            )
        }
        .buttonStyle(.plain)
        .accessibilityLabel(
            LegendLocalized("{value1}, pinned conversation", context: "accessibility copy", arguments: ["value1": String(describing: (conversation.title))])
        )
        .accessibilityHint(
            LegendLocalized("Open conversation", context: "accessibility copy")
        )
        .contextMenu {
            conversationContextMenu(
                conversation
            )
        }
    }

    private func conversationButton(
        _ conversation: ConversationSummary
    ) -> some View {
        Button {
            openConversation(conversation.id)
        } label: {
            LegendConversationRow(
                conversation: conversation
            )
        }
        .buttonStyle(
            LegendMessagingPressButtonStyle()
        )
        .accessibilityHint(
            LegendLocalized("Open conversation", context: "accessibility copy")
        )
        .contextMenu {
            conversationContextMenu(
                conversation
            )
        }
    }

    @ViewBuilder
    private func conversationContextMenu(
        _ conversation: ConversationSummary
    ) -> some View {
        Button {
            store.setPinned(
                conversationID: conversation.id,
                isPinned: !conversation.isPinned
            )
        } label: {
            Label(
                conversation.isPinned
                    ? "Unpin"
                    : "Pin",
                systemImage:
                    conversation.isPinned
                        ? "pin.slash"
                        : "pin"
            )
        }

        Button {
            store.setMuted(
                conversationID: conversation.id,
                isMuted: !conversation.isMuted
            )
        } label: {
            Label(
                conversation.isMuted
                    ? "Unmute"
                    : "Mute",
                systemImage:
                    conversation.isMuted
                        ? "bell"
                        : "bell.slash"
            )
        }

        Divider()

        Button(role: .destructive) {
            conversationPendingRemoval =
                conversation
        } label: {
            Label(
                LegendLocalized("Remove from inbox"),
                systemImage: "trash"
            )
        }
    }

    private var inboxEmptyState: some View {
        LegendMessagingEmptyState(
            symbol: "message.badge.waveform.fill",
            title: LegendLocalized("Start a private conversation"),
            message: LegendLocalized("Choose someone in your Legend network to begin."),
            actionTitle: "New message",
            action: {
                isPresentingNewConversation = true
                store.loadRecipients()
            }
        )
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
    }

    private var inboxLoadingState: some View {
        LegendScrollView {
            VStack(spacing: 0) {
                inboxHeader

                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    ForEach(0..<4, id: \.self) { _ in
                        LegendMessagingConversationSkeleton()
                    }
                }
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.intermediate)
            }
            .padding(.bottom, 118)
        }
        .scrollIndicators(.hidden)
        .accessibilityLabel(LegendLocalized("Loading conversations", context: "accessibility copy"))
    }

    private func inboxFailure(
        title: String,
        message: String,
        symbol: String,
        canRetry: Bool
    ) -> some View {
        LegendScrollView {
            VStack(spacing: 0) {
                inboxHeader

                LegendMessagingEmptyState(
                    symbol: symbol,
                    title: title,
                    message: message,
                    actionTitle: canRetry ? "Try again" : nil,
                    action: canRetry
                        ? { Task { _ = await store.refresh() } }
                        : nil
                )
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.display)
            }
            .padding(.bottom, 118)
        }
        .scrollIndicators(.hidden)
    }
}

private struct LegendCallRecipient: Identifiable {
    let id: UUID
    let name: String
}

private struct LegendConversationCallSheet: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    let fallbackName: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ZStack {
            LegendNextCanvas()
            VStack(spacing: LegendNextSpacing.md) {
                Text(fallbackName).font(.title3.bold())
                LegendCallActionButton(title: LegendLocalized("Legend voice call"), subtitle: LegendLocalized("Private in-app audio"), symbol: "phone.fill") {
                    dismiss()
                    store.calling?.start(conversationId: conversationID, video: false, recipientName: fallbackName)
                }
                LegendCallActionButton(title: LegendLocalized("Legend video call"), subtitle: LegendLocalized("Connect face to face"), symbol: "video.fill") {
                    dismiss()
                    store.calling?.start(conversationId: conversationID, video: true, recipientName: fallbackName)
                }
                Button(LegendLocalized("Cancel")) { dismiss() }
            }.padding(LegendNextSpacing.pageHorizontal)
        }
    }
}

private struct LegendCallActionButton: View {
    let title: String
    let subtitle: String
    let symbol: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: symbol)
                    .font(.body.weight(.bold))
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 42, height: 42)
                    .background(LegendNextGradient.gold, in: Circle())

                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer()
                Image(systemName: "arrow.up.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.gold)
            }
            .padding(LegendNextSpacing.sm)
            .background(LegendNextColor.surfaceElevated, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .stroke(LegendNextColor.separator, lineWidth: 1)
            }
        }
        .buttonStyle(LegendMessagingPressButtonStyle())
    }
}

// MARK: - Recipient Picker

private struct LegendRecipientPicker: View {
    @ObservedObject var store: MessagingStore
    var initialSearch = ""
    var forCalling = false
    let selectConversation: (UUID) -> Void
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var colorScheme
    @FocusState private var searchIsFocused: Bool
    @State private var search = ""
    @State private var selectedCall: LegendCallRecipient?
    @State private var isCreatingGroup = false
    @State private var groupSubject = ""
    @State private var groupRecipients: [LogicalParticipantIdentity: MessagingRecipient] = [:]
    @State private var groupMeetingDraft = LegendGroupMeetingDraft()
    @State private var selectedGroupPhoto: PhotosPickerItem?
    @State private var groupImage: MessagingGroupImageRequest?
    @State private var isPreparingGroupImage = false
    @State private var isShowingGroupPhotoPreparationFailure = false

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                VStack(spacing: 0) {
                    recipientHeader
                    LegendMessagingSearchField(search: $search)
                    recipientScopes
                    if isCreatingGroup {
                        groupCreationBar
                    }
                    recipientContent
                }
            }
            .toolbar(.hidden, for: .navigationBar)
            .onAppear {
                search = initialSearch
                store.loadRecipients(search: initialSearch)
            }
            .onChange(of: search) { _, value in
                store.searchRecipients(value)
            }
            .sheet(item: $selectedCall) { target in
                LegendConversationCallSheet(store: store, conversationID: target.id, fallbackName: target.name)
                    .legendNextSheetChrome(detents: [.height(340)])
            }
        }
    }

    private var recipientHeader: some View {
        VStack(spacing: LegendNextSpacing.md) {
            HStack(spacing: LegendNextSpacing.md) {
                Button(LegendLocalized("Cancel"), action: dismiss)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.white)
                    .frame(minWidth: 68, minHeight: 44)
                    .background(.white.opacity(0.08), in: Capsule())
                    .overlay {
                        Capsule()
                            .stroke(.white.opacity(0.10), lineWidth: 1)
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())

                Spacer()

                VStack(spacing: 2) {
                    Text(forCalling ? LegendLocalized("Call a connection") : isCreatingGroup ? LegendLocalized("New group") : LegendLocalized("New message"))
                        .font(.system(.headline, design: .rounded).weight(.bold))
                        .foregroundStyle(.white)

                    Text(isCreatingGroup
                         ? LegendLocalized("Choose at least two connections")
                         : LegendLocalized("Search your Legend network"))
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.66))
                }

                Spacer()

                if !forCalling {
                Button {
                    isCreatingGroup.toggle()
                    groupRecipients.removeAll()
                    groupSubject = ""
                    groupMeetingDraft = LegendGroupMeetingDraft()
                    groupImage = nil
                    isPreparingGroupImage = false
                    selectedGroupPhoto = nil
                } label: {
                    Image(systemName: isCreatingGroup ? "person.fill" : "person.3.fill")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(.white)
                        .frame(width: 44, height: 44)
                        .background(.white.opacity(0.10), in: Circle())
                }
                .buttonStyle(LegendMessagingPressButtonStyle())
                .accessibilityLabel(
                    isCreatingGroup
                        ? LegendLocalized("Create a direct message", context: "accessibility copy")
                        : LegendLocalized("Create a group chat", context: "accessibility copy"))
                }
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.sm)
        .padding(.bottom, LegendNextSpacing.lg)
        .background {
            LegendNextGradient.hero
                .clipShape(
                    UnevenRoundedRectangle(
                        bottomLeadingRadius: 26,
                        bottomTrailingRadius: 26
                    )
                )
                .ignoresSafeArea(edges: .top)
        }
    }

    private var groupCreationBar: some View {
        VStack(spacing: LegendNextSpacing.xs) {
            HStack(spacing: LegendNextSpacing.sm) {
                PhotosPicker(
                    selection: $selectedGroupPhoto,
                    matching: .images,
                    photoLibrary: PHPhotoLibrary.shared()) {
                        LegendMessagingGroupAvatar(
                            avatar: groupImage.map {
                                ProfileAvatar(
                                    kind: "inline",
                                    contentType: $0.contentType,
                                    base64Content: $0.base64Content)
                            },
                            size: 42)
                    }
                    .accessibilityLabel(LegendLocalized("Choose group photo", context: "accessibility copy"))
                    .disabled(isPreparingGroupImage)

                TextField(LegendLocalized("Group name"), text: $groupSubject)
                    .textInputAutocapitalization(.words)
                    .font(.subheadline.weight(.semibold))
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .frame(minHeight: 42)
                    .background(
                        LegendNextColor.surfaceElevated,
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.compact,
                            style: .continuous
                        )
                    )
            }

            LegendGroupMeetingEditor(
                draft: $groupMeetingDraft,
                hostOptions: groupHostOptions,
                canManageMeeting: true)

            Button(
                store.isCreatingGroup
                    ? LegendLocalized("Creating group…")
                    : LegendLocalized("Create group ({count})", arguments: ["count": groupRecipients.count])) {
                let recipients = Array(groupRecipients.values)
                store.createGroup(
                    subject: groupSubject,
                    recipients: recipients,
                    groupImage: groupImage,
                    meeting: groupMeetingDraft.request) { conversationID in
                    selectConversation(conversationID)
                }
            }
            .buttonStyle(LegendNextButtonStyle(kind: .primary, controlHeight: 34))
            .disabled(
                store.isCreatingGroup ||
                isPreparingGroupImage ||
                groupSubject.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                groupRecipients.count < 2 ||
                !groupMeetingDraft.isValid
            )
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.bottom, LegendNextSpacing.sm)
        .alert(LegendLocalized("Group photo could not be prepared"), isPresented: $isShowingGroupPhotoPreparationFailure) {
            Button(LegendLocalized("OK"), role: .cancel) {}
        } message: {
            Text(LegendLocalized("Choose another photo and try again. Your group was not created without the selected photo."))
        }
        .onChange(of: selectedGroupPhoto) { _, item in
            guard let item else { return }
            isPreparingGroupImage = true
            groupImage = nil
            Task {
                let photoData = try? await item.loadTransferable(type: Data.self)
                guard !Task.isCancelled else { return }

                groupImage = legendMessagingGroupImageRequest(from: photoData)
                isShowingGroupPhotoPreparationFailure = groupImage == nil
                isPreparingGroupImage = false
                selectedGroupPhoto = nil
            }
        }
    }

    private var groupHostOptions: [LegendGroupHostOption] {
        groupRecipients.values
            .map { recipient in
                LegendGroupHostOption(
                    identity: recipient.identity,
                    displayName: recipient.displayName,
                    detail: recipient.relationshipLabel ?? LegendLocalized("Group member"))
            }
            .sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    private var recipientScopes: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: LegendNextSpacing.xs) {
                ForEach(store.availableRecipientScopes) { scope in
                    Button {
                        search = ""
                        store.selectRecipientScope(scope)
                    } label: {
                        Label(scope.title, systemImage: scope.icon)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(
                                store.selectedRecipientScope == scope
                                    ? LegendNextColor.midnight
                                    : LegendNextColor.textSecondary
                            )
                            .padding(.horizontal, LegendNextSpacing.md)
                            .frame(minHeight: 38)
                            .background {
                                if store.selectedRecipientScope == scope {
                                    Capsule()
                                        .fill(LegendNextGradient.gold)
                                } else {
                                    Capsule()
                                        .fill(LegendNextColor.surfaceElevated)
                                }
                            }
                            .overlay {
                                Capsule()
                                    .stroke(
                                        store.selectedRecipientScope == scope
                                            ? Color.clear
                                            : LegendNextColor.separator,
                                        lineWidth: 1
                                    )
                            }
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())
                    .accessibilityLabel(LegendLocalized("Show {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (scope.title.lowercased()))]))
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        }
        .scrollIndicators(.hidden)
        .padding(.bottom, LegendNextSpacing.sm)
    }

    @ViewBuilder
    private var recipientContent: some View {
        switch store.recipientState {
        case .idle, .loading:
            recipientLoading

        case .unavailable(let failure):
            LegendMessagingEmptyState(
                symbol: "person.crop.circle.badge.exclamationmark",
                title: failure.title,
                message: failure.message,
                actionTitle: "Try again",
                action: {
                    store.loadRecipients(search: search)
                }
            )
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.display)

        case .loaded(let recipients):
            if recipients.isEmpty {
                LegendMessagingEmptyState(
                    symbol: "person.2.slash",
                    title: search.isEmpty
                        ? "No people found"
                        : "No matches found",
                    message: search.isEmpty
                        ? "Try another category or search."
                        : "No \(store.selectedRecipientScope.rawValue.lowercased()) match “\(search)”.",
                    actionTitle: nil,
                    action: nil
                )
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.display)
            } else {
                recipientList(recipients)
            }
        }
    }

    private func recipientList(
        _ recipients: [MessagingRecipient]
    ) -> some View {
        LegendScrollView(tracksNavigationChrome: false) {
            LazyVStack(alignment: .leading, spacing: 3) {
                Text(store.selectedRecipientScope.title)
                    .font(.system(.headline, design: .rounded).weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .padding(.bottom, LegendNextSpacing.tiny)

                ForEach(recipients) { recipient in
                    if isCreatingGroup {
                        Button {
                            toggleGroupRecipient(recipient)
                        } label: {
                            LegendContactCard(
                                displayName: recipient.displayName,
                                subtitle: relationshipLabel(for: recipient),
                                detail: recipient.email,
                                isVerified: recipient.isVerified == true,
                                avatar: {
                                    LegendMessagingAvatar(
                                        participant: MessagingParticipant(
                                            identity: recipient.identity,
                                            profileID: recipient.profileID,
                                            displayName: recipient.displayName,
                                            roleLabel: recipient.roleLabel,
                                            avatar: recipient.avatar,
                                            isVerified: recipient.isVerified
                                        ),
                                        size: 46,
                                        showsGoldRing: true)
                                },
                                action: {
                                    Image(systemName: groupRecipients[recipient.identity] == nil
                                          ? "circle"
                                          : "checkmark.circle.fill")
                                        .font(.title3.weight(.semibold))
                                        .foregroundStyle(groupRecipients[recipient.identity] == nil
                                                         ? LegendNextColor.contactAction
                                                         : LegendNextColor.success)
                                }
                            )
                        }
                        .buttonStyle(LegendMessagingPressButtonStyle())
                        .accessibilityLabel(
                            groupRecipients[recipient.identity] == nil
                                ? "Add \(recipient.displayName) to the group"
                                : "Remove \(recipient.displayName) from the group")
                    } else {
                        LegendRecipientRow(
                            recipient: recipient,
                            isStarting: store.isStartingConversation
                        )
                        .contentShape(Rectangle())
                        .onTapGesture {
                            select(recipient)
                        }
                        .accessibilityAddTraits(.isButton)
                        .accessibilityHint(
                            recipient.existingConversationID == nil
                                ? "Start a conversation"
                                : "Open the existing conversation"
                        )
                        .accessibilityAction {
                            select(recipient)
                        }
                        .allowsHitTesting(!store.isStartingConversation)
                    }
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xxl)
        }
        .scrollDismissesKeyboard(.interactively)
        .scrollIndicators(.hidden)
    }

    private func select(
        _ recipient: MessagingRecipient
    ) {
        guard !store.isStartingConversation else {
            return
        }

        if forCalling, let existingID = recipient.existingConversationID {
            selectedCall = LegendCallRecipient(id: existingID, name: recipient.displayName)
            return
        }
        store.startConversation(
            with: recipient,
            completion: { id in
                if forCalling { selectedCall = LegendCallRecipient(id: id, name: recipient.displayName) }
                else { selectConversation(id) }
            }
        )
    }

    private func toggleGroupRecipient(_ recipient: MessagingRecipient) {
        if groupRecipients[recipient.identity] == nil {
            groupRecipients[recipient.identity] = recipient
        } else {
            groupRecipients.removeValue(forKey: recipient.identity)
            if groupMeetingDraft.hostIdentity == recipient.identity {
                groupMeetingDraft.hostIdentity = nil
            }
        }
    }

    private func relationshipLabel(for recipient: MessagingRecipient) -> String? {
        recipient.identity.participantType == .agent
            ? publicAgentRoleLabel(
                roleLabel: recipient.roleLabel)
            : publicRelationshipLabel(recipient.relationshipLabel) ?? LegendLocalized("Connection")
    }

    private var recipientLoading: some View {
        LegendScrollView(tracksNavigationChrome: false) {
            LazyVStack(spacing: LegendNextSpacing.sm) {
                ForEach(0..<4, id: \.self) { _ in
                    LegendMessagingConversationSkeleton()
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.sm)
        }
        .scrollIndicators(.hidden)
        .accessibilityLabel(LegendLocalized("Loading people", context: "accessibility copy"))
    }
}

/// Member additions use the same server-authorized recipient collections as
/// direct and new-group conversations. The server still confirms ownership
/// before it accepts the mutation.
private struct LegendGroupMemberPicker: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    let dismiss: () -> Void

    @State private var search = ""

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                VStack(spacing: 0) {
                    header
                    LegendMessagingSearchField(search: $search, placeholder: LegendSharedDesign.copy("search.connections"))
                    scopes
                    content
                }
            }
            .toolbar(.hidden, for: .navigationBar)
            .task { store.loadRecipients() }
            .onChange(of: search) { _, value in
                store.searchRecipients(value)
            }
        }
    }

    private var header: some View {
        HStack {
            Button(LegendLocalized("Cancel"), action: dismiss)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.white)

            Spacer()

            VStack(spacing: 2) {
                Text(LegendLocalized("Add group member"))
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.white)
                Text(LegendLocalized("Choose one of your connections"))
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.68))
            }

            Spacer()

            Color.clear.frame(width: 48, height: 1)
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.vertical, LegendNextSpacing.md)
        .background(LegendNextGradient.hero)
    }

    private var scopes: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: LegendNextSpacing.xs) {
                ForEach(store.availableRecipientScopes) { scope in
                    Button(store.recipientScopeTitle(scope)) {
                        search = ""
                        store.selectRecipientScope(scope)
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(store.selectedRecipientScope == scope
                                     ? LegendNextColor.midnight
                                     : LegendNextColor.textSecondary)
                    .padding(.horizontal, 11)
                    .frame(minHeight: 32)
                    .background(
                        store.selectedRecipientScope == scope
                            ? AnyShapeStyle(LegendNextGradient.gold)
                            : AnyShapeStyle(LegendNextColor.surfaceElevated),
                        in: Capsule()
                    )
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        }
        .padding(.bottom, LegendNextSpacing.xs)
    }

    @ViewBuilder
    private var content: some View {
        switch store.recipientState {
        case .idle, .loading:
            ProgressView(LegendLocalized("Loading connections"))
                .frame(maxWidth: .infinity, maxHeight: .infinity)

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: LegendLocalized("Retry"),
                retry: { store.loadRecipients(search: search) }
            )
            .padding(LegendNextSpacing.md)

        case .loaded(let recipients):
            LegendScrollView(tracksNavigationChrome: false) {
                LazyVStack(spacing: LegendNextSpacing.sm) {
                    ForEach(recipients) { recipient in
                        Button {
                            store.addGroupParticipant(recipient, to: conversationID) {
                                dismiss()
                            }
                        } label: {
                            LegendContactCard(
                                displayName: recipient.displayName,
                                subtitle: recipient.identity.participantType == .agent
                                    ? publicAgentRoleLabel(
                                        roleLabel: recipient.roleLabel)
                                    : publicRelationshipLabel(recipient.relationshipLabel) ?? LegendLocalized("Connection"),
                                detail: recipient.email,
                                isVerified: recipient.isVerified == true,
                                avatar: {
                                    LegendMessagingAvatar(
                                        participant: MessagingParticipant(
                                            identity: recipient.identity,
                                            profileID: recipient.profileID,
                                            displayName: recipient.displayName,
                                            roleLabel: recipient.roleLabel,
                                            avatar: recipient.avatar,
                                            isVerified: recipient.isVerified),
                                        size: 46,
                                        showsGoldRing: true)
                                },
                                action: {
                                    Image(systemName: "plus.circle.fill")
                                        .font(.title3)
                                        .foregroundStyle(LegendNextColor.gold)
                                }
                            )
                        }
                        .buttonStyle(LegendMessagingPressButtonStyle())
                        .disabled(store.isCreatingGroup)
                    }
                }
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.bottom, LegendNextSpacing.xxl)
            }
        }
    }
}

/// Group management authority is enforced by the server. Owners and
/// delegated collaborators may edit the profile when CanManageMembers is true.
private struct LegendGroupCollaboratorSheet: View {
    @ObservedObject var store: MessagingStore
    let conversation: ConversationDetail
    let currentIdentity: LogicalParticipantIdentity
    let dismiss: () -> Void

    @State private var search = ""

    private var manageableParticipants: [MessagingParticipant] {
        conversation.participants
            .filter { $0.identity != currentIdentity && (search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || $0.displayName.localizedCaseInsensitiveContains(search.trimmingCharacters(in: .whitespacesAndNewlines))) }
            .sorted {
                $0.displayName.localizedCaseInsensitiveCompare(
                    $1.displayName) == .orderedAscending
            }
    }

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                VStack(spacing: 0) {
                    header
                    LegendMessagingSearchField(search: $search, placeholder: LegendSharedDesign.copy("search.groupMembers"))

                    if let failure = store.sendFailure {
                        LegendMessagingStatusBanner(
                            symbol: "exclamationmark.circle.fill",
                            title: failure.title,
                            message: failure.message
                        )
                        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                        .padding(.top, LegendNextSpacing.sm)
                    }

                    LegendScrollView(tracksNavigationChrome: false) {
                        LazyVStack(spacing: LegendNextSpacing.sm) {
                            explanatoryCard

                            ForEach(manageableParticipants) { participant in
                                collaboratorRow(participant)
                            }
                        }
                        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                        .padding(.top, LegendNextSpacing.sm)
                        .padding(.bottom, LegendNextSpacing.xxl)
                    }
                    .scrollIndicators(.hidden)
                }
            }
            .toolbar(.hidden, for: .navigationBar)
        }
    }

    private var header: some View {
        HStack {
            Button(LegendLocalized("Done"), action: dismiss)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.white)

            Spacer()

            VStack(spacing: 2) {
                Text(LegendLocalized("Collaborators"))
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.white)

                Text(LegendLocalized("Co-manage this group"))
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.68))
            }

            Spacer()

            Color.clear
                .frame(width: 42, height: 1)
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.vertical, LegendNextSpacing.md)
        .background(LegendNextGradient.hero)
    }

    private var explanatoryCard: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            Label(
                LegendLocalized("Owner-controlled access"),
                systemImage: "lock.shield.fill")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)

            Text(
                LegendLocalized("Collaborators can edit the group and add members. ")
                + "They cannot appoint other collaborators or delete the group."
            )
            .font(.footnote)
            .foregroundStyle(LegendNextColor.textSecondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(LegendNextSpacing.md)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
    }

    private func collaboratorRow(
        _ participant: MessagingParticipant
    ) -> some View {
        let isManager = participant.isGroupManager == true

        return Button {
            store.setGroupCollaborator(
                participant,
                in: conversation.id,
                isManager: !isManager)
        } label: {
            LegendContactCard(
                displayName: participant.displayName,
                subtitle: participant.identity.participantType == .agent
                    ? publicAgentRoleLabel(roleLabel: participant.roleLabel)
                    : "Group member",
                detail: isManager
                    ? "Collaborator · Can co-manage this group"
                    : "Member",
                isVerified: participant.isVerified == true,
                avatar: {
                    LegendMessagingAvatar(
                        participant: participant,
                        size: 46,
                        showsGoldRing: true)
                },
                action: {
                    if store.isCreatingGroup {
                        ProgressView()
                            .controlSize(.small)
                            .tint(LegendNextColor.gold)
                    } else {
                        HStack(spacing: 5) {
                            Image(
                                systemName: isManager
                                    ? "checkmark.shield.fill"
                                    : "person.badge.plus")
                                .font(.caption.weight(.bold))

                            Text(
                                isManager
                                    ? LegendLocalized("Collaborator")
                                    : LegendLocalized("Make collaborator"))
                                .font(.caption.weight(.bold))
                        }
                        .foregroundStyle(
                            isManager
                                ? LegendNextColor.success
                                : LegendNextColor.gold)
                    }
                }
            )
        }
        .buttonStyle(LegendMessagingPressButtonStyle())
        .disabled(store.isCreatingGroup)
        .accessibilityLabel(
            isManager
                ? LegendLocalized(
                    "Remove {displayName} as collaborator",
                    context: "accessibility copy",
                    arguments: ["displayName": participant.displayName])
                : LegendLocalized(
                    "Make {displayName} a collaborator",
                    context: "accessibility copy",
                    arguments: ["displayName": participant.displayName]))
    }
}

private enum LegendGroupMeetingFrequency: String, CaseIterable, Identifiable {
    case oneTime = "OneTime"
    case daily = "Daily"
    case weekly = "Weekly"
    case biweekly = "Biweekly"
    case monthly = "Monthly"
    case custom = "Custom"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .oneTime: return LegendLocalized("One time")
        case .daily: return LegendLocalized("Daily")
        case .weekly: return LegendLocalized("Weekly")
        case .biweekly: return LegendLocalized("Every other week")
        case .monthly: return LegendLocalized("Monthly")
        case .custom: return LegendLocalized("Custom")
        }
    }

    var usesLocalTime: Bool {
        self != .oneTime && self != .custom
    }

    var needsWeekday: Bool {
        self == .weekly || self == .biweekly
    }
}

private enum LegendGroupMeetingWeekday: String, CaseIterable, Identifiable {
    case sunday = "Sunday"
    case monday = "Monday"
    case tuesday = "Tuesday"
    case wednesday = "Wednesday"
    case thursday = "Thursday"
    case friday = "Friday"
    case saturday = "Saturday"

    var id: String { rawValue }
    var title: String {
        switch self {
        case .sunday: LegendLocalized("Sunday")
        case .monday: LegendLocalized("Monday")
        case .tuesday: LegendLocalized("Tuesday")
        case .wednesday: LegendLocalized("Wednesday")
        case .thursday: LegendLocalized("Thursday")
        case .friday: LegendLocalized("Friday")
        case .saturday: LegendLocalized("Saturday")
        }
    }
}

private struct LegendGroupHostOption: Identifiable {
    let identity: LogicalParticipantIdentity
    let displayName: String
    let detail: String

    var id: LogicalParticipantIdentity { identity }
}

private struct LegendGroupMeetingDraft {
    var hostIdentity: LogicalParticipantIdentity?
    var isMeetingEnabled: Bool
    var linkLabel: String
    var linkURL: String
    var isScheduleEnabled: Bool
    var frequency: LegendGroupMeetingFrequency
    var weekday: LegendGroupMeetingWeekday
    var time: Date
    var timeZoneID: String
    var startDate: Date
    var customDescription: String

    init(meeting: MessagingGroupMeeting? = nil) {
        hostIdentity = meeting?.host.identity
        isMeetingEnabled = meeting?.linkLabel != nil || meeting?.linkURL != nil
        linkLabel = meeting?.linkLabel ?? ""
        linkURL = meeting?.linkURL ?? ""
        isScheduleEnabled = meeting?.schedule != nil
        frequency = meeting?.schedule.flatMap {
            LegendGroupMeetingFrequency(rawValue: $0.frequency)
        } ?? .weekly
        weekday = meeting?.schedule?.weekdays.first.flatMap {
            LegendGroupMeetingWeekday(rawValue: $0)
        } ?? .wednesday
        time = Self.date(for: meeting?.schedule?.localTime) ?? Date()
        timeZoneID = meeting?.schedule?.timeZoneID ?? TimeZone.current.identifier
        startDate = meeting?.schedule?.startsUTC ?? Date()
        customDescription = meeting?.schedule?.customDescription ?? ""
    }

    var isValid: Bool {
        guard isMeetingEnabled else { return true }
        let label = normalized(linkLabel)
        let urlValue = normalized(linkURL)
        guard label != nil,
              let urlValue,
              let url = URL(string: urlValue),
              ["https", "http"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.isEmpty == false else {
            return false
        }

        guard isScheduleEnabled else { return true }
        if frequency.usesLocalTime && normalized(timeZoneID) == nil {
            return false
        }
        if frequency == .custom && normalized(customDescription) == nil {
            return false
        }
        return true
    }

    var request: MessagingGroupMeetingRequest {
        let includesMeeting = isMeetingEnabled
        let schedule: MessagingGroupMeetingScheduleRequest?
        if includesMeeting && isScheduleEnabled {
            schedule = MessagingGroupMeetingScheduleRequest(
                frequency: frequency.rawValue,
                weekdays: frequency.needsWeekday ? [weekday.rawValue] : [],
                localTime: frequency.usesLocalTime ? Self.timeString(from: time) : nil,
                timeZoneID: frequency.usesLocalTime ? normalized(timeZoneID) : nil,
                startsUTC: frequency == .oneTime || frequency == .monthly
                    ? startDate
                    : nil,
                customDescription: frequency == .custom
                    ? normalized(customDescription)
                    : nil)
        } else {
            schedule = nil
        }

        return MessagingGroupMeetingRequest(
            host: hostIdentity.map {
                MessagingGroupMemberRequest(
                    userID: $0.userID,
                    participantType: $0.participantType)
            },
            linkLabel: includesMeeting ? normalized(linkLabel) : nil,
            linkURL: includesMeeting ? normalized(linkURL) : nil,
            schedule: schedule)
    }

    private static func date(for localTime: String?) -> Date? {
        guard let localTime else { return nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "HH:mm"
        return formatter.date(from: localTime)
    }

    private static func timeString(from date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: date)
    }

    private func normalized(_ value: String) -> String? {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
}

private struct LegendGroupMeetingEditor: View {
    @Binding var draft: LegendGroupMeetingDraft
    let hostOptions: [LegendGroupHostOption]
    let canManageMeeting: Bool

    @State private var isPresentingHostPicker = false

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(LegendLocalized("Group host"))
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.textSecondary)
                    Text(hostName)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                }

                Spacer()

                Button(LegendLocalized("Choose by name")) {
                    isPresentingHostPicker = true
                }
                .font(.subheadline.weight(.semibold))
                .buttonStyle(.bordered)
            }

            Toggle(LegendLocalized("Add an online meeting"), isOn: $draft.isMeetingEnabled)
                .font(.subheadline.weight(.semibold))
                .tint(LegendNextColor.gold)

            if draft.isMeetingEnabled {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    TextField(LegendLocalized("Meeting link name (for example, Wednesday Zoom)"), text: $draft.linkLabel)
                        .textInputAutocapitalization(.words)
                        .autocorrectionDisabled()
                    TextField(LegendLocalized("Zoom, Teams, or Google Meet URL"), text: $draft.linkURL)
                        .keyboardType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
                .font(.subheadline)
                .padding(LegendNextSpacing.sm)
                .background(
                    LegendNextColor.surfaceElevated,
                    in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.compact,
                        style: .continuous))

                Toggle(LegendLocalized("Add a recurring schedule"), isOn: $draft.isScheduleEnabled)
                    .font(.subheadline.weight(.semibold))
                    .tint(LegendNextColor.gold)

                if draft.isScheduleEnabled {
                    meetingScheduleControls
                }
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(
            LegendNextColor.surfaceElevated.opacity(0.65),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous))
        .disabled(!canManageMeeting)
        .sheet(isPresented: $isPresentingHostPicker) {
            LegendGroupHostPicker(
                options: hostOptions,
                selectedHost: $draft.hostIdentity)
        }
    }

    @ViewBuilder
    private var meetingScheduleControls: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            Picker(LegendLocalized("Frequency"), selection: $draft.frequency) {
                ForEach(LegendGroupMeetingFrequency.allCases) { frequency in
                    Text(frequency.title).tag(frequency)
                }
            }
            .pickerStyle(.menu)

            if draft.frequency.needsWeekday {
                Picker(LegendLocalized("Day"), selection: $draft.weekday) {
                    ForEach(LegendGroupMeetingWeekday.allCases) { weekday in
                        Text(weekday.title).tag(weekday)
                    }
                }
                .pickerStyle(.menu)
            }

            if draft.frequency == .oneTime {
                DatePicker(
                    "Date and time",
                    selection: $draft.startDate,
                    displayedComponents: [.date, .hourAndMinute])
            } else if draft.frequency == .monthly {
                DatePicker("Starts", selection: $draft.startDate, displayedComponents: .date)
            }

            if draft.frequency.usesLocalTime {
                DatePicker("Time", selection: $draft.time, displayedComponents: .hourAndMinute)
                TextField(LegendLocalized("Time zone"), text: $draft.timeZoneID)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }

            if draft.frequency == .custom {
                TextField(
                    LegendLocalized("Custom schedule (for example, first and third Wednesday)"),
                    text: $draft.customDescription,
                    axis: .vertical)
                    .lineLimit(2...4)
            }
        }
        .font(.subheadline)
        .padding(LegendNextSpacing.sm)
        .background(
            LegendNextColor.canvas,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.compact,
                style: .continuous))
    }

    private var hostName: String {
        guard let hostIdentity = draft.hostIdentity else {
            return LegendLocalized("You (group owner)")
        }
        return hostOptions.first(where: { $0.identity == hostIdentity })?.displayName
            ?? LegendLocalized("Selected group member")
    }
}

private struct LegendGroupHostPicker: View {
    let options: [LegendGroupHostOption]
    @Binding var selectedHost: LogicalParticipantIdentity?

    @Environment(\.dismiss) private var dismiss
    @State private var search = ""

    var body: some View {
        NavigationStack {
            List {
                Button {
                    selectedHost = nil
                    dismiss()
                } label: {
                    Label(LegendLocalized("You (group owner)"), systemImage: "person.crop.circle")
                }

                ForEach(filteredOptions) { option in
                    Button {
                        selectedHost = option.identity
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(option.displayName)
                                .font(.body.weight(.semibold))
                            Text(option.detail)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search group members by name")
            .navigationTitle(LegendLocalized("Choose group host"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(LegendLocalized("Cancel")) {
                        dismiss()
                    }
                }
            }
        }
    }

    private var filteredOptions: [LegendGroupHostOption] {
        let search = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !search.isEmpty else { return options }
        return options.filter {
            $0.displayName.localizedCaseInsensitiveContains(search) ||
                $0.detail.localizedCaseInsensitiveContains(search)
        }
    }
}

private struct LegendGroupProfileEditor: View {
    @ObservedObject var store: MessagingStore
    let conversation: ConversationDetail
    let dismiss: () -> Void

    @State private var subject: String
    @State private var meetingDraft: LegendGroupMeetingDraft
    @State private var selectedPhoto: PhotosPickerItem?
    @State private var replacementImage: MessagingGroupImageRequest?
    @State private var isPreparingReplacementImage = false
    @State private var isShowingGroupPhotoPreparationFailure = false

    init(
        store: MessagingStore,
        conversation: ConversationDetail,
        dismiss: @escaping () -> Void
    ) {
        _store = ObservedObject(wrappedValue: store)
        self.conversation = conversation
        self.dismiss = dismiss
        _subject = State(initialValue: conversation.title)
        _meetingDraft = State(initialValue: LegendGroupMeetingDraft(meeting: conversation.meeting))
    }

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                VStack(spacing: LegendNextSpacing.lg) {
                    PhotosPicker(
                        selection: $selectedPhoto,
                        matching: .images,
                        photoLibrary: PHPhotoLibrary.shared()) {
                            LegendMessagingGroupAvatar(
                                avatar: replacementAvatar ?? conversation.groupAvatar,
                                size: 92)
                        }
                        .accessibilityLabel(LegendLocalized("Change group photo", context: "accessibility copy"))
                        .disabled(isPreparingReplacementImage)

                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        Text(LegendLocalized("Group name"))
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                        TextField(LegendLocalized("Group name"), text: $subject)
                            .textInputAutocapitalization(.words)
                            .padding(.horizontal, LegendNextSpacing.md)
                            .frame(minHeight: 48)
                            .background(
                                LegendNextColor.surfaceElevated,
                                in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous))
                    }

                    if conversation.canManageMeeting == true {
                        LegendGroupMeetingEditor(
                            draft: $meetingDraft,
                            hostOptions: hostOptions,
                            canManageMeeting: true)
                    }

                    Text(
                        conversation.canManageMeeting == true
                            ? LegendLocalized("Only the group owner can set the host, meeting link, and schedule. Collaborators can still manage the group name, photo, and membership.")
                            : LegendLocalized("Group owners and collaborators can manage the group name, photo, and membership."))
                        .font(.footnote)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    Spacer()
                }
                .padding(LegendNextSpacing.pageHorizontal)
            }
            .navigationTitle(LegendLocalized("Group profile"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(LegendLocalized("Cancel")) {
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(store.isCreatingGroup ? LegendLocalized("Saving…") : LegendLocalized("Save")) {
                        store.updateGroup(
                            conversationID: conversation.id,
                            subject: subject,
                            groupImage: replacementImage,
                            meeting: conversation.canManageMeeting == true
                                ? meetingDraft.request
                                : nil,
                            completion: dismiss)
                    }
                    .disabled(
                        store.isCreatingGroup ||
                        isPreparingReplacementImage ||
                        subject.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                        (conversation.canManageMeeting == true && !meetingDraft.isValid))
                }
            }
            .alert(LegendLocalized("Group photo could not be prepared"), isPresented: $isShowingGroupPhotoPreparationFailure) {
                Button(LegendLocalized("OK"), role: .cancel) {}
            } message: {
                Text(LegendLocalized("Choose another photo and try again. Your existing group photo was not changed."))
            }
            .onChange(of: selectedPhoto) { _, item in
                guard let item else { return }
                isPreparingReplacementImage = true
                replacementImage = nil
                Task {
                    let photoData = try? await item.loadTransferable(type: Data.self)
                    guard !Task.isCancelled else { return }

                    replacementImage = legendMessagingGroupImageRequest(from: photoData)
                    isShowingGroupPhotoPreparationFailure = replacementImage == nil
                    isPreparingReplacementImage = false
                    selectedPhoto = nil
                }
            }
        }
    }

    private var replacementAvatar: ProfileAvatar? {
        replacementImage.map {
            ProfileAvatar(
                kind: "inline",
                contentType: $0.contentType,
                base64Content: $0.base64Content)
        }
    }

    private var hostOptions: [LegendGroupHostOption] {
        conversation.participants
            .map { participant in
                LegendGroupHostOption(
                    identity: participant.identity,
                    displayName: participant.displayName,
                    detail: participant.roleLabel ?? LegendLocalized("Group member"))
            }
            .sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }
}

// MARK: - Conversation Thread

struct ConversationThreadView: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.dismiss) private var dismissThread
    @Environment(\.scenePhase) private var scenePhase
    @State private var sharedPostID: UUID?
    @State private var isThreadVisible = false
    @FocusState private var composerIsFocused: Bool
    @State private var draft = ""
    @State private var stagedAttachments: [MessagingAttachmentDraft] = []
    @State private var selectedPhoto: PhotosPickerItem?
    @State private var isImportingFile = false
    @State private var messageForStagedAttachments: ConversationMessage?
    @State private var replyingToMessage: ConversationMessage?
    @State private var isPresentingAddMember = false
    @State private var isPresentingGroupProfile = false
    @State private var isPresentingGroupCollaborators = false
    @State private var isConfirmingDeleteGroup = false
    @State private var isPresentingCallSheet = false
    @State private var isPresentingReceiptPrivacy = false
    @State private var verificationProfile: LegendVerificationProfileRoute?

    var body: some View {
        ZStack {
            LegendNextCanvas()

            threadContent
        }
        .sheet(isPresented: Binding(get: { sharedPostID != nil }, set: { if !$0 { sharedPostID = nil; social.closePost() } })) {
            if let sharedPostID {
                NavigationStack {
                    if let post = social.focusedPost, post.id == sharedPostID {
                        LegendPostDetailView(post: post, currentIdentity: currentIdentity, social: social)
                    } else {
                        VStack(spacing: 16) {
                            Text(social.focusedPostFailure?.message ?? LegendLocalized("Opening shared content…"))
                            if social.focusedPostFailure != nil {
                                Button(LegendLocalized("Retry")) { Task { await social.loadPost(sharedPostID) } }
                            }
                        }.padding()
                    }
                }.task(id: sharedPostID) { await social.loadPost(sharedPostID) }
            }
        }
        .toolbar(.hidden, for: .navigationBar)
        .onAppear { isThreadVisible = true }
        .onDisappear { isThreadVisible = false }
        .task {
            if store.selectedConversationID != conversationID {
                store.openConversation(conversationID)
            }
        }
        .sheet(isPresented: $isPresentingAddMember) {
            LegendGroupMemberPicker(
                store: store,
                conversationID: conversationID,
                dismiss: { isPresentingAddMember = false }
            )
            .legendNextSheetChrome(detents: [.large])
        }
        .sheet(isPresented: $isPresentingGroupProfile) {
            if case .loaded(let conversation) = store.detailState {
                LegendGroupProfileEditor(
                    store: store,
                    conversation: conversation,
                    dismiss: { isPresentingGroupProfile = false })
                .legendNextSheetChrome(detents: [.large])
            }
        }
        .sheet(isPresented: $isPresentingGroupCollaborators) {
            if case .loaded(let conversation) = store.detailState,
               conversation.canManageCollaborators == true {
                LegendGroupCollaboratorSheet(
                    store: store,
                    conversation: conversation,
                    currentIdentity: currentIdentity,
                    dismiss: {
                        isPresentingGroupCollaborators = false
                    }
                )
                .legendNextSheetChrome(detents: [.large])
            }
        }
        .confirmationDialog(
            LegendLocalized("Delete this group?"),
            isPresented: $isConfirmingDeleteGroup,
            titleVisibility: .visible
        ) {
            Button(LegendLocalized("Delete Group for Everyone"), role: .destructive) {
                store.deleteGroup(conversationID: conversationID) {
                    dismissThread()
                }
            }

            Button(LegendLocalized("Cancel"), role: .cancel) {}
        } message: {
            Text(
                LegendLocalized("This permanently closes the group for every member. ")
                + "Only the group owner can perform this action."
            )
        }
        .confirmationDialog(LegendLocalized("Read receipt privacy"), isPresented: $isPresentingReceiptPrivacy, titleVisibility: .visible) {
            if case .loaded(let conversation) = store.detailState, let privacy = conversation.readReceipts {
                Button(privacy.conversationEnabled ? LegendLocalized("Turn off for this chat") : LegendLocalized("Turn on for this chat")) {
                    store.setReadReceipts(conversationID: conversation.id, enabled: !privacy.conversationEnabled, globally: false)
                }
                Button(privacy.globalEnabled ? LegendLocalized("Turn off for all chats") : LegendLocalized("Turn on for all chats")) {
                    store.setReadReceipts(conversationID: conversation.id, enabled: !privacy.globalEnabled, globally: true)
                }
            }
            Button(LegendLocalized("Cancel"), role: .cancel) {}
        } message: {
            Text(LegendLocalized("Your unread counts stay accurate. Turning receipts off for all chats takes precedence over individual chat settings."))
        }
        .sheet(isPresented: $isPresentingCallSheet) {
            if case .loaded(let conversation) = store.detailState {
                LegendConversationCallSheet(
                    store: store,
                    conversationID: conversation.id,
                    fallbackName: conversation.title)
                    .legendNextSheetChrome(detents: [.height(340)])
            }
        }
        .sheet(item: $verificationProfile) { route in
            LegendPublicProfileView(
                profile: route.profile,
                currentIdentity: currentIdentity,
                social: social,
                isFollowing: false,
                verificationReview: route.review,
                resolveVerification: { review, approve, note in
                    await store.resolveVerificationRequest(
                        review,
                        approve: approve,
                        note: note)
                })
                .legendNextSheetChrome(detents: [.large])
        }
    }

    private var selectedTitle: String? {
        guard case .loaded(let summaries) = store.state else { return nil }
        return summaries.first(where: { $0.id == conversationID })?.title
    }

    @ViewBuilder
    private var threadContent: some View {
        switch store.detailState {
        case .idle, .loading:
            LegendConversationLoadingView(title: selectedTitle)

        case .loaded(let conversation):
            if conversation.id == conversationID {
                conversationView(conversation)
            } else {
                LegendConversationLoadingView(title: selectedTitle)
            }

        case .unavailable(let message):
            conversationFailure(
                title: LegendLocalized("Conversation unavailable"),
                message: message,
                canRetry: false
            )

        case .unauthorized(let failure),
             .forbidden(let failure),
             .offline(let failure),
             .failed(let failure):
            conversationFailure(
                title: failure.title,
                message: failure.message,
                canRetry: true
            )
        }
    }

    private func conversationView(
        _ conversation: ConversationDetail
    ) -> some View {
        VStack(spacing: 0) {
            LegendConversationHeader(
                conversation: conversation,
                currentIdentity: currentIdentity,
                addMember: { isPresentingAddMember = true },
                editGroup: { isPresentingGroupProfile = true },
                manageCollaborators: {
                    isPresentingGroupCollaborators = true
                },
                deleteGroup: {
                    isConfirmingDeleteGroup = true
                },
                setGroupPromotion: { isPromoted in
                    store.setGroupPromotion(
                        conversationID: conversationID,
                        isPromoted: isPromoted)
                },
                isFounder: store.isFounder,
                startCall: { isPresentingCallSheet = true },
                receiptPrivacy: { isPresentingReceiptPrivacy = true }
            )

            LegendMessageTimeline(
                messages: conversation.messages.filter { !$0.isDeleted },
                readReceipts: conversation.readReceipts,
                reactionOptions: conversation.reactionOptions ?? [],
                openSharedPost: { sharedPostID = $0 },
                onReact: { message, emoji in store.react(to: message, emoji: emoji) },
                participantAvatar: { identity in
                    conversation.participants.first(where: {
                        $0.identity == identity
                    })?.avatar
                },
                hasOlderMessages: conversation.hasOlderMessages == true,
                isLoadingOlderMessages: store.isLoadingOlderMessages,
                loadOlderMessages: { store.loadOlderMessages() },
                onReply: { message in
                    replyingToMessage = message
                    composerIsFocused = true
                },
                onDelete: { message in
                    store.deleteMessage(message)
                },
                onOpenVerificationProfile: { message in
                    guard let review = message.verificationReview else { return }
                    verificationProfile = LegendVerificationProfileRoute(
                        participant: message.sender,
                        review: review)
                }
            )
            .id(conversation.id)
        }
        .onChange(of: conversation.messages.last?.id, initial: true) { _, _ in
            if isThreadVisible && scenePhase == .active { store.markViewed(conversation.id) }
        }
        .onChange(of: scenePhase) { _, phase in
            if isThreadVisible && phase == .active { store.markViewed(conversation.id) }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if conversation.isClosed {
                closedConversationBanner
            } else {
                composerArea
            }
        }
    }

    private var composerArea: some View {
        VStack(spacing: 0) {
            if let sendFailure = store.sendFailure {
                LegendMessagingStatusBanner(
                    symbol: "exclamationmark.circle.fill",
                    title: sendFailure.title,
                    message: sendFailure.message
                )
                .padding(.top, LegendNextSpacing.xs)
            }

            if MessagingAttachmentDraft.hasPendingAcknowledgedUploads(stagedAttachments) {
                LegendMessagingStatusBanner(
                    symbol: "exclamationmark.circle.fill",
                    title: "Attachments pending",
                    message: "Retry or remove attachments from the previous message before sending another message."
                )
                .padding(.top, LegendNextSpacing.xs)
            }

            if !stagedAttachments.isEmpty {
                LegendMessageAttachmentStaging(
                    attachments: stagedAttachments,
                    onRemove: removeAttachment,
                    onRetry: retryAttachment
                )
                .padding(.top, LegendNextSpacing.xs)
            }

            if let replyingToMessage {
                LegendMessageReplyComposerPreview(
                    message: replyingToMessage,
                    onDismiss: {
                        self.replyingToMessage = nil
                    }
                )
                .padding(.top, LegendNextSpacing.xs)
            }

            messageComposer
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.sm)
        .padding(.bottom, LegendNextSpacing.sm)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) {
            Rectangle()
                .fill(LegendNextColor.separator)
                .frame(height: 0.5)
        }
    }

    private var messageComposer: some View {
        HStack(alignment: .bottom, spacing: LegendNextSpacing.sm) {
            Menu {
                PhotosPicker(
                    selection: $selectedPhoto,
                    matching: .images,
                    photoLibrary: PHPhotoLibrary.shared()
                ) {
                    Label(LegendLocalized("Photo Library"), systemImage: "photo.on.rectangle")
                }

                Button {
                    isImportingFile = true
                } label: {
                    Label(LegendLocalized("Files"), systemImage: "doc")
                }
            } label: {
                Image(systemName: "plus")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .frame(width: 42, height: 42)
                    .background(LegendNextColor.fill, in: Circle())
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .accessibilityLabel(LegendLocalized("Add photo or file", context: "accessibility copy"))

            TextField(
                LegendLocalized("Write a message"),
                text: $draft,
                axis: .vertical
            )
            .font(.body)
            .foregroundStyle(LegendNextColor.textPrimary)
            .lineLimit(1...5)
            .focused($composerIsFocused)
            .textInputAutocapitalization(.sentences)
            .autocorrectionDisabled(false)
            .keyboardType(.default)
            .textContentType(.none)
            .submitLabel(.send)
            .onSubmit {
                if canSend {
                    sendDraft()
                }
            }
            .padding(.horizontal, LegendNextSpacing.md)
            .padding(.vertical, 12)
            .background(
                LegendNextColor.surfaceElevated,
                in: RoundedRectangle(
                    cornerRadius: 22,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: 22,
                    style: .continuous
                )
                .stroke(
                    composerIsFocused
                        ? LegendNextColor.gold.opacity(0.64)
                        : LegendNextColor.separator,
                    lineWidth: 1
                )
            }
            .accessibilityLabel(LegendLocalized("Message", context: "accessibility copy"))

            Button {
                if canSend {
                    sendDraft()
                }
            } label: {
                ZStack {
                    Circle()
                        .fill(
                            canSend
                                ? AnyShapeStyle(LegendNextGradient.gold)
                                : AnyShapeStyle(LegendNextColor.fill)
                        )

                    if store.isSending || store.isUploadingAttachment {
                        ProgressView()
                            .controlSize(.small)
                            .tint(LegendNextColor.midnight)
                    } else {
                        Image(systemName: canSend ? "arrow.up" : "mic.fill")
                            .font(.system(size: 17, weight: .bold))
                            .foregroundStyle(
                                canSend
                                    ? LegendNextColor.midnight
                                    : LegendNextColor.textTertiary
                            )
                    }
                }
                .frame(width: 46, height: 46)
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .disabled(!canSend)
            .accessibilityLabel(
                store.isSending || store.isUploadingAttachment
                    ? "Sending message"
                    : canSend
                        ? "Send message"
                        : "Voice messages are not enabled"
            )
        }
        .onChange(of: selectedPhoto) { _, item in
            guard let item else { return }
            Task { await stagePhoto(item) }
        }
        .fileImporter(
            isPresented: $isImportingFile,
            allowedContentTypes: supportedAttachmentTypes,
            allowsMultipleSelection: true,
            onCompletion: stageFiles
        )
    }

    private var closedConversationBanner: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: "lock.fill")
                .foregroundStyle(LegendNextColor.gold)

            VStack(alignment: .leading, spacing: 2) {
                Text(LegendLocalized("Conversation closed"))
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text(LegendLocalized("New messages cannot be sent in this conversation."))
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }

            Spacer()
        }
        .padding(LegendNextSpacing.md)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) {
            Rectangle()
                .fill(LegendNextColor.separator)
                .frame(height: 0.5)
        }
    }

    private var canSend: Bool {
        !store.isSending &&
        !store.isUploadingAttachment &&
        !MessagingAttachmentDraft.hasPendingAcknowledgedUploads(stagedAttachments) &&
        !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func sendDraft() {
        guard canSend else { return }
        let submittedAttachmentIDs = Set(stagedAttachments.map(\.id))
        let submittedReply = replyingToMessage
        let submittedDraft = MessagingDraftSnapshot(body: draft, replyToMessageID: submittedReply?.id)
        let outgoing = submittedDraft.body.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        guard !outgoing.isEmpty else { return }

        Task {
            guard let message = await store.send(body: outgoing, replyingTo: submittedReply) else { return }
            if submittedDraft.matches(body: draft, replyToMessageID: replyingToMessage?.id) {
                draft = ""
                replyingToMessage = nil
            }
            for index in stagedAttachments.indices where submittedAttachmentIDs.contains(stagedAttachments[index].id) {
                stagedAttachments[index].acknowledgedMessageID = message.id
            }
            messageForStagedAttachments = message
            await uploadStagedAttachments(to: message, attachmentIDs: submittedAttachmentIDs)
        }
    }

    private var supportedAttachmentTypes: [UTType] {
        [.image, .pdf, .plainText] + ["doc", "docx", "xls", "xlsx"]
            .compactMap { UTType(filenameExtension: $0) }
    }

    private func stagePhoto(_ item: PhotosPickerItem) async {
        defer { selectedPhoto = nil }

        guard let data = try? await item.loadTransferable(type: Data.self),
              let image = UIImage(data: data),
              let jpegData = image.jpegData(compressionQuality: 0.88) else {
            return
        }

        stage(MessagingAttachmentDraft(
            fileName: "photo-\(UUID().uuidString).jpg",
            contentType: "image/jpeg",
            data: jpegData
        ))
    }

    private func stageFiles(_ result: Result<[URL], Error>) {
        guard case .success(let urls) = result else { return }

        for url in urls {
            let hasSecurityScope = url.startAccessingSecurityScopedResource()
            defer {
                if hasSecurityScope {
                    url.stopAccessingSecurityScopedResource()
                }
            }

            guard let data = try? Data(contentsOf: url),
                  let fileType = UTType(filenameExtension: url.pathExtension),
                  let contentType = fileType.preferredMIMEType else {
                continue
            }

            stage(MessagingAttachmentDraft(
                fileName: url.lastPathComponent,
                contentType: contentType,
                data: data
            ))
        }
    }

    private func stage(_ attachment: MessagingAttachmentDraft) {
        guard attachment.data.count <= 10 * 1024 * 1024 else {
            return
        }

        stagedAttachments.append(attachment)
    }

    private func removeAttachment(_ attachmentID: UUID) {
        stagedAttachments.removeAll { $0.id == attachmentID }
    }

    private func retryAttachment(_ attachmentID: UUID) {
        guard let message = messageForStagedAttachments else { return }
        Task {
            await uploadStagedAttachments(
                to: message,
                attachmentIDs: [attachmentID]
            )
        }
    }

    private func uploadStagedAttachments(
        to message: ConversationMessage,
        attachmentIDs: Set<UUID>? = nil
    ) async {
        let uploads = stagedAttachments.filter { attachment in
            attachment.canUpload(to: message.id) &&
                (attachmentIDs == nil || attachmentIDs?.contains(attachment.id) == true)
        }

        for attachment in uploads {
            updateAttachment(attachment.id, state: .uploading)

            if await store.upload(attachment: attachment, to: message) != nil {
                removeAttachment(attachment.id)
            } else {
                updateAttachment(
                    attachment.id,
                    state: .failed("Upload failed")
                )
            }
        }
    }

    private func updateAttachment(
        _ attachmentID: UUID,
        state: MessagingAttachmentDraft.State
    ) {
        guard let index = stagedAttachments.firstIndex(where: {
            $0.id == attachmentID
        }) else {
            return
        }

        stagedAttachments[index].state = state
    }

    private func conversationFailure(
        title: String,
        message: String,
        canRetry: Bool
    ) -> some View {
        VStack(spacing: 0) {
            LegendConversationFallbackHeader()

            LegendMessagingEmptyState(
                symbol: "exclamationmark.bubble",
                title: title,
                message: message,
                actionTitle: canRetry ? "Try again" : nil,
                action: canRetry
                    ? { store.openConversation(conversationID) }
                    : nil
            )
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.display)

            Spacer()
        }
    }
}

private struct LegendMessageAttachmentStaging: View {
    let attachments: [MessagingAttachmentDraft]
    let onRemove: (UUID) -> Void
    let onRetry: (UUID) -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: LegendNextSpacing.sm) {
                ForEach(attachments) { attachment in
                    HStack(spacing: LegendNextSpacing.xs) {
                        attachmentSymbol(for: attachment)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(attachment.fileName)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(LegendNextColor.textPrimary)
                                .lineLimit(1)

                            attachmentStatus(for: attachment)
                        }

                        attachmentAction(for: attachment)
                    }
                    .padding(.leading, LegendNextSpacing.sm)
                    .padding(.trailing, LegendNextSpacing.xs)
                    .padding(.vertical, LegendNextSpacing.xs)
                    .frame(maxWidth: 220)
                    .background(
                        LegendNextColor.surfaceElevated,
                        in: RoundedRectangle(cornerRadius: 14, style: .continuous)
                    )
                }
            }
            .padding(.vertical, 2)
        }
        .scrollIndicators(.hidden)
        .accessibilityLabel(LegendLocalized("Attachments ready to send", context: "accessibility copy"))
    }

    @ViewBuilder
    private func attachmentSymbol(
        for attachment: MessagingAttachmentDraft
    ) -> some View {
        if attachment.contentType.hasPrefix("image/"),
           let image = UIImage(data: attachment.data) {
            Image(uiImage: image)
                .resizable()
                .scaledToFill()
                .frame(width: 36, height: 36)
                .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
                .accessibilityHidden(true)
        } else {
            Image(systemName: "doc.fill")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(LegendNextColor.gold)
                .frame(width: 36, height: 36)
                .background(LegendNextColor.fill, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                .accessibilityHidden(true)
        }
    }

    @ViewBuilder
    private func attachmentStatus(
        for attachment: MessagingAttachmentDraft
    ) -> some View {
        switch attachment.state {
        case .ready:
            Text(LegendLocalized("Ready to send"))
                .font(.caption2)
                .foregroundStyle(LegendNextColor.textSecondary)
        case .uploading:
            HStack(spacing: 4) {
                ProgressView()
                    .controlSize(.mini)
                Text(LegendLocalized("Uploading"))
            }
            .font(.caption2)
            .foregroundStyle(LegendNextColor.textSecondary)
        case .failed(let message):
            Text(message)
                .font(.caption2)
                .foregroundStyle(Color(uiColor: .systemRed))
        }
    }

    @ViewBuilder
    private func attachmentAction(
        for attachment: MessagingAttachmentDraft
    ) -> some View {
        switch attachment.state {
        case .uploading:
            EmptyView()
        case .ready:
            Button {
                onRemove(attachment.id)
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .foregroundStyle(LegendNextColor.textTertiary)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Remove {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (attachment.fileName))]))
        case .failed:
            Button {
                onRetry(attachment.id)
            } label: {
                Image(systemName: "arrow.clockwise.circle.fill")
                    .foregroundStyle(LegendNextColor.gold)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(LegendLocalized("Retry {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (attachment.fileName))]))
        }
    }
}

// MARK: - Inbox Components

private struct LegendConversationRow: View {
    let conversation: ConversationSummary

    private let unreadColor = Color(uiColor: .systemRed)

    var body: some View {
        LegendContactCard(
            displayName: conversation.title,
            subtitle: conversation.lastMessagePreview ?? LegendLocalized("Start your conversation"),
            detail: relationshipTitle,
            isVerified: !isGroup && conversation.counterparty.isVerified == true,
            avatar: {
                if isGroup {
                    LegendMessagingGroupAvatar(
                        avatar: conversation.groupAvatar,
                        size: 46)
                } else {
                    LegendMessagingAvatar(
                        participant: conversation.counterparty,
                        size: 46,
                        showsGoldRing: conversation.unreadCount > 0)
                }
            },
            action: {
                VStack(alignment: .trailing, spacing: 5) {
                    if let date = conversation.lastMessageUTC {
                        Text(LegendMessagingDateFormatter.inbox(date))
                            .font(.caption2.weight(conversation.unreadCount > 0 ? .bold : .medium))
                            .foregroundStyle(conversation.unreadCount > 0 ? unreadColor : LegendNextColor.contactAction)
                            .lineLimit(1)
                    }

                    if conversation.isPinned {
                        Image(systemName: "pin.fill")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.gold)
                            .accessibilityLabel(LegendLocalized("Pinned conversation", context: "accessibility copy"))
                    }

                    if conversation.unreadCount > 0 {
                        Text(unreadText)
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .frame(minWidth: 21, minHeight: 21)
                            .padding(.horizontal, conversation.unreadCount > 9 ? 4 : 0)
                            .background(unreadColor, in: Capsule())
                            .accessibilityLabel(LegendLocalized("{value1} unread messages", context: "accessibility copy", arguments: ["value1": String(describing: (conversation.unreadCount))]))
                    } else if conversation.isMuted {
                        Image(systemName: "bell.slash.fill")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textTertiary)
                    } else {
                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendNextColor.contactAction)
                    }
                }
            }
        )
        .accessibilityElement(children: .combine)
    }

    private var relationshipTitle: String? {
        if isGroup {
            return LegendLocalized("Group chat")
        }

        switch conversation.counterparty.identity.participantType {
        case .agent:
            return publicAgentRoleLabel(roleLabel: conversation.counterparty.roleLabel)

        case .client:
            return LegendLocalized("Connection")
        }
    }

    private var isGroup: Bool {
        conversation.conversationType == "Group"
    }

    private var unreadText: String {
        conversation.unreadCount > 99
            ? "99+"
            : "\(conversation.unreadCount)"
    }
}

private struct LegendRecipientRow: View {
    let recipient: MessagingRecipient
    let isStarting: Bool

    var body: some View {
        LegendContactCard(
            displayName: recipient.displayName,
            subtitle: relationshipLabel,
            detail: normalized(recipient.email),
            isVerified: recipient.isVerified == true,
            avatar: {
                LegendMessagingAvatar(
                participant: MessagingParticipant(
                    identity: recipient.identity,
                    profileID: recipient.profileID,
                    displayName: recipient.displayName,
                    roleLabel: recipient.roleLabel,
                        avatar: recipient.avatar,
                        isVerified: recipient.isVerified
                    ),
                    size: 46,
                    showsGoldRing: true
                )
            },
            action: {
                if isStarting {
                    ProgressView()
                        .controlSize(.small)
                        .tint(LegendNextColor.gold)
                } else {
                    HStack(spacing: 5) {
                        Text(
                            recipient.existingConversationID == nil
                                ? LegendLocalized("Message")
                                : LegendLocalized("Open"))
                            .font(.caption.weight(.bold))

                        Image(systemName: "arrow.up.right")
                            .font(.caption2.weight(.bold))
                    }
                    .foregroundStyle(LegendNextColor.midnight)
                    .padding(.horizontal, 10)
                    .frame(minHeight: 30)
                    .background(LegendNextGradient.gold, in: Capsule())
                }
            }
        )
        .accessibilityElement(children: .combine)
    }

    private var relationshipLabel: String? {
        switch recipient.identity.participantType {
        case .agent:
            return publicAgentRoleLabel(roleLabel: recipient.roleLabel)

        case .client:
            return publicRelationshipLabel(recipient.relationshipLabel)
                ?? LegendLocalized("Connection")
        }
    }

    private func publicRelationshipLabel(
        _ value: String?
    ) -> String? {
        guard let value = normalized(value) else {
            return nil
        }

        let blocked = [
            "agent",
            "client",
            "legend agent",
            "legend client"
        ]

        guard !blocked.contains(value.lowercased()) else {
            return nil
        }

        return value
    }

    private func normalized(
        _ value: String?
    ) -> String? {
        guard let value = value?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }

        return value
    }
}

// MARK: - Thread Components

private struct LegendConversationHeader: View {
    let conversation: ConversationDetail
    let currentIdentity: LogicalParticipantIdentity
    let addMember: () -> Void
    let editGroup: () -> Void
    let manageCollaborators: () -> Void
    let deleteGroup: () -> Void
    let setGroupPromotion: (Bool) -> Void
    let isFounder: Bool
    let startCall: () -> Void
    var receiptPrivacy: (() -> Void)? = nil

    @State private var showingMembers = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        HStack(spacing: LegendNextSpacing.md) {
            Button {
                dismiss()
            } label: {
                Image(systemName: "chevron.left")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 42, height: 42)
                    .background(.white.opacity(0.08), in: Circle())
                    .overlay {
                        Circle()
                            .stroke(.white.opacity(0.10), lineWidth: 1)
                    }
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .accessibilityLabel(LegendLocalized("Back", context: "accessibility copy"))

            if isGroup {
                LegendMessagingGroupAvatar(
                    avatar: conversation.groupAvatar,
                    size: 46)
            } else if let counterparty {
                LegendMemberProfileLink(profile: counterparty.legendProfile) {
                    LegendMessagingAvatar(participant: counterparty, size: 46, showsGoldRing: true)
                }.buttonStyle(.plain)
            } else {
                Image(systemName: "person.2.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 46, height: 46)
                    .background(LegendNextGradient.gold, in: Circle())
            }

            VStack(alignment: .leading, spacing: 2) {
                if isGroup {
                    Button { showingMembers = true } label: { titleLabel }.buttonStyle(.plain)
                } else if let counterparty {
                    LegendMemberProfileLink(profile: counterparty.legendProfile) { titleLabel }.buttonStyle(.plain)
                } else { titleLabel }

                if isGroup {
                    HStack(spacing: 5) {
                        Image(systemName: "lock.shield.fill")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.goldBright)

                        Text(conversation.isClosed
                             ? LegendLocalized("CLOSED LEGEND® CONVERSATION")
                             : LegendLocalized("PRIVATE GROUP CHAT"))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.white.opacity(0.72))
                            .lineLimit(1)
                    }

                    if let meeting = conversation.meeting {
                        LegendGroupMeetingHeaderDetail(meeting: meeting)
                    }
                } else {
                    HStack(spacing: 5) {
                        Image(systemName: "lock.shield.fill")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.goldBright)

                        Text(conversation.isClosed
                             ? LegendLocalized("Closed Legend® conversation")
                             : relationshipSubtitle)
                            .font(.caption)
                            .foregroundStyle(.white.opacity(0.72))
                            .lineLimit(1)
                    }
                }
            }

            Spacer()

            Menu {
                if isGroup {
                    Button { showingMembers = true } label: {
                        Label(LegendLocalized("View group members"), systemImage: "person.3.fill")
                    }
                } else {
                    Button(action: startCall) {
                        Label(LegendLocalized("Voice or video call"), systemImage: "phone.fill")
                    }
                }
                if let receiptPrivacy {
                    Button(action: receiptPrivacy) {
                        Label(LegendLocalized("Read receipt privacy"), systemImage: "eye")
                    }
                }
                if conversation.canManageMembers {
                    Button(action: editGroup) {
                        Label(LegendLocalized("Edit Group"), systemImage: "pencil")
                    }

                    Button(action: addMember) {
                        Label(
                            LegendLocalized("Add Members"),
                            systemImage: "person.badge.plus")
                    }
                }

                if conversation.canManageCollaborators == true {
                    Divider()

                    Button(action: manageCollaborators) {
                        Label(
                            LegendLocalized("Collaborators"),
                            systemImage: "person.2.badge.gearshape")
                    }
                }

                if conversation.canDeleteGroup == true {
                    Divider()

                    Button(role: .destructive, action: deleteGroup) {
                        Label(
                            LegendLocalized("Delete Group"),
                            systemImage: "trash")
                    }
                }

                if isFounder, conversation.canManagePromotion == true {
                    Divider()

                    Button {
                        setGroupPromotion(!(conversation.isPromoted ?? false))
                    } label: {
                        Label(
                            conversation.isPromoted == true
                                ? "Stop Promoting Group"
                                : "Promote Group",
                            systemImage: conversation.isPromoted == true
                                ? "megaphone.fill"
                                : "megaphone")
                    }
                }
            } label: {
                Image(systemName: "ellipsis")
                    .font(.body.weight(.bold))
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 38, height: 38)
                    .background(LegendNextGradient.gold, in: Circle())
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .accessibilityLabel(LegendLocalized("Conversation options", context: "accessibility copy"))

        }
        .sheet(isPresented: $showingMembers) {
            NavigationStack {
                List(conversation.participants) { member in
                    LegendMemberProfileLink(profile: member.legendProfile) {
                        HStack {
                            LegendMessagingAvatar(participant: member, size: 42, showsGoldRing: true)
                            Text(member.displayName).font(.headline)
                            Spacer()
                            Image(systemName: "chevron.right")
                        }
                    }.buttonStyle(.plain)
                }
                .navigationTitle(LegendLocalized("Group members"))
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button(LegendLocalized("Done")) { showingMembers = false } } }
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.xs)
        .padding(.bottom, LegendNextSpacing.md)
        .background {
            LegendNextGradient.hero
                .clipShape(
                    UnevenRoundedRectangle(
                        bottomLeadingRadius: 24,
                        bottomTrailingRadius: 24
                    )
                )
                .ignoresSafeArea(edges: .top)
        }
    }

    private var titleLabel: some View {
        LegendVerifiedName(conversation.title,
            isVerified: !isGroup && counterparty?.isVerified == true,
            font: .system(.headline, design: .rounded).weight(.bold),
            textColor: .white, badgePlacement: .alongsideProfileImage)
    }

    private var counterparty: MessagingParticipant? {
        conversation.participants.first { $0.identity != currentIdentity }
    }

    private var relationshipSubtitle: String {
        guard let counterparty else {
            return LegendLocalized("Private Legend® conversation")
        }

        switch counterparty.identity.participantType {
        case .agent:
            return publicAgentRoleLabel(roleLabel: counterparty.roleLabel)
                ?? LegendLocalized("Private Legend® conversation")

        case .client:
            return LegendLocalized("Private connection")
        }
    }

    private var isGroup: Bool {
        conversation.conversationType == "Group"
    }

    private func normalized(
        _ value: String?
    ) -> String? {
        guard let value = value?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }

        return value
    }
}

private struct LegendGroupMeetingHeaderDetail: View {
    let meeting: MessagingGroupMeeting

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            if let link = meetingLink {
                Link(destination: link.url) {
                    Label(link.label, systemImage: "video.fill")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.goldBright)
                        .lineLimit(1)
                }
                .accessibilityLabel(LegendLocalized("Open {value1}", context: "accessibility copy", arguments: ["value1": String(describing: (link.label))]))
            }

            Text(detail)
                .font(.caption2)
                .foregroundStyle(.white.opacity(0.66))
                .lineLimit(2)
        }
        .padding(.top, 1)
    }

    private var meetingLink: (label: String, url: URL)? {
        guard let label = normalized(meeting.linkLabel),
              let value = normalized(meeting.linkURL),
              let url = URL(string: value),
              ["https", "http"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.isEmpty == false else {
            return nil
        }
        return (label, url)
    }

    private var detail: String {
        let host = LegendLocalized(
            "Hosted by {displayName}",
            arguments: ["displayName": meeting.host.displayName])
        guard let schedule = meeting.schedule else { return host }
        return "\(host) · \(scheduleDescription(schedule))"
    }

    private func scheduleDescription(_ schedule: MessagingGroupMeetingSchedule) -> String {
        let time = displayTime(schedule.localTime)
        let zone = normalized(schedule.timeZoneID)
        let timeDetail = [time, zone].compactMap { $0 }.joined(separator: " ")

        switch schedule.frequency {
        case "OneTime":
            if let startsUTC = schedule.startsUTC {
                return startsUTC.formatted(
                    .dateTime
                        .year().month(.abbreviated).day()
                        .hour().minute()
                        .locale(LegendActiveLocale()))
            }
            return LegendLocalized("One-time meeting")
        case "Daily":
            return timeDetail.isEmpty
                ? LegendLocalized("Daily")
                : LegendLocalized("Daily at {time}", arguments: ["time": timeDetail])
        case "Weekly":
            return timeDetail.isEmpty
                ? LegendLocalized("Weekly on {weekdays}", arguments: ["weekdays": weekdayText(schedule)])
                : LegendLocalized("Weekly on {weekdays} at {time}", arguments: ["weekdays": weekdayText(schedule), "time": timeDetail])
        case "Biweekly":
            return timeDetail.isEmpty
                ? LegendLocalized("Every other {weekdays}", arguments: ["weekdays": weekdayText(schedule)])
                : LegendLocalized("Every other {weekdays} at {time}", arguments: ["weekdays": weekdayText(schedule), "time": timeDetail])
        case "Monthly":
            return timeDetail.isEmpty
                ? LegendLocalized("Monthly")
                : LegendLocalized("Monthly at {time}", arguments: ["time": timeDetail])
        case "Custom":
            return normalized(schedule.customDescription) ?? LegendLocalized("Custom schedule")
        default:
            return LegendLocalized("Scheduled meeting")
        }
    }

    private func weekdayText(_ schedule: MessagingGroupMeetingSchedule) -> String {
        schedule.weekdays.isEmpty
            ? LegendLocalized("selected days")
            : schedule.weekdays.map(localizedWeekday).joined(separator: ", ")
    }

    private func localizedWeekday(_ value: String) -> String {
        switch value.lowercased() {
        case "monday": return LegendLocalized("Monday")
        case "tuesday": return LegendLocalized("Tuesday")
        case "wednesday": return LegendLocalized("Wednesday")
        case "thursday": return LegendLocalized("Thursday")
        case "friday": return LegendLocalized("Friday")
        case "saturday": return LegendLocalized("Saturday")
        case "sunday": return LegendLocalized("Sunday")
        default: return value
        }
    }

    private func displayTime(_ value: String?) -> String? {
        guard let value = normalized(value) else { return nil }
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX")
        parser.dateFormat = "HH:mm"
        guard let date = parser.date(from: value) else { return value }
        let formatter = DateFormatter()
        formatter.locale = LegendActiveLocale()
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }

    private func normalized(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }
        return value
    }
}

private struct LegendConversationFallbackHeader: View {
    var title: String? = nil
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        HStack {
            Button {
                dismiss()
            } label: {
                Image(systemName: "chevron.left")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 42, height: 42)
                    .background(.white.opacity(0.08), in: Circle())
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .accessibilityLabel(LegendLocalized("Back", context: "accessibility copy"))

            Text(title ?? LegendLocalized("Conversation"))
                .font(.system(.headline, design: .rounded).weight(.bold))
                .foregroundStyle(.white)

            Spacer()
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.xs)
        .padding(.bottom, LegendNextSpacing.md)
        .background {
            LegendNextGradient.hero
                .clipShape(
                    UnevenRoundedRectangle(
                        bottomLeadingRadius: 24,
                        bottomTrailingRadius: 24
                    )
                )
                .ignoresSafeArea(edges: .top)
        }
    }
}

private struct LegendMessageTimeline: View {
    let messages: [ConversationMessage]
    var readReceipts: MessagingReadReceiptSettings? = nil
    let reactionOptions: [String]
    let openSharedPost: (UUID) -> Void
    let onReact: (ConversationMessage, String?) -> Void
    let participantAvatar: (LogicalParticipantIdentity) -> ProfileAvatar?
    let hasOlderMessages: Bool
    let isLoadingOlderMessages: Bool
    let loadOlderMessages: () -> Void
    let onReply: (ConversationMessage) -> Void
    let onDelete: (ConversationMessage) -> Void
    let onOpenVerificationProfile: (ConversationMessage) -> Void

    var body: some View {
        let receiptLabels = messageReceiptLabels(messages: messages, readers: readReceipts?.readers ?? [])
        ScrollViewReader { proxy in
            LegendScrollView(tracksNavigationChrome: false) {
                LazyVStack(spacing: 2) {
                    if hasOlderMessages {
                        Button(action: loadOlderMessages) {
                            if isLoadingOlderMessages {
                                Text(LegendLocalized("Loading earlier messages…"))
                            } else {
                                Label(LegendLocalized("Load earlier messages"), systemImage: "arrow.up.circle")
                                    .font(.caption.weight(.semibold))
                            }
                        }
                        .foregroundStyle(LegendNextColor.gold)
                        .disabled(isLoadingOlderMessages)
                        .padding(.vertical, LegendNextSpacing.sm)
                    }

                    if messages.isEmpty {
                        LegendMessagingEmptyState(
                            symbol: "bubble.left.and.bubble.right.fill",
                            title: LegendLocalized("Begin the conversation"),
                            message: LegendLocalized("Your messages will appear here."),
                            actionTitle: nil,
                            action: nil
                        )
                        .padding(.top, LegendNextSpacing.display)
                    } else {
                        ForEach(Array(messages.enumerated()), id: \.element.id) {
                            index,
                            message in

                            if shouldShowDateSeparator(
                                at: index,
                                messages: messages
                            ) {
                                LegendMessageDateSeparator(
                                    date: message.sentUTC
                                )
                            }

                            LegendMessageBubble(
                                message: message,
                                senderAvatar: participantAvatar(message.sender.identity),
                                showsSender: shouldShowSender(
                                    at: index,
                                    messages: messages
                                ),
                                onReply: {
                                    onReply(message)
                                },
                                onDelete: {
                                    onDelete(message)
                                },
                                onReact: { emoji in onReact(message, emoji) },
                                reactionOptions: reactionOptions,
                                openSharedPost: openSharedPost,
                                onOpenVerificationProfile: message.verificationReview?.status != "Pending"
                                    ? nil
                                    : { onOpenVerificationProfile(message) }
                            )
                            .id(message.id)
                            if let label = receiptLabels[message.id] {
                                Text(LegendLocalized(label))
                                    .font(.caption2.weight(.semibold))
                                    .foregroundStyle(label == "Read" ? LegendNextColor.success : LegendNextColor.danger)
                                    .padding(.horizontal, 8).padding(.vertical, 2)
                                    .background((label == "Read" ? LegendNextColor.success : LegendNextColor.danger).opacity(0.12), in: Capsule())
                                    .frame(maxWidth: .infinity, alignment: .trailing)
                            }
                        }
                    }

                    Color.clear
                        .frame(height: 1)
                        .id("MESSAGES_BOTTOM")
                }
                .padding(.horizontal, 12)
                .padding(.top, 8)
                .padding(.bottom, 10)
            }
            .scrollDismissesKeyboard(.interactively)
            .scrollIndicators(.hidden)
            .defaultScrollAnchor(.bottom)
            .onChange(of: messages.last?.id) { oldValue, newValue in
                guard oldValue != nil, newValue != nil else { return }
                scrollToBottom(proxy, animated: true)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func scrollToBottom(
        _ proxy: ScrollViewProxy,
        animated: Bool
    ) {
        let action = {
            proxy.scrollTo("MESSAGES_BOTTOM", anchor: .bottom)
        }

        if animated {
            withAnimation(.easeOut(duration: 0.25), action)
        } else {
            action()
        }
    }

    private func shouldShowDateSeparator(
        at index: Int,
        messages: [ConversationMessage]
    ) -> Bool {
        guard index > 0 else { return true }

        return !Calendar.current.isDate(
            messages[index - 1].sentUTC,
            inSameDayAs: messages[index].sentUTC
        )
    }

    private func shouldShowSender(
        at index: Int,
        messages: [ConversationMessage]
    ) -> Bool {
        guard !messages[index].isMine else { return false }
        guard index > 0 else { return true }

        return messages[index - 1].sender.id != messages[index].sender.id ||
            messages[index - 1].isMine
    }
}

private struct LegendMessageDateSeparator: View {
    let date: Date

    var body: some View {
        Text(LegendMessagingDateFormatter.threadDate(date))
            .font(.caption2.weight(.semibold))
            .foregroundStyle(LegendNextColor.chatTimestamp)
            .padding(.horizontal, 9)
            .padding(.vertical, 3)
            .background(LegendNextColor.chatTimestamp.opacity(0.10), in: Capsule())
            .frame(maxWidth: .infinity)
            .padding(.vertical, 4)
    }
}


private struct LegendMessageReplyComposerPreview: View {
    @Environment(\.colorScheme) private var colorScheme

    let message: ConversationMessage
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            RoundedRectangle(
                cornerRadius: 2,
                style: .continuous
            )
            .fill(LegendNextColor.gold)
            .frame(width: 3)
            .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 5) {
                    Image(systemName: "arrowshape.turn.up.left.fill")
                        .font(.system(size: 9, weight: .bold))

                    Text(LegendLocalized("Replying to {value1}", arguments: ["value1": String(describing: (replyTargetName))]))
                        .font(.caption.weight(.bold))
                }
                .foregroundStyle(LegendNextColor.gold)

                Text(message.body)
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
            }

            Spacer(minLength: LegendNextSpacing.sm)

            Button(action: onDismiss) {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .frame(width: 28, height: 28)
                    .background(
                        LegendNextColor.fill,
                        in: Circle()
                    )
            }
            .buttonStyle(.plain)
            .contentShape(Circle())
            .accessibilityLabel(LegendLocalized("Cancel reply", context: "accessibility copy"))
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .padding(.vertical, 10)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: 16,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: 16,
                style: .continuous
            )
            .stroke(
                LegendNextColor.gold.opacity(0.32),
                lineWidth: 1
            )
        }
        .shadow(
            color: LegendNextColor.ambientShadow(
                for: colorScheme
            ),
            radius: 14,
            x: 0,
            y: 5
        )
    }

    private var replyTargetName: String {
        message.isMine ? "yourself" : message.sender.displayName
    }
}

private struct LegendMessageContextPreview: View {
    @Environment(\.colorScheme) private var colorScheme

    let message: ConversationMessage

    var body: some View {
        VStack(
            alignment: message.isMine ? .trailing : .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendMemberProfileLink(profile: message.sender.legendProfile) {
                LegendVerifiedName(message.sender.displayName, isVerified: message.sender.isVerified == true,
                    font: .caption.weight(.semibold), textColor: LegendNextColor.textSecondary)
            }.buttonStyle(.plain)

            Text(message.body)
                .font(.body)
                .foregroundStyle(
                    message.isMine
                        ? LegendNextColor.midnight
                        : Color.white
                )
                .multilineTextAlignment(.leading)
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .background(
                    message.isMine
                        ? LegendNextColor.gold
                        : LegendNextColor.navy,
                    in: RoundedRectangle(
                        cornerRadius: 16,
                        style: .continuous
                    )
                )
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: 320)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: 22,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: 22,
                style: .continuous
            )
            .stroke(
                LegendNextColor.gold.opacity(0.24),
                lineWidth: 1
            )
        }
        .shadow(
            color: LegendNextColor.ambientShadow(
                for: colorScheme
            ),
            radius: 20,
            x: 0,
            y: 10
        )
    }
}

private struct LegendMessageBubble: View {
    @Environment(\.legendMessagingStore) private var messaging
    @State private var sharingAttachments = false

    let message: ConversationMessage
    let senderAvatar: ProfileAvatar?
    let showsSender: Bool
    let onReply: () -> Void
    let onDelete: () -> Void
    let onReact: (String?) -> Void
    let reactionOptions: [String]
    let openSharedPost: (UUID) -> Void
    let onOpenVerificationProfile: (() -> Void)?

    @State private var copyFeedbackTrigger = 0
    @State private var isShowingOriginal = false
    @State private var emojiPicker = false
    @State private var emojiDraft = ""
    @State private var actionMenu = false

    private let senderBubbleColor = LegendNextColor.gold

    private let recipientBubbleColor = LegendNextColor.navy

    private let accessoryColumnWidth: CGFloat = 24
    private let bubbleSpacing: CGFloat = 5
    private let opposingGutter: CGFloat = 52

    var body: some View {
        HStack(alignment: .bottom, spacing: bubbleSpacing) {
            leadingAccessoryColumn

            if message.isMine {
                Spacer(minLength: opposingGutter)
            }

            bubbleContent
                .layoutPriority(1)

            if !message.isMine {
                Spacer(minLength: opposingGutter)
            }

            trailingAccessoryColumn
        }
        .frame(maxWidth: .infinity)
        .contentShape(
            RoundedRectangle(
                cornerRadius: 18,
                style: .continuous
            )
        )
        .onTapGesture(count: 2) { if !message.isDeleted { onReact("❤️") } }
        .sheet(isPresented: $emojiPicker) {
            VStack(spacing: 16) {
                Text(LegendLocalized("Choose a reaction")).font(.headline)
                TextField(LegendLocalized("Emoji"), text: $emojiDraft).textFieldStyle(.roundedBorder)
                Button(LegendLocalized("React")) { onReact(emojiDraft.trimmingCharacters(in: .whitespacesAndNewlines)); emojiPicker = false }
                    .disabled(emojiDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }.padding().presentationDetents([.height(220)])
        }
        .sheet(isPresented: $sharingAttachments) {
            NavigationStack {
                List(message.attachments) { attachment in
                    LegendMessageAttachmentChip(attachment: attachment, isMine: false)
                }
                .navigationTitle(LegendLocalized("Open or share attachments"))
            }
        }
        .onLongPressGesture(minimumDuration: 0.35) { if !message.isDeleted { actionMenu = true } }
        .popover(isPresented: $actionMenu) {
          VStack(alignment: .leading, spacing: 12) {
            LegendMessageContextPreview(message: message).frame(maxHeight: 90).clipped()
            if !message.isDeleted {
                ScrollView(.horizontal, showsIndicators: false) {
                  HStack(spacing: 6) {
                    ForEach(reactionOptions, id: \.self) { emoji in
                        Button { actionMenu = false; onReact(emoji) } label: {
                            Text(emoji).font(.system(size: 25))
                                .frame(width: 40, height: 44)
                                .background(message.reactions.contains { $0.emoji == emoji && $0.reactedByCurrentActor }
                                            ? LegendNextColor.gold.opacity(0.22) : LegendNextColor.surface, in: RoundedRectangle(cornerRadius: 14))
                        }.buttonStyle(.plain)
                    }
                    Button { actionMenu = false; emojiPicker = true } label: {
                        Image(systemName: "plus.circle.fill").font(.title2).frame(width: 40, height: 44)
                            .accessibilityLabel(LegendLocalized("More reactions"))
                    }
                  }
                }
                if message.reactions.contains(where: { $0.reactedByCurrentActor }) {
                    Button(LegendLocalized("Remove reaction")) { actionMenu = false; onReact(nil) }
                }
                Divider()
                Button {
                    actionMenu = false; onReply()
                } label: {
                    Label(
                        LegendLocalized("Reply"),
                        systemImage: "arrowshape.turn.up.left.fill"
                    )
                }

                Button {
                    actionMenu = false
                    UIPasteboard.general.string = message.body
                    copyFeedbackTrigger += 1
                } label: {
                    Label(
                        LegendLocalized("Copy"),
                        systemImage: "doc.on.doc.fill"
                    )
                }

                if let shared = message.sharedContent, let url = messaging?.sharedPostURL(shared.sourcePostId) {
                    ShareLink(item: url) { Label(LegendLocalized("Share content"), systemImage: "square.and.arrow.up") }
                } else if !message.attachments.isEmpty {
                    Button { sharingAttachments = true } label: {
                        Label(LegendLocalized("Open or share attachments"), systemImage: "square.and.arrow.up")
                    }
                } else {
                    ShareLink(item: message.body) {
                        Label(LegendLocalized("Share message"), systemImage: "square.and.arrow.up")
                    }
                }
                if let original = message.originalBody, original != message.body {
                    Button { UIPasteboard.general.string = original } label: {
                        Label(LegendLocalized("Copy original text"), systemImage: "text.quote")
                    }
                }
                if message.isMine {
                    Divider()
                    Button(role: .destructive) { actionMenu = false; onDelete() } label: {
                        Label(LegendLocalized("Unsend"), systemImage: "trash")
                    }
                }
            }
          }
          .font(.subheadline.weight(.semibold))
          .buttonStyle(.plain)
          .foregroundStyle(LegendNextColor.textPrimary)
          .padding(16).frame(width: 340)
          .presentationCompactAdaptation(.popover)
        }
        .sensoryFeedback(
            .success,
            trigger: copyFeedbackTrigger
        )
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityDescription)
        .accessibilityAction(named: "Reply") {
            if !message.isDeleted { onReply() }
        }
        .accessibilityAction(named: "Copy") {
            if !message.isDeleted {
                UIPasteboard.general.string = message.body
                copyFeedbackTrigger += 1
            }
        }
        .accessibilityAction(named: "Unsend") {
            if message.isMine && !message.isDeleted { onDelete() }
        }
    }

    private var bubbleContent: some View {
        VStack(
            alignment: message.isMine ? .trailing : .leading,
            spacing: 1
        ) {
            VStack(
                alignment: message.isMine ? .trailing : .leading,
                spacing: LegendNextSpacing.xs
            ) {
                if !displayedMessageBody.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Text(displayedMessageBody)
                    .textSelection(.enabled)
                    .font(.system(size: 15, weight: .regular))
                    .italic(message.isDeleted)
                    .lineSpacing(0)
                    .foregroundStyle(
                        isMediaMessage ? LegendNextColor.textPrimary : message.isMine ? LegendNextColor.midnight : Color.white
                    )
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)

                }
                if !message.isDeleted, let shared = message.sharedContent {
                    LegendSharedMessageCard(content: shared, reactionBadge: AnyView(reactionBadges), open: { openSharedPost(shared.sourcePostId) })
                }
                if !message.isDeleted {
                    ForEach(message.attachments) { attachment in
                        LegendMessageAttachmentChip(
                            attachment: attachment,
                            isMine: message.isMine
                        )
                    }
                }

                if let translation = message.translation,
                   message.originalBody != nil {
                    Button {
                        isShowingOriginal.toggle()
                    } label: {
                        Label(
                            isShowingOriginal
                                ? "View translation"
                                : "Translated from \(languageName(translation.originalLanguage)) · View original",
                            systemImage: "character.bubble")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(isMediaMessage ? LegendNextColor.textPrimary : message.isMine ? LegendNextColor.midnight : LegendNextColor.goldBright)
                            .padding(.top, 2)
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint(LegendLocalized("Switch between the translated and original message", context: "accessibility copy"))
                }

                if let onOpenVerificationProfile,
                   let review = message.verificationReview {
                    Button(action: onOpenVerificationProfile) {
                        Label(
                            review.status == "Pending"
                                ? "Open \(review.resourceType.displayName) profile"
                                : "Open reviewed profile",
                            systemImage: "person.crop.circle")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(message.isMine
                                             ? LegendNextColor.midnight
                                             : LegendNextColor.goldBright)
                            .padding(.top, 2)
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint(LegendLocalized("Open this member’s profile for {value1} review", context: "accessibility copy", arguments: ["value1": String(describing: (review.resourceType.displayName))]))
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(
                isMediaMessage ? Color.clear : bubbleColor,
                in: RoundedRectangle(
                    cornerRadius: 16,
                    style: .continuous
                )
            )

            .overlay(alignment: isMediaMessage ? .bottomTrailing : .topTrailing) {
                if !message.reactions.isEmpty && message.sharedContent == nil {
                    reactionBadges.offset(x: isMediaMessage ? -8 : 3, y: isMediaMessage ? -8 : -13)
                }
            }
            .padding(.top, message.reactions.isEmpty || isMediaMessage ? 0 : 13)

            Text(
                message.sentUTC,
                format: .dateTime.hour().minute()
            )
            .font(.system(size: 10, weight: .regular))
            .foregroundStyle(LegendNextColor.chatTimestamp)
            .padding(
                message.isMine ? .trailing : .leading,
                3
            )
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private var reactionBadges: some View {
                    HStack(spacing: 3) {
                        ForEach(message.reactions, id: \.emoji) { reaction in
                            Button { onReact(reaction.reactedByCurrentActor ? nil : reaction.emoji) } label: {
                                HStack(spacing: 2) {
                                    Text(reaction.emoji)
                                    if reaction.count > 1 { Text("\(reaction.count)").font(.caption2.bold()) }
                                }
                                .font(.system(size: 15))
                                .padding(.horizontal, 6).padding(.vertical, 3)
                                .background(LegendNextColor.surface, in: Capsule())
                                .overlay(Capsule().stroke(LegendNextColor.chatTimestamp.opacity(reaction.reactedByCurrentActor ? 1 : 0.3), lineWidth: 1))
                            }.buttonStyle(.plain)
                        }
                    }
    }

    private var isMediaMessage: Bool { !message.isDeleted && (message.sharedContent != nil || !message.attachments.isEmpty) }

    private var displayedMessageBody: String {
        isShowingOriginal ? (message.originalBody ?? message.body) : message.body
    }

    private func languageName(_ code: String) -> String {
        LegendActiveLocale().localizedString(forLanguageCode: code) ?? code
    }

    @ViewBuilder
    private var leadingAccessoryColumn: some View {
        if message.isMine {
            Color.clear
                .frame(
                    width: accessoryColumnWidth,
                    height: 1
                )
                .accessibilityHidden(true)
        } else {
            incomingAvatar
        }
    }

    private var trailingAccessoryColumn: some View {
        Color.clear
            .frame(
                width: accessoryColumnWidth,
                height: 1
            )
            .accessibilityHidden(true)
    }

    @ViewBuilder
    private var incomingAvatar: some View {
        if showsSender {
            LegendMemberProfileLink(profile: message.sender.legendProfile) {
            LegendAvatarImageContent(
                avatar: senderAvatar
            ) {
                Text(senderInitials)
                    .font(.system(
                        size: 8,
                        weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity)
                    .background(recipientBubbleColor)
            }
            .frame(
                width: accessoryColumnWidth,
                height: accessoryColumnWidth
            )
            .clipShape(Circle())
            .padding(.bottom, 13)
            }.buttonStyle(.plain).accessibilityLabel(message.sender.displayName)
        } else {
            Color.clear
                .frame(
                    width: accessoryColumnWidth,
                    height: 1
                )
                .accessibilityHidden(true)
        }
    }

    private var bubbleColor: Color {
        message.isMine
            ? senderBubbleColor
            : recipientBubbleColor
    }

    private var senderInitials: String {
        message.sender.displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()
    }

    private var accessibilityDescription: String {
        if message.isDeleted {
            return message.isMine
                ? LegendLocalized("Sent message unsent.", context: "accessibility copy")
                : LegendLocalized(
                    "Received message unsent by {displayName}.",
                    context: "accessibility copy",
                    arguments: ["displayName": message.sender.displayName])
        }

        let attachmentDescription = message.attachments.isEmpty
            ? ""
            : LegendLocalized(
                message.attachments.count == 1
                    ? " Includes {count} attachment."
                    : " Includes {count} attachments.",
                context: "accessibility copy",
                arguments: ["count": message.attachments.count])

        if message.isMine {
            return LegendLocalized(
                "Sent message. {body}{attachmentDescription}",
                context: "accessibility copy",
                arguments: ["body": message.body, "attachmentDescription": attachmentDescription])
        }

        return LegendLocalized(
            "Received message from {displayName}. {body}{attachmentDescription}",
            context: "accessibility copy",
            arguments: [
                "displayName": message.sender.displayName,
                "body": message.body,
                "attachmentDescription": attachmentDescription
            ])
    }
}

private struct LegendMessageAttachmentChip: View {
    @Environment(\.legendMessagingStore) private var messaging
    @State private var inlineImage: UIImage?
    @State private var previewURL: URL?
    @State private var downloadedURL: URL?
    @State private var isLoading = false
    @State private var failed = false

    let attachment: MessagingAttachment
    let isMine: Bool

    private var isImage: Bool {
        attachment.contentType.hasPrefix("image/")
    }

    private var scanStatusLabel: String {
        "\(attachment.scanStatus.capitalized) scan"
    }

    var body: some View {
        Button {
            guard let messaging, attachment.canDownload, !isLoading else { return }
            failed = false
            isLoading = true
            Task {
                defer { isLoading = false }
                do { let url: URL
                    if let downloadedURL { url = downloadedURL } else { url = try await messaging.downloadAttachment(attachment) }; downloadedURL = url; previewURL = url }
                catch { failed = true }
            }
        } label: {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
        if let inlineImage {
            Image(uiImage: inlineImage).resizable().scaledToFit().frame(maxWidth: 300, maxHeight: 320).clipShape(RoundedRectangle(cornerRadius: 12))
        }
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: isImage ? "photo.fill" : "doc.fill")
                .font(.caption.weight(.semibold))

            Text(attachment.originalFileName)
                .lineLimit(1)

            Text(scanStatusLabel)
                .font(.caption2.weight(.semibold))
                .opacity(0.78)
        }
        }
        .font(.caption.weight(.medium))
        .foregroundStyle(LegendNextColor.textPrimary)
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, 6)
        .background(
            inlineImage != nil ? Color.clear : isMine
                ? LegendNextColor.midnight.opacity(0.09)
                : Color.white.opacity(0.14),
            in: Capsule()
        )
        .overlay {
            Capsule()
                .stroke(
                    inlineImage != nil ? Color.clear : isMine
                        ? LegendNextColor.midnight.opacity(0.12)
                        : Color.white.opacity(0.12),
                    lineWidth: 0.75
                )
        }
        .accessibilityLabel(
            "\(attachment.originalFileName), \(scanStatusLabel)"
        )
        }
        .disabled(!attachment.canDownload || isLoading)
        .task(id: attachment.id) {
            guard isImage, attachment.canDownload, attachment.sizeBytes <= 8 * 1_024 * 1_024, let messaging else { return }
            do {
                let url = try await messaging.downloadAttachment(attachment)
                guard !Task.isCancelled else { try? FileManager.default.removeItem(at: url); return }
                downloadedURL = url
                if let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                   let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 1024, kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary) {
                    inlineImage = UIImage(cgImage: image)
                }
            } catch { if !Task.isCancelled { failed = true } }
        }
        .onDisappear {
            if previewURL == nil, let downloadedURL { try? FileManager.default.removeItem(at: downloadedURL.deletingLastPathComponent()); self.downloadedURL = nil }
        }
        .quickLookPreview($previewURL)
        .onChange(of: previewURL) { _, current in
            if current == nil, let downloadedURL {
                try? FileManager.default.removeItem(at: downloadedURL.deletingLastPathComponent())
                self.downloadedURL = nil
            }
        }
        .alert(LegendLocalized("Attachment unavailable"), isPresented: $failed) {
            Button(LegendLocalized("OK"), role: .cancel) { }
        }

    }
}

private struct LegendConversationLoadingView: View {
    var title: String? = nil
    var body: some View {
        VStack(spacing: 0) {
            LegendConversationFallbackHeader(title: title)

            VStack(spacing: LegendNextSpacing.sm) {
                ForEach(0..<5, id: \.self) { index in
                    HStack {
                        if index.isMultiple(of: 2) {
                            Spacer(minLength: 72)
                        }

                        RoundedRectangle(
                            cornerRadius: 18,
                            style: .continuous
                        )
                        .fill(LegendNextColor.surfaceElevated)
                        .frame(
                            width: index.isMultiple(of: 2) ? 210 : 180,
                            height: index.isMultiple(of: 3) ? 68 : 48
                        )
                        .legendNextShimmer()

                        if !index.isMultiple(of: 2) {
                            Spacer(minLength: 72)
                        }
                    }
                }

                Spacer()
            }
            .padding(LegendNextSpacing.pageHorizontal)
        }
        .accessibilityLabel(LegendLocalized("Loading conversation", context: "accessibility copy"))
    }
}

// MARK: - Shared Messaging Components

private struct LegendMessagingAvatar: View {
    let participant: MessagingParticipant
    let size: CGFloat
    let showsGoldRing: Bool

    var body: some View {
        LegendAvatarImageContent(
            avatar: participant.avatar
        ) {
            Text(initials)
                .font(
                    .system(
                        size: max(11, size * 0.29),
                        weight: .bold,
                        design: .rounded))
                .foregroundStyle(.white)
                .frame(
                    maxWidth: .infinity,
                    maxHeight: .infinity)
                .background(LegendNextGradient.hero)
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay {
            Circle()
                .stroke(
                    showsGoldRing
                        ? LegendNextColor.gold.opacity(0.78)
                        : LegendNextColor.separator,
                    lineWidth: showsGoldRing ? 2 : 1
                )
        }
        .accessibilityHidden(true)
    }

    private var initials: String {
        let value = participant.displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()

        return value.isEmpty ? "L" : value
    }
}

private struct LegendMessagingEmptyState: View {
    let symbol: String
    let title: String
    let message: String
    let actionTitle: String?
    let action: (() -> Void)?

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        VStack(spacing: LegendNextSpacing.md) {
            ZStack {
                Circle()
                    .fill(LegendNextColor.gold.opacity(0.11))
                    .frame(width: 82, height: 82)

                Circle()
                    .stroke(
                        LegendNextColor.gold.opacity(0.44),
                        lineWidth: 1
                    )
                    .frame(width: 82, height: 82)

                Image(systemName: symbol)
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(LegendNextColor.gold)
            }

            VStack(spacing: LegendNextSpacing.xs) {
                Text(title)
                    .font(.system(.title3, design: .rounded).weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .multilineTextAlignment(.center)

                Text(message)
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 310)
            }

            if let actionTitle, let action {
                Button(actionTitle, action: action)
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.midnight)
                    .padding(.horizontal, LegendNextSpacing.lg)
                    .frame(minHeight: 44)
                    .background(LegendNextGradient.gold, in: Capsule())
                    .buttonStyle(LegendMessagingPressButtonStyle())
            }

        }
        .padding(.horizontal, LegendNextSpacing.lg)
        .padding(.vertical, LegendNextSpacing.xl)
        .frame(maxWidth: .infinity)
        .background(
            LegendNextColor.surface,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous
            )
            .stroke(
                LegendNextColor.subtleBorder(for: colorScheme),
                lineWidth: 1
            )
        }
    }
}

private struct LegendMessagingStatusBanner: View {
    let symbol: String
    let title: String
    let message: String

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
            Image(systemName: symbol)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(LegendNextColor.warning)
                .frame(width: 28, height: 28)
                .background(
                    LegendNextColor.warning.opacity(0.12),
                    in: Circle()
                )

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text(message)
                    .font(.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)
        }
        .padding(LegendNextSpacing.sm)
        .background(
            LegendNextColor.warning.opacity(0.07),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
            .stroke(
                LegendNextColor.warning.opacity(0.20),
                lineWidth: 1
            )
        }
    }
}

private struct LegendMessagingConversationSkeleton: View {
    var body: some View {
        HStack(spacing: LegendNextSpacing.md) {
            Circle()
                .fill(LegendNextColor.surfaceElevated)
                .frame(width: 56, height: 56)
                .legendNextShimmer()

            VStack(alignment: .leading, spacing: 8) {
                RoundedRectangle(cornerRadius: 4)
                    .fill(LegendNextColor.surfaceElevated)
                    .frame(width: 142, height: 14)
                    .legendNextShimmer()

                RoundedRectangle(cornerRadius: 4)
                    .fill(LegendNextColor.surfaceElevated)
                    .frame(maxWidth: .infinity)
                    .frame(height: 11)
                    .legendNextShimmer()

                RoundedRectangle(cornerRadius: 4)
                    .fill(LegendNextColor.surfaceElevated)
                    .frame(width: 98, height: 9)
                    .legendNextShimmer()
            }
        }
        .padding(LegendNextSpacing.md)
        .background(
            LegendNextColor.surface,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.card,
                style: .continuous
            )
        )
    }
}

private struct LegendMessagingPressButtonStyle: ButtonStyle {
    func makeBody(
        configuration: Configuration
    ) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? LegendSharedDesign.opacity("pressedControlScale") : 1)
            .opacity(configuration.isPressed ? LegendSharedDesign.opacity("pressedSurface") : 1)
            .animation(
                .easeOut(duration: 0.14),
                value: configuration.isPressed
            )
    }
}

private enum LegendMessagingDateFormatter {
    static func inbox(
        _ date: Date
    ) -> String {
        let calendar = Calendar.current

        if calendar.isDateInToday(date) {
            return date.formatted(
                .dateTime.hour().minute()
                    .locale(LegendActiveLocale())
            )
        }

        if calendar.isDateInYesterday(date) {
            return LegendLocalized("Yesterday")
        }

        if let weekAgo = calendar.date(
            byAdding: .day,
            value: -7,
            to: Date()
        ),
           date >= weekAgo {
            return date.formatted(
                .dateTime.weekday(.abbreviated)
                    .locale(LegendActiveLocale())
            )
        }

        return date.formatted(
            .dateTime.month(.abbreviated).day()
                .locale(LegendActiveLocale())
        )
    }

    static func threadDate(
        _ date: Date
    ) -> String {
        let calendar = Calendar.current

        if calendar.isDateInToday(date) {
            return LegendLocalized("Today")
        }

        if calendar.isDateInYesterday(date) {
            return LegendLocalized("Yesterday")
        }

        return date.formatted(
            .dateTime
                .weekday(.abbreviated)
                .month(.abbreviated)
                .day()
                .locale(LegendActiveLocale())
        )
    }
}

extension MessagingParticipant {
    var legendProfile: MobileSocialAuthor {
        MobileSocialAuthor(identity: identity, profileID: profileID, displayName: displayName, avatar: avatar)
    }
}

private struct LegendMessagingSearchField: View {
    @Binding var search: String
    var placeholder = LegendSharedDesign.copy("search.people")
    @FocusState private var searchIsFocused: Bool
    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: "magnifyingglass")
                .font(.body.weight(.semibold))
                .foregroundStyle(
                    searchIsFocused
                        ? LegendNextColor.gold
                        : LegendNextColor.textTertiary
                )

            TextField(LegendLocalized(placeholder), text: $search)
                .font(.body)
                .foregroundStyle(LegendNextColor.textPrimary)
                .textInputAutocapitalization(.words)
                .autocorrectionDisabled()
                .focused($searchIsFocused)

            if !search.isEmpty {
                Button {
                    search = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(LegendNextColor.textTertiary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(LegendLocalized("Clear search", context: "accessibility copy"))
            }
        }
        .padding(.horizontal, LegendNextSpacing.md)
        .frame(minHeight: 50)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
            .stroke(
                searchIsFocused
                    ? LegendNextColor.gold.opacity(0.72)
                    : LegendNextColor.separator,
                lineWidth: searchIsFocused ? 1.25 : 1
            )
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.intermediate)
        .padding(.bottom, LegendNextSpacing.sm)
    }

}

private struct LegendSharedMessageCard: View {
    let content: MessagingSharedContent
    let reactionBadge: AnyView
    let open: () -> Void
    @Environment(\.legendSocialStore) private var social
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if content.status == "available", let social {
                Button(action: open) {
                    Label(content.authorDisplayName ?? LegendLocalized("Open shared content"), systemImage: "person.crop.circle.fill")
                        .font(.caption.weight(.semibold)).lineLimit(1)
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .foregroundStyle(LegendNextColor.textPrimary)
                        .background(LegendNextColor.gold.opacity(0.12), in: Capsule())
                }.buttonStyle(.plain)
                VStack(alignment: .leading, spacing: 6) {
                if let body = content.body, !body.isEmpty { Text(body) }
                ForEach(content.media.sorted { $0.displayOrder < $1.displayOrder }) { media in
                    if media.mediaKind == "Image" {
                        LegendSocialMediaImage(media: media, social: social).onTapGesture(perform: open)
                    } else if media.mediaKind == "Video" {
                        LegendSocialMediaVideo(postID: content.sourcePostId, media: media, music: nil, social: social)
                    }
                }
                }.overlay(alignment: .bottomTrailing) { reactionBadge.padding(8) }
                Button(action: open) {
                    Label(LegendLocalized("Open original"), systemImage: "arrow.up.right")
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .foregroundStyle(LegendNextColor.chatTimestamp)
                        .background(LegendNextColor.chatTimestamp.opacity(0.09), in: Capsule())
                }.buttonStyle(.plain).padding(.trailing, 64)
            } else { Text(LegendLocalized("Shared content is unavailable.")) }
        }
    }
}
