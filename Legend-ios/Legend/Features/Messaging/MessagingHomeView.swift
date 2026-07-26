import SwiftUI
import UIKit

struct MessagingHomeView: View {
    @ObservedObject var store: MessagingStore
    let currentSession: MobileSession

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                ProgressView("Loading conversations…")
            case .loaded(let conversations):
                conversationsView(conversations)
            case .unavailable(let message):
                unavailableView(message)
            case .unauthorized(let failure), .forbidden(let failure), .offline(let failure), .failed(let failure):
                failureView(failure)
            }
        }
        .navigationTitle("Messages")
        .task {
            if case .idle = store.state {
                store.load()
            }
        }
    }

    @ViewBuilder
    private func conversationsView(_ conversations: [ConversationSummary]) -> some View {
        if conversations.isEmpty {
            ContentUnavailableView("No conversations", systemImage: "message", description: Text("Your authorized conversations will appear here."))
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
        }
    }

    private func unavailableView(_ message: String) -> some View {
        ContentUnavailableView {
            Label("Messages unavailable", systemImage: "lock.message")
        } description: {
            Text(message)
        }
    }

    private func failureView(_ failure: UserFacingFailure) -> some View {
        ContentUnavailableView {
            Label(failure.title, systemImage: "exclamationmark.triangle")
        } description: {
            Text(failure.message)
        } actions: {
            Button("Retry") {
                store.load()
            }
            .buttonStyle(.borderedProminent)
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
                ProgressView("Loading conversation…")
            case .loaded(let conversation):
                threadView(conversation)
            case .unavailable(let message):
                ContentUnavailableView("Conversation unavailable", systemImage: "lock.message", description: Text(message))
            case .unauthorized(let failure), .forbidden(let failure), .offline(let failure), .failed(let failure):
                ContentUnavailableView {
                    Label(failure.title, systemImage: "exclamationmark.triangle")
                } description: {
                    Text(failure.message)
                } actions: {
                    Button("Retry") {
                        store.openConversation(conversationID)
                    }
                    .buttonStyle(.borderedProminent)
                }
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
                LazyVStack(alignment: .leading, spacing: 12) {
                    ForEach(conversation.messages) { message in
                        MessageBubble(message: message)
                    }
                }
                .padding()
            }

            Divider()
            VStack(alignment: .leading, spacing: 8) {
                if let sendFailure = store.sendFailure {
                    Text(sendFailure.message)
                        .font(.footnote)
                        .foregroundStyle(.red)
                }
                HStack(alignment: .bottom, spacing: 10) {
                    TextField("Write a message", text: $draft, axis: .vertical)
                        .textFieldStyle(.roundedBorder)
                        .lineLimit(1 ... 6)
                    Button("Send") {
                        let outgoing = draft
                        draft = ""
                        store.send(body: outgoing)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || store.isSending || conversation.isClosed)
                }
            }
            .padding()
        }
        .navigationTitle(conversation.title)
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct ConversationRow: View {
    let conversation: ConversationSummary

    var body: some View {
        HStack(spacing: 12) {
            ParticipantAvatar(participant: conversation.counterparty)

            VStack(alignment: .leading, spacing: 4) {
                Text(conversation.counterparty.displayName)
                    .font(.headline)
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                Text(conversation.lastMessagePreview ?? "No message preview")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            VStack(alignment: .trailing, spacing: 6) {
                if let lastMessageUTC = conversation.lastMessageUTC {
                    Text(lastMessageUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if conversation.unreadCount > 0 {
                    Text("\(conversation.unreadCount)")
                        .font(.caption.bold())
                        .foregroundStyle(.white)
                        .padding(.horizontal, 7)
                        .padding(.vertical, 3)
                        .background(.red, in: Capsule())
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
            VStack(alignment: message.isMine ? .trailing : .leading, spacing: 5) {
                Text(message.sender.displayName)
                    .font(.caption.bold())
                Text(message.body)
                    .textSelection(.enabled)
                Text(message.sentUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(10)
            .foregroundStyle(message.isMine ? .white : .primary)
            .background(message.isMine ? Color.accentColor : Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 14))
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
