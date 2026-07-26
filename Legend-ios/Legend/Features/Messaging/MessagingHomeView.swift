import SwiftUI

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
                ContentUnavailableView {
                    Label("Messages need a server contract", systemImage: "lock.message")
                } description: {
                    Text(message)
                }
            case .failed(let failure):
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
        .navigationTitle("Messages")
        .task {
            store.load()
        }
    }

    @ViewBuilder
    private func conversationsView(_ conversations: [ConversationSummary]) -> some View {
        if conversations.isEmpty {
            ContentUnavailableView("No conversations", systemImage: "message", description: Text("Your authorized conversations will appear here."))
        } else {
            List(conversations) { conversation in
                Button {
                    store.selectConversation(conversation.id)
                } label: {
                    ConversationRow(conversation: conversation)
                }
                .buttonStyle(.plain)
            }
            .listStyle(.plain)
        }
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
                Text(conversation.preview ?? "No message preview")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            VStack(alignment: .trailing, spacing: 6) {
                Text(conversation.lastActivityUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                    .font(.caption)
                    .foregroundStyle(.secondary)
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

private struct ParticipantAvatar: View {
    let participant: MessagingParticipant

    var body: some View {
        Group {
            if let avatarURL = participant.avatarURL {
                AsyncImage(url: avatarURL) { phase in
                    switch phase {
                    case .success(let image):
                        image.resizable().scaledToFill()
                    case .empty:
                        ProgressView()
                    case .failure:
                        initialsView
                    @unknown default:
                        initialsView
                    }
                }
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
