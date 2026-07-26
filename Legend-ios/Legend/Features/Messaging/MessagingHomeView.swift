import SwiftUI
import UIKit

struct MessagingHomeView: View {
    @ObservedObject var store: MessagingStore
    @State private var isPresentingNewConversation = false

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendLoadingView("Loading conversations…")
            case .loaded(let conversations):
                conversationsView(conversations)
            case .unavailable(let message):
                unavailableView(message)
            case .unauthorized(let failure), .forbidden(let failure), .offline(let failure), .failed(let failure):
                failureView(failure)
            }
        }
        .navigationTitle("Messages")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            Button {
                isPresentingNewConversation = true
                store.loadRecipients()
            } label: {
                Image(systemName: "square.and.pencil")
            }
            .accessibilityLabel("Start a new authorized conversation")
        }
        .sheet(isPresented: $isPresentingNewConversation) {
            LegendRecipientPicker(
                store: store,
                dismiss: { isPresentingNewConversation = false })
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .task {
            if case .idle = store.state {
                store.load()
            }
        }
    }

    @ViewBuilder
    private func conversationsView(_ conversations: [ConversationSummary]) -> some View {
        if conversations.isEmpty {
            LegendEmptyState(
                title: "No conversations",
                message: "Your authorized conversations will appear here.",
                symbolName: "message")
        } else {
            List(conversations) { conversation in
                NavigationLink {
                    ConversationThreadView(store: store, conversationID: conversation.id)
                } label: {
                    ConversationRow(conversation: conversation)
                }
                .simultaneousGesture(TapGesture().onEnded {
                    store.openConversation(conversation.id)
                })
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
        }
    }

    private func unavailableView(_ message: String) -> some View {
        LegendEmptyState(
            title: "Messages unavailable",
            message: message,
            symbolName: "lock.message")
    }

    private func failureView(_ failure: UserFacingFailure) -> some View {
        LegendErrorCard(
            title: failure.title,
            message: failure.message,
            retryTitle: "Retry",
            retry: store.load)
        .padding(LegendSpacing.md)
    }
}

private struct LegendRecipientPicker: View {
    @ObservedObject var store: MessagingStore
    let dismiss: () -> Void
    @State private var search = ""

    var body: some View {
        NavigationStack {
            Group {
                switch store.recipientState {
                case .idle, .loading:
                    LegendLoadingView("Loading authorized contacts…")
                case .unavailable(let failure):
                    LegendErrorCard(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Retry",
                        retry: { store.loadRecipients(search: search) })
                    .padding(LegendSpacing.md)
                case .loaded(let recipients):
                    if recipients.isEmpty {
                        LegendEmptyState(
                            title: "No authorized contacts",
                            message: "Only contacts approved by your existing secure relationship can be messaged.",
                            symbolName: "lock.message")
                    } else {
                        List(recipients) { recipient in
                            Button {
                                store.startConversation(with: recipient, completion: dismiss)
                            } label: {
                                LegendListCell {
                                    ParticipantAvatar(participant: MessagingParticipant(
                                        identity: recipient.identity,
                                        profileID: recipient.profileID,
                                        displayName: recipient.displayName,
                                        avatar: recipient.avatar))
                                } content: {
                                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                                        Text(recipient.displayName).font(.headline)
                                        Text(recipient.relationshipLabel ?? recipient.identity.participantType.rawValue)
                                            .font(LegendTypography.metadata)
                                            .foregroundStyle(LegendPalette.secondaryLabel)
                                    }
                                } trailing: {
                                    if recipient.existingConversationID != nil {
                                        LegendBadge(title: "Open", tone: .neutral)
                                    } else {
                                        Image(systemName: "chevron.right")
                                            .font(.caption.weight(.bold))
                                            .foregroundStyle(LegendPalette.secondaryLabel)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                            .disabled(store.isStartingConversation)
                        }
                        .listStyle(.plain)
                    }
                }
            }
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("New message")
            .navigationBarTitleDisplayMode(.inline)
            .searchable(text: $search, prompt: "Search authorized contacts")
            .onChange(of: search) { _, value in store.loadRecipients(search: value) }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: dismiss)
                }
            }
        }
    }
}

private struct ConversationThreadView: View {
    @ObservedObject var store: MessagingStore
    let conversationID: UUID
    @State private var draft = ""

    var body: some View {
        Group {
            switch store.detailState {
            case .idle, .loading:
                LegendLoadingView("Loading conversation…")
            case .loaded(let conversation):
                threadView(conversation)
            case .unavailable(let message):
                LegendEmptyState(
                    title: "Conversation unavailable",
                    message: message,
                    symbolName: "lock.message")
            case .unauthorized(let failure), .forbidden(let failure), .offline(let failure), .failed(let failure):
                LegendErrorCard(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: { store.openConversation(conversationID) })
                    .padding(LegendSpacing.md)
            }
        }
        .task {
            if store.selectedConversationID != conversationID {
                store.openConversation(conversationID)
            }
        }
    }

    private func threadView(_ conversation: ConversationDetail) -> some View {
        VStack(spacing: 0) {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: LegendSpacing.sm) {
                    ForEach(conversation.messages) { message in
                        MessageBubble(message: message)
                    }
                }
                .padding(LegendSpacing.md)
            }

            Divider()
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                if let sendFailure = store.sendFailure {
                    LegendStatusBanner(
                        title: "Message not sent",
                        detail: sendFailure.message,
                        tone: .critical)
                }
                HStack(alignment: .bottom, spacing: LegendSpacing.sm) {
                    TextField("Write a message", text: $draft, axis: .vertical)
                        .lineLimit(1 ... 6)
                        .padding(LegendSpacing.sm)
                        .background(LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
                        .overlay {
                            RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous)
                                .stroke(LegendPalette.separator.opacity(0.45), lineWidth: 1)
                        }
                    Button("Send") {
                        let outgoing = draft
                        draft = ""
                        store.send(body: outgoing)
                    }
                    .buttonStyle(LegendButtonStyle(kind: .gold))
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || store.isSending || conversation.isClosed)
                }
            }
            .padding(LegendSpacing.md)
            .background(LegendPalette.canvas)
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle(conversation.title)
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct ConversationRow: View {
    let conversation: ConversationSummary

    var body: some View {
        LegendListCell {
            ParticipantAvatar(participant: conversation.counterparty)
        } content: {
            VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                Text(conversation.counterparty.displayName)
                    .font(.headline)
                    .foregroundStyle(LegendPalette.label)
                    .lineLimit(1)
                Text(conversation.lastMessagePreview ?? "No message preview")
                    .font(.subheadline)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .lineLimit(1)
            }
        } trailing: {
            VStack(alignment: .trailing, spacing: LegendSpacing.xs) {
                if let lastMessageUTC = conversation.lastMessageUTC {
                    Text(lastMessageUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                        .font(.caption)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }
                if conversation.unreadCount > 0 {
                    LegendBadge(title: "\(conversation.unreadCount)", tone: .critical)
                        .accessibilityLabel("\(conversation.unreadCount) unread messages")
                }
            }
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

private struct MessageBubble: View {
    let message: ConversationMessage

    var body: some View {
        HStack {
            if message.isMine { Spacer(minLength: 48) }
            VStack(alignment: message.isMine ? .trailing : .leading, spacing: LegendSpacing.xxs) {
                Text(message.sender.displayName)
                    .font(.caption.bold())
                Text(message.body)
                    .textSelection(.enabled)
                Text(message.sentUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(LegendSpacing.sm)
            .foregroundStyle(message.isMine ? .white : .primary)
            .background(message.isMine ? LegendPalette.primaryNavy : LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
            if !message.isMine { Spacer(minLength: 48) }
        }
    }
}

private struct ParticipantAvatar: View {
    let participant: MessagingParticipant

    var body: some View {
        Group {
            if let data = participant.avatar?.imageData,
               let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                initialsView
            }
        }
        .frame(width: 44, height: 44)
        .clipShape(Circle())
        .accessibilityHidden(true)
    }

    private var initials: String {
        participant.displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()
    }

    private var initialsView: some View {
        Text(initials)
            .font(.caption.bold())
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(.blue)
    }
}
