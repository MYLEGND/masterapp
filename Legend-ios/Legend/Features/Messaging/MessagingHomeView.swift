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
            if case .loaded(let conversations) = store.state {
                LegendMessagingCallDirectory(
                    store: store,
                    conversations: conversations,
                    dismiss: { isPresentingCallDirectory = false })
                    .legendNextSheetChrome(detents: [.medium, .large])
            }
        }
        .alert(
            "Remove conversation?",
            isPresented: Binding(
                get: { conversationPendingRemoval != nil },
                set: { if !$0 { conversationPendingRemoval = nil } }
            )
        ) {
            Button("Cancel", role: .cancel) {
                conversationPendingRemoval = nil
            }
            Button("Remove", role: .destructive) {
                if let conversationPendingRemoval {
                    store.removeConversation(conversationID: conversationPendingRemoval.id)
                }
                conversationPendingRemoval = nil
            }
        } message: {
            Text("This removes the conversation from your inbox only. A new message will bring it back.")
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
                title: "Messaging is unavailable",
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
        LegendScrollView {
            LazyVStack(spacing: 0) {
                inboxHeader

                if let refreshFailure = store.refreshFailure {
                    LegendMessagingStatusBanner(
                        symbol: "exclamationmark.arrow.triangle.2.circlepath",
                        title: "Messages could not refresh",
                        message: refreshFailure.message
                    )
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                    .padding(.top, LegendNextSpacing.sm)
                }

                if conversations.isEmpty {
                    inboxEmptyState
                        .padding(.top, LegendNextSpacing.display)
                } else {
                    conversationSection(conversations)
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
                    Text("Messages")
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
                    .accessibilityLabel("Start a new conversation")

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
                    .accessibilityLabel("Call a connection")
                }
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.md)
        .padding(.bottom, LegendNextSpacing.xs)
    }

    private func conversationSection(
        _ conversations: [ConversationSummary]
    ) -> some View {
        let pinned = Array(conversations.filter(\.isPinned).prefix(6))
        let remaining = conversations.filter { !$0.isPinned }

        return VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            if store.isRefreshing {
                HStack(spacing: LegendNextSpacing.xs) {
                    ProgressView()
                        .controlSize(.small)
                        .tint(LegendNextColor.gold)
                    Text("Updating")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(LegendNextColor.textSecondary)
                }
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            }

            if !pinned.isEmpty {
                Text("Pinned")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.gold)
                    .textCase(.uppercase)
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)

                LegendPinnedConversationGrid(
                    conversations: pinned,
                    openConversation: openConversation,
                    setPinned: { conversation, isPinned in
                        store.setPinned(conversationID: conversation.id, isPinned: isPinned)
                    },
                    setMuted: { conversation, isMuted in
                        store.setMuted(conversationID: conversation.id, isMuted: isMuted)
                    },
                    remove: { conversation in
                        conversationPendingRemoval = conversation
                    })
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                    .padding(.bottom, LegendNextSpacing.xs)
            }

            if !remaining.isEmpty {
                LazyVStack(spacing: LegendNextSpacing.sm) {
                    ForEach(remaining) { conversation in
                        conversationButton(conversation)
                    }
                }
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            }
        }
    }

    private func conversationButton(_ conversation: ConversationSummary) -> some View {
        Button {
            openConversation(conversation.id)
        } label: {
            LegendConversationRow(conversation: conversation)
        }
        .buttonStyle(LegendMessagingPressButtonStyle())
        .accessibilityHint("Open conversation")
        .contextMenu {
            Button {
                store.setPinned(conversationID: conversation.id, isPinned: !conversation.isPinned)
            } label: {
                Label(
                    conversation.isPinned ? "Unpin" : "Pin",
                    systemImage: conversation.isPinned ? "pin.slash" : "pin")
            }

            Button {
                store.setMuted(conversationID: conversation.id, isMuted: !conversation.isMuted)
            } label: {
                Label(
                    conversation.isMuted ? "Unmute" : "Mute",
                    systemImage: conversation.isMuted ? "bell" : "bell.slash")
            }

            Divider()

            Button(role: .destructive) {
                conversationPendingRemoval = conversation
            } label: {
                Label("Remove from inbox", systemImage: "trash")
            }
        }
    }

    private var inboxEmptyState: some View {
        LegendMessagingEmptyState(
            symbol: "message.badge.waveform.fill",
            title: "Start a private conversation",
            message: "Choose someone in your Legend network to begin.",
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
        .accessibilityLabel("Loading conversations")
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

private struct LegendPinnedConversationGrid: View {
    let conversations: [ConversationSummary]
    let openConversation: (UUID) -> Void
    let setPinned: (ConversationSummary, Bool) -> Void
    let setMuted: (ConversationSummary, Bool) -> Void
    let remove: (ConversationSummary) -> Void

    private let columns = Array(
        repeating: GridItem(.flexible(minimum: 72), spacing: LegendNextSpacing.sm),
        count: 3)

    var body: some View {
        LazyVGrid(columns: columns, spacing: LegendNextSpacing.md) {
            ForEach(conversations) { conversation in
                Button {
                    openConversation(conversation.id)
                } label: {
                    VStack(spacing: 7) {
                        Group {
                            if conversation.conversationType == "Group" {
                                LegendMessagingGroupAvatar(
                                    avatar: conversation.groupAvatar,
                                    size: 58)
                            } else {
                                LegendMessagingAvatar(
                                    participant: conversation.counterparty,
                                    size: 58,
                                    showsGoldRing: conversation.unreadCount > 0)
                            }
                        }

                        Text(conversation.title)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity)
                    }
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendMessagingPressButtonStyle())
                .accessibilityLabel("Pinned conversation with \(conversation.title)")
                .contextMenu {
                    Button {
                        setPinned(conversation, false)
                    } label: {
                        Label("Unpin", systemImage: "pin.slash")
                    }

                    Button {
                        setMuted(conversation, !conversation.isMuted)
                    } label: {
                        Label(
                            conversation.isMuted ? "Unmute" : "Mute",
                            systemImage: conversation.isMuted ? "bell" : "bell.slash")
                    }

                    Divider()

                    Button(role: .destructive) {
                        remove(conversation)
                    } label: {
                        Label("Remove from inbox", systemImage: "trash")
                    }
                }
            }
        }
    }
}

private struct LegendMessagingCallDirectory: View {
    @ObservedObject var store: MessagingStore
    let conversations: [ConversationSummary]
    let dismiss: () -> Void

    @State private var selectedConversation: ConversationSummary?

    private var directConversations: [ConversationSummary] {
        conversations.filter { $0.conversationType != "Group" }
    }

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                LegendScrollView {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                        Text("Call a connection")
                            .font(LegendNextTypography.section)
                            .foregroundStyle(LegendNextColor.textPrimary)

                        Text("Calls open through your device’s secure Phone or FaceTime experience.")
                            .font(.subheadline)
                            .foregroundStyle(LegendNextColor.textSecondary)

                        if directConversations.isEmpty {
                            LegendMessagingEmptyState(
                                symbol: "phone.down.fill",
                                title: "No direct conversations",
                                message: "Start a private conversation to call a connection.",
                                actionTitle: nil,
                                action: nil)
                        } else {
                            LazyVStack(spacing: LegendNextSpacing.sm) {
                                ForEach(directConversations) { conversation in
                                    Button {
                                        selectedConversation = conversation
                                    } label: {
                                        LegendConversationRow(conversation: conversation)
                                    }
                                    .buttonStyle(LegendMessagingPressButtonStyle())
                                }
                            }
                        }
                    }
                    .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                    .padding(.vertical, LegendNextSpacing.md)
                }
                .scrollIndicators(.hidden)
            }
            .navigationTitle("Call")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done", action: dismiss)
                        .foregroundStyle(LegendNextColor.gold)
                }
            }
        }
        .sheet(item: $selectedConversation) { conversation in
            LegendConversationCallSheet(
                store: store,
                conversationID: conversation.id,
                fallbackName: conversation.title)
                .legendNextSheetChrome(detents: [.height(340)])
        }
    }
}

private struct LegendConversationCallSheet: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    let fallbackName: String

    @Environment(\.dismiss) private var dismiss
    @State private var options: ConversationCallOptions?
    @State private var isLoading = true

    var body: some View {
        ZStack {
            LegendNextCanvas()

            VStack(spacing: LegendNextSpacing.md) {
                if isLoading {
                    ProgressView()
                        .tint(LegendNextColor.gold)
                    Text("Preparing secure call options")
                        .font(.subheadline)
                        .foregroundStyle(LegendNextColor.textSecondary)
                } else if let options {
                    Image(systemName: "phone.connection.fill")
                        .font(.system(size: 30, weight: .semibold))
                        .foregroundStyle(LegendNextColor.gold)
                        .frame(width: 68, height: 68)
                        .background(LegendNextColor.navy, in: Circle())

                    Text(options.displayName)
                        .font(.title3.weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)

                    if let phone = options.phoneNumber {
                        LegendCallActionButton(
                            title: "Phone call",
                            subtitle: "Use your carrier",
                            symbol: "phone.fill",
                            action: { openSystemCall(scheme: "tel", address: phone) })
                    }

                    if let faceTime = options.faceTimeAddress {
                        LegendCallActionButton(
                            title: "FaceTime video",
                            subtitle: "Open FaceTime",
                            symbol: "video.fill",
                            action: { openSystemCall(scheme: "facetime", address: faceTime) })

                        LegendCallActionButton(
                            title: "FaceTime Audio",
                            subtitle: "Open FaceTime Audio",
                            symbol: "phone.badge.waveform.fill",
                            action: { openSystemCall(scheme: "facetime-audio", address: faceTime) })
                    }
                } else {
                    LegendMessagingEmptyState(
                        symbol: "phone.down.fill",
                        title: "Calling unavailable",
                        message: "\(fallbackName) has not shared a call address for this private conversation.",
                        actionTitle: nil,
                        action: nil)
                }
            }
            .padding(LegendNextSpacing.pageHorizontal)
        }
        .task {
            options = await store.callOptions(for: conversationID)
            isLoading = false
        }
    }

    private func openSystemCall(scheme: String, address: String) {
        let allowed = CharacterSet.urlPathAllowed
        guard let encoded = address.addingPercentEncoding(withAllowedCharacters: allowed),
              let url = URL(string: "\(scheme)://\(encoded)") else { return }
        UIApplication.shared.open(url)
        dismiss()
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
    let selectConversation: (UUID) -> Void
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var colorScheme
    @FocusState private var searchIsFocused: Bool
    @State private var search = ""
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
                    searchField
                    recipientScopes
                    if isCreatingGroup {
                        groupCreationBar
                    }
                    recipientContent
                }
            }
            .toolbar(.hidden, for: .navigationBar)
            .onChange(of: search) { _, value in
                store.searchRecipients(value)
            }
        }
    }

    private var recipientHeader: some View {
        VStack(spacing: LegendNextSpacing.md) {
            HStack(spacing: LegendNextSpacing.md) {
                Button("Cancel", action: dismiss)
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
                    Text(isCreatingGroup ? "New group" : "New message")
                        .font(.system(.headline, design: .rounded).weight(.bold))
                        .foregroundStyle(.white)

                    Text(isCreatingGroup
                         ? "Choose at least two connections"
                         : "Search your Legend network")
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.66))
                }

                Spacer()

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
                .accessibilityLabel(isCreatingGroup ? "Create a direct message" : "Create a group chat")
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

    private var searchField: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: "magnifyingglass")
                .font(.body.weight(.semibold))
                .foregroundStyle(
                    searchIsFocused
                        ? LegendNextColor.gold
                        : LegendNextColor.textTertiary
                )

            TextField("Search people", text: $search)
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
                .accessibilityLabel("Clear search")
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
                    .accessibilityLabel("Choose group photo")
                    .disabled(isPreparingGroupImage)

                TextField("Group name", text: $groupSubject)
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

            Button(store.isCreatingGroup ? "Creating group…" : "Create group (\(groupRecipients.count))") {
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
        .alert("Group photo could not be prepared", isPresented: $isShowingGroupPhotoPreparationFailure) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("Choose another photo and try again. Your group was not created without the selected photo.")
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
                    detail: recipient.relationshipLabel ?? "Group member")
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
                        Label(scope.rawValue, systemImage: scope.icon)
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
                    .accessibilityLabel("Show \(scope.rawValue.lowercased())")
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
                Text(store.selectedRecipientScope.rawValue)
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

        store.startConversation(
            with: recipient,
            completion: selectConversation
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
            : publicRelationshipLabel(recipient.relationshipLabel) ?? "Connection"
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
        .accessibilityLabel("Loading people")
    }
}

/// Member additions use the same server-authorized recipient collections as
/// direct and new-group conversations. The server still confirms ownership
/// before it accepts the mutation.
private struct LegendGroupMemberPicker: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    let dismiss: () -> Void

    @FocusState private var searchIsFocused: Bool
    @State private var search = ""

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                VStack(spacing: 0) {
                    header
                    searchField
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
            Button("Cancel", action: dismiss)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.white)

            Spacer()

            VStack(spacing: 2) {
                Text("Add group member")
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.white)
                Text("Choose one of your connections")
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

    private var searchField: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(LegendNextColor.textTertiary)

            TextField("Search connections", text: $search)
                .focused($searchIsFocused)
                .autocorrectionDisabled()
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .frame(minHeight: 44)
        .background(
            LegendNextColor.surfaceElevated,
            in: RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
        )
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.vertical, LegendNextSpacing.sm)
    }

    private var scopes: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: LegendNextSpacing.xs) {
                ForEach(store.availableRecipientScopes) { scope in
                    Button(scope.rawValue) {
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
            ProgressView("Loading connections")
                .frame(maxWidth: .infinity, maxHeight: .infinity)

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
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
                                    : publicRelationshipLabel(recipient.relationshipLabel) ?? "Connection",
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

    private var manageableParticipants: [MessagingParticipant] {
        conversation.participants
            .filter { $0.identity != currentIdentity }
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
            Button("Done", action: dismiss)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.white)

            Spacer()

            VStack(spacing: 2) {
                Text("Collaborators")
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.white)

                Text("Co-manage this group")
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
                "Owner-controlled access",
                systemImage: "lock.shield.fill")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)

            Text(
                "Collaborators can edit the group and add members. "
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
                                    ? "Collaborator"
                                    : "Make collaborator")
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
                ? "Remove \(participant.displayName) as collaborator"
                : "Make \(participant.displayName) a collaborator")
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
        case .oneTime: return "One time"
        case .daily: return "Daily"
        case .weekly: return "Weekly"
        case .biweekly: return "Every other week"
        case .monthly: return "Monthly"
        case .custom: return "Custom"
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
                    Text("Group host")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.textSecondary)
                    Text(hostName)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                }

                Spacer()

                Button("Choose by name") {
                    isPresentingHostPicker = true
                }
                .font(.subheadline.weight(.semibold))
                .buttonStyle(.bordered)
            }

            Toggle("Add an online meeting", isOn: $draft.isMeetingEnabled)
                .font(.subheadline.weight(.semibold))
                .tint(LegendNextColor.gold)

            if draft.isMeetingEnabled {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    TextField("Meeting link name (for example, Wednesday Zoom)", text: $draft.linkLabel)
                        .textInputAutocapitalization(.words)
                        .autocorrectionDisabled()
                    TextField("Zoom, Teams, or Google Meet URL", text: $draft.linkURL)
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

                Toggle("Add a recurring schedule", isOn: $draft.isScheduleEnabled)
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
            Picker("Frequency", selection: $draft.frequency) {
                ForEach(LegendGroupMeetingFrequency.allCases) { frequency in
                    Text(frequency.title).tag(frequency)
                }
            }
            .pickerStyle(.menu)

            if draft.frequency.needsWeekday {
                Picker("Day", selection: $draft.weekday) {
                    ForEach(LegendGroupMeetingWeekday.allCases) { weekday in
                        Text(weekday.rawValue).tag(weekday)
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
                TextField("Time zone", text: $draft.timeZoneID)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }

            if draft.frequency == .custom {
                TextField(
                    "Custom schedule (for example, first and third Wednesday)",
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
            return "You (group owner)"
        }
        return hostOptions.first(where: { $0.identity == hostIdentity })?.displayName
            ?? "Selected group member"
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
                    Label("You (group owner)", systemImage: "person.crop.circle")
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
            .searchable(text: $search, prompt: "Search group members by name")
            .navigationTitle("Choose group host")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
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
                        .accessibilityLabel("Change group photo")
                        .disabled(isPreparingReplacementImage)

                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        Text("Group name")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                        TextField("Group name", text: $subject)
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
                            ? "Only the group owner can set the host, meeting link, and schedule. Collaborators can still manage the group name, photo, and membership."
                            : "Group owners and collaborators can manage the group name, photo, and membership.")
                        .font(.footnote)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    Spacer()
                }
                .padding(LegendNextSpacing.pageHorizontal)
            }
            .navigationTitle("Group profile")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(store.isCreatingGroup ? "Saving…" : "Save") {
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
            .alert("Group photo could not be prepared", isPresented: $isShowingGroupPhotoPreparationFailure) {
                Button("OK", role: .cancel) {}
            } message: {
                Text("Choose another photo and try again. Your existing group photo was not changed.")
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
                    detail: participant.roleLabel ?? "Group member")
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
    @State private var verificationProfile: LegendVerificationProfileRoute?

    var body: some View {
        ZStack {
            LegendNextCanvas()

            threadContent
        }
        .toolbar(.hidden, for: .navigationBar)
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
            "Delete this group?",
            isPresented: $isConfirmingDeleteGroup,
            titleVisibility: .visible
        ) {
            Button("Delete Group for Everyone", role: .destructive) {
                store.deleteGroup(conversationID: conversationID) {
                    dismissThread()
                }
            }

            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "This permanently closes the group for every member. "
                + "Only the group owner can perform this action."
            )
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

    @ViewBuilder
    private var threadContent: some View {
        switch store.detailState {
        case .idle, .loading:
            LegendConversationLoadingView()

        case .loaded(let conversation):
            conversationView(conversation)

        case .unavailable(let message):
            conversationFailure(
                title: "Conversation unavailable",
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
                startCall: { isPresentingCallSheet = true }
            )

            LegendMessageTimeline(
                messages: conversation.messages,
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
                    title: "Message not sent",
                    message: sendFailure.message
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
                    Label("Photo Library", systemImage: "photo.on.rectangle")
                }

                Button {
                    isImportingFile = true
                } label: {
                    Label("Files", systemImage: "doc")
                }
            } label: {
                Image(systemName: "plus")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .frame(width: 42, height: 42)
                    .background(LegendNextColor.fill, in: Circle())
            }
            .buttonStyle(LegendMessagingPressButtonStyle())
            .accessibilityLabel("Add photo or file")

            TextField(
                "Write a message",
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
            .accessibilityLabel("Message")

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
                Text("Conversation closed")
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text("New messages cannot be sent in this conversation.")
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
        !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func sendDraft() {
        let outgoing = draft.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        guard !outgoing.isEmpty else { return }

        Task {
            guard let message = await store.send(body: outgoing) else { return }
            draft = ""
            replyingToMessage = nil
            messageForStagedAttachments = message
            await uploadStagedAttachments(to: message)
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
            (attachmentIDs == nil || attachmentIDs?.contains(attachment.id) == true) &&
                attachment.state != .uploading
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
        .accessibilityLabel("Attachments ready to send")
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
            Text("Ready to send")
                .font(.caption2)
                .foregroundStyle(LegendNextColor.textSecondary)
        case .uploading:
            HStack(spacing: 4) {
                ProgressView()
                    .controlSize(.mini)
                Text("Uploading")
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
            .accessibilityLabel("Remove \(attachment.fileName)")
        case .failed:
            Button {
                onRetry(attachment.id)
            } label: {
                Image(systemName: "arrow.clockwise.circle.fill")
                    .foregroundStyle(LegendNextColor.gold)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Retry \(attachment.fileName)")
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
            subtitle: conversation.lastMessagePreview ?? "Start your conversation",
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

                    if conversation.unreadCount > 0 {
                        Text(unreadText)
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .frame(minWidth: 21, minHeight: 21)
                            .padding(.horizontal, conversation.unreadCount > 9 ? 4 : 0)
                            .background(unreadColor, in: Capsule())
                            .accessibilityLabel("\(conversation.unreadCount) unread messages")
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
            return "Group chat"
        }

        switch conversation.counterparty.identity.participantType {
        case .agent:
            return publicAgentRoleLabel(roleLabel: conversation.counterparty.roleLabel)

        case .client:
            return "Connection"
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
                        Text(recipient.existingConversationID == nil ? "Message" : "Open")
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
                ?? "Connection"
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
    let addMember: () -> Void
    let editGroup: () -> Void
    let manageCollaborators: () -> Void
    let deleteGroup: () -> Void
    let setGroupPromotion: (Bool) -> Void
    let isFounder: Bool
    let startCall: () -> Void

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
            .accessibilityLabel("Back")

            if isGroup {
                LegendMessagingGroupAvatar(
                    avatar: conversation.groupAvatar,
                    size: 46)
            } else if let counterparty {
                LegendMessagingAvatar(
                    participant: counterparty,
                    size: 46,
                    showsGoldRing: true
                )
            } else {
                Image(systemName: "person.2.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 46, height: 46)
                    .background(LegendNextGradient.gold, in: Circle())
            }

            VStack(alignment: .leading, spacing: 2) {
                LegendVerifiedName(
                    conversation.title,
                    isVerified: !isGroup && counterparty?.isVerified == true,
                    font: .system(.headline, design: .rounded).weight(.bold),
                    textColor: .white,
                    badgePlacement: .alongsideProfileImage)

                if isGroup {
                    HStack(spacing: 5) {
                        Image(systemName: "lock.shield.fill")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(LegendNextColor.goldBright)

                        Text(conversation.isClosed
                             ? "CLOSED LEGEND CONVERSATION"
                             : "PRIVATE GROUP CHAT")
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
                             ? "Closed Legend conversation"
                             : relationshipSubtitle)
                            .font(.caption)
                            .foregroundStyle(.white.opacity(0.72))
                            .lineLimit(1)
                    }
                }
            }

            Spacer()

            if isGroup &&
                (
                    conversation.canManageMembers ||
                    conversation.canManageCollaborators == true ||
                    conversation.canDeleteGroup == true ||
                    (isFounder && conversation.canManagePromotion == true)
                ) {
                Menu {
                    if conversation.canManageMembers {
                        Button(action: editGroup) {
                            Label("Edit Group", systemImage: "pencil")
                        }

                        Button(action: addMember) {
                            Label(
                                "Add Members",
                                systemImage: "person.badge.plus")
                        }
                    }

                    if conversation.canManageCollaborators == true {
                        Divider()

                        Button(action: manageCollaborators) {
                            Label(
                                "Collaborators",
                                systemImage: "person.2.badge.gearshape")
                        }
                    }

                    if conversation.canDeleteGroup == true {
                        Divider()

                        Button(role: .destructive, action: deleteGroup) {
                            Label(
                                "Delete Group",
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
                .accessibilityLabel("Group management")
            } else if !isGroup {
                Button(action: startCall) {
                    Image(systemName: "phone.fill")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: 38, height: 38)
                        .background(LegendNextGradient.gold, in: Circle())
                }
                .buttonStyle(LegendMessagingPressButtonStyle())
                .accessibilityLabel("Call \(conversation.title)")
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

    private var counterparty: MessagingParticipant? {
        conversation.participants.first
    }

    private var relationshipSubtitle: String {
        guard let counterparty else {
            return "Private Legend conversation"
        }

        switch counterparty.identity.participantType {
        case .agent:
            return publicAgentRoleLabel(roleLabel: counterparty.roleLabel)
                ?? "Private Legend conversation"

        case .client:
            return "Private connection"
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
                .accessibilityLabel("Open \(link.label)")
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
        let host = "Hosted by \(meeting.host.displayName)"
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
                return startsUTC.formatted(date: .abbreviated, time: .shortened)
            }
            return "One-time meeting"
        case "Daily":
            return "Daily\(timeDetail.isEmpty ? "" : " at \(timeDetail)")"
        case "Weekly":
            return "Weekly on \(weekdayText(schedule))\(timeDetail.isEmpty ? "" : " at \(timeDetail)")"
        case "Biweekly":
            return "Every other \(weekdayText(schedule))\(timeDetail.isEmpty ? "" : " at \(timeDetail)")"
        case "Monthly":
            return "Monthly\(timeDetail.isEmpty ? "" : " at \(timeDetail)")"
        case "Custom":
            return normalized(schedule.customDescription) ?? "Custom schedule"
        default:
            return "Scheduled meeting"
        }
    }

    private func weekdayText(_ schedule: MessagingGroupMeetingSchedule) -> String {
        schedule.weekdays.isEmpty ? "selected days" : schedule.weekdays.joined(separator: ", ")
    }

    private func displayTime(_ value: String?) -> String? {
        guard let value = normalized(value) else { return nil }
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX")
        parser.dateFormat = "HH:mm"
        guard let date = parser.date(from: value) else { return value }
        let formatter = DateFormatter()
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
            .accessibilityLabel("Back")

            Text("Conversation")
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
    let participantAvatar: (LogicalParticipantIdentity) -> ProfileAvatar?
    let hasOlderMessages: Bool
    let isLoadingOlderMessages: Bool
    let loadOlderMessages: () -> Void
    let onReply: (ConversationMessage) -> Void
    let onDelete: (ConversationMessage) -> Void
    let onOpenVerificationProfile: (ConversationMessage) -> Void

    var body: some View {
        ScrollViewReader { proxy in
            LegendScrollView(tracksNavigationChrome: false) {
                LazyVStack(spacing: 2) {
                    if hasOlderMessages {
                        Button(action: loadOlderMessages) {
                            if isLoadingOlderMessages {
                                ProgressView()
                                    .controlSize(.small)
                            } else {
                                Label("Load earlier messages", systemImage: "arrow.up.circle")
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
                            title: "Begin the conversation",
                            message: "Your messages will appear here.",
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
                                onOpenVerificationProfile: message.verificationReview?.status != "Pending"
                                    ? nil
                                    : { onOpenVerificationProfile(message) }
                            )
                            .id(message.id)
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
            .onAppear {
                scrollToBottom(proxy, animated: false)
            }
            .onChange(of: messages.last?.id) { _, _ in
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
            .foregroundStyle(LegendNextColor.textTertiary)
            .padding(.horizontal, 9)
            .padding(.vertical, 3)
            .background(LegendNextColor.fill, in: Capsule())
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

                    Text("Replying to \(replyTargetName)")
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
            .accessibilityLabel("Cancel reply")
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
            LegendVerifiedName(
                message.sender.displayName,
                isVerified: message.sender.isVerified == true,
                font: .caption.weight(.semibold),
                textColor: LegendNextColor.textSecondary)

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
    let message: ConversationMessage
    let senderAvatar: ProfileAvatar?
    let showsSender: Bool
    let onReply: () -> Void
    let onDelete: () -> Void
    let onOpenVerificationProfile: (() -> Void)?

    @State private var copyFeedbackTrigger = 0
    @State private var isShowingOriginal = false

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
        .contextMenu {
            if !message.isDeleted {
                Button {
                    onReply()
                } label: {
                    Label(
                        "Reply",
                        systemImage: "arrowshape.turn.up.left.fill"
                    )
                }

                Button {
                    UIPasteboard.general.string = message.body
                    copyFeedbackTrigger += 1
                } label: {
                    Label(
                        "Copy",
                        systemImage: "doc.on.doc.fill"
                    )
                }

                if message.isMine {
                    Divider()
                    Button(role: .destructive, action: onDelete) {
                        Label("Unsend", systemImage: "trash")
                    }
                }
            }
        } preview: {
            LegendMessageContextPreview(message: message)
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
                Text(message.isDeleted ? "Message unsent" : displayedMessageBody)
                    .font(.system(size: 15, weight: .regular))
                    .italic(message.isDeleted)
                    .lineSpacing(0)
                    .foregroundStyle(
                        message.isMine
                            ? LegendNextColor.midnight
                            : Color.white
                    )
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)

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
                            .foregroundStyle(message.isMine
                                             ? LegendNextColor.midnight
                                             : LegendNextColor.goldBright)
                            .padding(.top, 2)
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Switch between the translated and original message")
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
                    .accessibilityHint("Open this member’s profile for \(review.resourceType.displayName) review")
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(
                bubbleColor,
                in: RoundedRectangle(
                    cornerRadius: 16,
                    style: .continuous
                )
            )

            Text(
                message.sentUTC,
                format: .dateTime.hour().minute()
            )
            .font(.system(size: 10, weight: .regular))
            .foregroundStyle(LegendNextColor.textTertiary)
            .padding(
                message.isMine ? .trailing : .leading,
                3
            )
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private var displayedMessageBody: String {
        isShowingOriginal ? (message.originalBody ?? message.body) : message.body
    }

    private func languageName(_ code: String) -> String {
        switch code {
        case "ht": "Haitian Creole"
        case "en": "English"
        case "es": "Spanish"
        case "fr": "French"
        case "pt": "Portuguese"
        case "de": "German"
        case "ja": "Japanese"
        case "ko": "Korean"
        case "zh-Hans": "Chinese (Simplified)"
        case "ar": "Arabic"
        default: code
        }
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
            .accessibilityHidden(true)
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
                ? "Sent message unsent."
                : "Received message unsent by " + message.sender.displayName + "."
        }

        let attachmentDescription = message.attachments.isEmpty
            ? ""
            : " Includes \(message.attachments.count) attachment"
                + (message.attachments.count == 1 ? "." : "s.")

        if message.isMine {
            return "Sent message. \(message.body)\(attachmentDescription)"
        }

        return "Received message from "
            + message.sender.displayName
            + ". "
            + message.body
            + attachmentDescription
    }
}

private struct LegendMessageAttachmentChip: View {
    let attachment: MessagingAttachment
    let isMine: Bool

    private var isImage: Bool {
        attachment.contentType.hasPrefix("image/")
    }

    private var scanStatusLabel: String {
        "\(attachment.scanStatus.capitalized) scan"
    }

    var body: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: isImage ? "photo.fill" : "doc.fill")
                .font(.caption.weight(.semibold))

            Text(attachment.originalFileName)
                .lineLimit(1)

            Text(scanStatusLabel)
                .font(.caption2.weight(.semibold))
                .opacity(0.78)
        }
        .font(.caption.weight(.medium))
        .foregroundStyle(
            isMine
                ? LegendNextColor.midnight
                : Color.white
        )
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, 6)
        .background(
            isMine
                ? LegendNextColor.midnight.opacity(0.09)
                : Color.white.opacity(0.14),
            in: Capsule()
        )
        .overlay {
            Capsule()
                .stroke(
                    isMine
                        ? LegendNextColor.midnight.opacity(0.12)
                        : Color.white.opacity(0.12),
                    lineWidth: 0.75
                )
        }
        .accessibilityLabel(
            "\(attachment.originalFileName), \(scanStatusLabel)"
        )
    }
}

private struct LegendConversationLoadingView: View {
    var body: some View {
        VStack(spacing: 0) {
            LegendConversationFallbackHeader()

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
        .accessibilityLabel("Loading conversation")
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
            .scaleEffect(configuration.isPressed ? 0.975 : 1)
            .opacity(configuration.isPressed ? 0.86 : 1)
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
                date: .omitted,
                time: .shortened
            )
        }

        if calendar.isDateInYesterday(date) {
            return "Yesterday"
        }

        if let weekAgo = calendar.date(
            byAdding: .day,
            value: -7,
            to: Date()
        ),
           date >= weekAgo {
            return date.formatted(
                .dateTime.weekday(.abbreviated)
            )
        }

        return date.formatted(
            .dateTime.month(.abbreviated).day()
        )
    }

    static func threadDate(
        _ date: Date
    ) -> String {
        let calendar = Calendar.current

        if calendar.isDateInToday(date) {
            return "Today"
        }

        if calendar.isDateInYesterday(date) {
            return "Yesterday"
        }

        return date.formatted(
            .dateTime
                .weekday(.abbreviated)
                .month(.abbreviated)
                .day()
        )
    }
}
