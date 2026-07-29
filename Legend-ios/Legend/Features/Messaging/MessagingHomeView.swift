import SwiftUI
import UIKit
import PhotosUI
import UniformTypeIdentifiers


private func publicParticipantSubtitle(
    title: String?,
    participantType: ParticipantType
) -> String {
    guard participantType == .agent else {
        return "Connection"
    }

    guard let normalizedTitle = title?
        .trimmingCharacters(in: .whitespacesAndNewlines),
          !normalizedTitle.isEmpty else {
        return "Legend"
    }

    let suffix = " - Legend"

    if normalizedTitle.count >= suffix.count {
        let suffixStart = normalizedTitle.index(
            normalizedTitle.endIndex,
            offsetBy: -suffix.count
        )

        let existingSuffix = String(normalizedTitle[suffixStart...])

        if existingSuffix.localizedCaseInsensitiveCompare(suffix) == .orderedSame {
            return normalizedTitle
        }
    }

    return normalizedTitle + suffix
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

// MARK: - Messages Inbox

struct MessagingHomeView: View {
    @ObservedObject var store: MessagingStore
    let openConversation: (UUID) -> Void

    @Environment(\.colorScheme) private var colorScheme
    @State private var isPresentingNewConversation = false

    var body: some View {
        ZStack {
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()

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
            .presentationDetents([.large])
            .presentationDragIndicator(.visible)
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
        ScrollView {
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
        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
            HStack(alignment: .center, spacing: LegendNextSpacing.md) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.tiny) {
                    Text("Messages")
                        .font(.system(.largeTitle, design: .rounded).weight(.bold))
                        .foregroundStyle(.white)

                    Text("Private conversations within your Legend network.")
                        .font(.subheadline)
                        .foregroundStyle(.white.opacity(0.76))
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: LegendNextSpacing.sm)

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
            }
        }
        .padding(.horizontal, LegendNextSpacing.pageHorizontal)
        .padding(.top, LegendNextSpacing.md)
        .padding(.bottom, LegendNextSpacing.lg)
        .background {
            ZStack {
                LegendNextGradient.hero

                LegendNextGradient.heroGlow
                    .allowsHitTesting(false)
            }
            .clipShape(
                UnevenRoundedRectangle(
                    bottomLeadingRadius: 28,
                    bottomTrailingRadius: 28
                )
            )
            .ignoresSafeArea(edges: .top)
        }
    }

    private func conversationSection(
        _ conversations: [ConversationSummary]
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            HStack(alignment: .firstTextBaseline) {
                Text("Conversations")
                    .font(.system(.headline, design: .rounded).weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Spacer()

                if store.isRefreshing {
                    ProgressView()
                        .controlSize(.small)
                        .tint(LegendNextColor.gold)
                        .accessibilityLabel("Refreshing messages")
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)

            LazyVStack(spacing: LegendNextSpacing.sm) {
                ForEach(conversations) { conversation in
                    Button {
                        openConversation(conversation.id)
                    } label: {
                        LegendConversationRow(conversation: conversation)
                    }
                    .buttonStyle(LegendMessagingPressButtonStyle())
                    .accessibilityHint("Open conversation")
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
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
        ScrollView {
            VStack(spacing: 0) {
                inboxHeader

                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    Text("Conversations")
                        .font(.system(.headline, design: .rounded).weight(.bold))
                        .foregroundStyle(LegendNextColor.textPrimary)

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
        ScrollView {
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

// MARK: - Recipient Picker

private struct LegendRecipientPicker: View {
    @ObservedObject var store: MessagingStore
    let selectConversation: (UUID) -> Void
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var colorScheme
    @FocusState private var searchIsFocused: Bool
    @State private var search = ""

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.pageWash(for: colorScheme)
                    .ignoresSafeArea()

                VStack(spacing: 0) {
                    recipientHeader
                    searchField
                    recipientScopes
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
                    Text("New message")
                        .font(.system(.headline, design: .rounded).weight(.bold))
                        .foregroundStyle(.white)

                    Text("Search your Legend network")
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.66))
                }

                Spacer()

                Color.clear
                    .frame(width: 68, height: 44)
                    .accessibilityHidden(true)
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

    private var recipientScopes: some View {
        ScrollView(.horizontal) {
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
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 3) {
                Text(store.selectedRecipientScope.rawValue)
                    .font(.system(.headline, design: .rounded).weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .padding(.bottom, LegendNextSpacing.tiny)

                ForEach(recipients) { recipient in
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

    private var recipientLoading: some View {
        ScrollView {
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

// MARK: - Conversation Thread

struct ConversationThreadView: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID

    @Environment(\.colorScheme) private var colorScheme
    @FocusState private var composerIsFocused: Bool
    @State private var draft = ""
    @State private var stagedAttachments: [MessagingAttachmentDraft] = []
    @State private var selectedPhoto: PhotosPickerItem?
    @State private var isImportingFile = false
    @State private var messageForStagedAttachments: ConversationMessage?
    @State private var replyingToMessage: ConversationMessage?

    var body: some View {
        ZStack {
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()

            threadContent
        }
        .toolbar(.hidden, for: .navigationBar)
        .task {
            if store.selectedConversationID != conversationID {
                store.openConversation(conversationID)
            }
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
            LegendConversationHeader(conversation: conversation)

            LegendMessageTimeline(
                messages: conversation.messages,
                onReply: { message in
                    replyingToMessage = message
                    composerIsFocused = true
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
        ScrollView(.horizontal) {
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

    @Environment(\.colorScheme) private var colorScheme

    private let unreadColor = Color(uiColor: .systemRed)

    var body: some View {
        HStack(spacing: LegendNextSpacing.md) {
            LegendMessagingAvatar(
                participant: conversation.counterparty,
                size: 56,
                showsGoldRing: conversation.unreadCount > 0
            )

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.tiny
            ) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(conversation.counterparty.displayName)
                        .font(.system(.headline, design: .rounded).weight(
                            conversation.unreadCount > 0
                                ? .bold
                                : .semibold
                        ))
                        .foregroundStyle(LegendNextColor.textPrimary)
                        .lineLimit(1)

                    Spacer(minLength: 4)

                    if let date = conversation.lastMessageUTC {
                        Text(LegendMessagingDateFormatter.inbox(date))
                            .font(.caption2.weight(
                                conversation.unreadCount > 0
                                    ? .bold
                                    : .medium
                            ))
                            .foregroundStyle(
                                conversation.unreadCount > 0
                                    ? unreadColor
                                    : LegendNextColor.textTertiary
                            )
                            .lineLimit(1)
                    }
                }

                HStack(alignment: .center, spacing: LegendNextSpacing.xs) {
                    Text(
                        conversation.lastMessagePreview
                        ?? "Start your conversation"
                    )
                    .font(.subheadline.weight(
                        conversation.unreadCount > 0
                            ? .semibold
                            : .regular
                    ))
                    .foregroundStyle(
                        conversation.unreadCount > 0
                            ? LegendNextColor.textPrimary
                            : LegendNextColor.textSecondary
                    )
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)

                    Spacer(minLength: LegendNextSpacing.xs)

                    if conversation.unreadCount > 0 {
                        Text(unreadText)
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .frame(minWidth: 22, minHeight: 22)
                            .padding(.horizontal, conversation.unreadCount > 9 ? 4 : 0)
                            .background(unreadColor, in: Capsule())
                            .accessibilityLabel(
                                "\(conversation.unreadCount) unread messages"
                            )
                    } else {
                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(LegendNextColor.textTertiary)
                    }
                }

                HStack(spacing: LegendNextSpacing.xs) {
                    Image(systemName: relationshipSymbol)
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(LegendNextColor.gold)

                    Text(relationshipTitle)
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textTertiary)

                    if conversation.isClosed {
                        Text("Closed")
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .padding(.horizontal, 7)
                            .padding(.vertical, 3)
                            .background(LegendNextColor.fill, in: Capsule())
                    }
                }
            }
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
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
                conversation.unreadCount > 0
                    ? unreadColor.opacity(0.34)
                    : LegendNextColor.subtleBorder(for: colorScheme),
                lineWidth: 1
            )
        }
        .shadow(
            color: LegendNextColor.ambientShadow(for: colorScheme),
            radius: 12,
            y: 5
        )
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var relationshipTitle: String {
        switch conversation.counterparty.identity.participantType {
        case .agent:
            return agentPublicTitle(conversation.counterparty.title)

        case .client:
            return "Connection"
        }
    }

    private func agentPublicTitle(
        _ value: String?
    ) -> String {
        guard let title = normalized(value) else {
            return "Legend"
        }

        let suffix = " - Legend"

        if title.localizedCaseInsensitiveContains(suffix) {
            return title
        }

        return title + suffix
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

    private var relationshipSymbol: String {
        switch conversation.counterparty.identity.participantType {
        case .agent:
            return "shield.checkered"
        case .client:
            return "person.fill"
        }
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

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        HStack(spacing: LegendNextSpacing.md) {
            LegendMessagingAvatar(
                participant: MessagingParticipant(
                    identity: recipient.identity,
                    profileID: recipient.profileID,
                    displayName: recipient.displayName,
                    title: recipient.title,
                    avatar: recipient.avatar
                ),
                size: 54,
                showsGoldRing: true
            )

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.tiny
            ) {
                Text(recipient.displayName)
                    .font(.system(.headline, design: .rounded).weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(1)

                Text(relationshipLabel)
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(1)

                if let email = normalized(recipient.email) {
                    Text(email)
                        .font(.caption)
                        .foregroundStyle(LegendNextColor.textTertiary)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: LegendNextSpacing.sm)

            if isStarting {
                ProgressView()
                    .controlSize(.small)
                    .tint(LegendNextColor.gold)
            } else {
                HStack(spacing: 5) {
                    Text(
                        recipient.existingConversationID == nil
                            ? "Message"
                            : "Open"
                    )
                    .font(.caption.weight(.bold))

                    Image(systemName: "arrow.up.right")
                        .font(.caption2.weight(.bold))
                }
                .foregroundStyle(LegendNextColor.midnight)
                .padding(.horizontal, 11)
                .frame(minHeight: 34)
                .background(LegendNextGradient.gold, in: Capsule())
            }
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
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
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var relationshipLabel: String {
        switch recipient.identity.participantType {
        case .agent:
            return agentPublicTitle(recipient.title)

        case .client:
            return publicRelationshipLabel(recipient.relationshipLabel)
                ?? "Connection"
        }
    }

    private func agentPublicTitle(
        _ value: String?
    ) -> String {
        guard let title = normalized(value) else {
            return "Legend"
        }

        let suffix = " - Legend"

        if title.localizedCaseInsensitiveContains(suffix) {
            return title
        }

        return title + suffix
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

            if let counterparty {
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
                Text(conversation.title)
                    .font(.system(.headline, design: .rounded).weight(.bold))
                    .foregroundStyle(.white)
                    .lineLimit(1)

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

    private var counterparty: MessagingParticipant? {
        conversation.participants.first
    }

    private var relationshipSubtitle: String {
        guard let counterparty else {
            return "Private Legend conversation"
        }

        switch counterparty.identity.participantType {
        case .agent:
            return agentPublicTitle(counterparty.title)

        case .client:
            return "Private connection"
        }
    }

    private func agentPublicTitle(
        _ value: String?
    ) -> String {
        guard let title = normalized(value) else {
            return "Legend"
        }

        let suffix = " - Legend"

        if title.localizedCaseInsensitiveContains(suffix) {
            return title
        }

        return title + suffix
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
    let onReply: (ConversationMessage) -> Void

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 2) {
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
                                showsSender: shouldShowSender(
                                    at: index,
                                    messages: messages
                                ),
                                onReply: {
                                    onReply(message)
                                }
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
            .onChange(of: messages.count) { _, _ in
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
            Text(message.sender.displayName)
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)

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
    let showsSender: Bool
    let onReply: () -> Void

    @State private var copyFeedbackTrigger = 0

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
            onReply()
        }
        .accessibilityAction(named: "Copy") {
            UIPasteboard.general.string = message.body
            copyFeedbackTrigger += 1
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
                Text(message.body)
                    .font(.system(size: 15, weight: .regular))
                    .lineSpacing(0)
                    .foregroundStyle(
                        message.isMine
                            ? LegendNextColor.midnight
                            : Color.white
                    )
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)

                ForEach(message.attachments) { attachment in
                    LegendMessageAttachmentChip(
                        attachment: attachment,
                        isMine: message.isMine
                    )
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
            Group {
                if let data = message.sender.avatar?.imageData,
                   let image = UIImage(data: data) {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                } else {
                    Text(senderInitials)
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(
                            maxWidth: .infinity,
                            maxHeight: .infinity
                        )
                        .background(recipientBubbleColor)
                }
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
        Group {
            if let data = participant.avatar?.imageData,
               let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                Text(initials)
                    .font(
                        .system(
                            size: max(11, size * 0.29),
                            weight: .bold,
                            design: .rounded
                        )
                    )
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(LegendNextGradient.hero)
            }
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
