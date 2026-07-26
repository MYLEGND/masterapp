import Combine
import Foundation

@MainActor
final class MessagingStore: ObservableObject {
    @Published private(set) var state: MessagingLoadState = .idle
    @Published private(set) var selectedConversationID: UUID?

    private let api: any MessagingAPI
    private let accessToken: String
    private let diagnostics: LegendDiagnostics

    init(api: any MessagingAPI, accessToken: String, diagnostics: LegendDiagnostics) {
        self.api = api
        self.accessToken = accessToken
        self.diagnostics = diagnostics
    }

    func load() {
        state = .loading
        Task {
            do {
                let conversations = try await api.conversations(accessToken: accessToken)
                state = .loaded(conversations)
            } catch MobileMessagingContractError.unavailable {
                state = .unavailable("Secure mobile messaging will be available after the server publishes the approved bearer API contract.")
            } catch {
                let apiError = error as? MobileAPIError
                state = .failed(UserFacingFailure(
                    title: "Messages unavailable",
                    message: error.localizedDescription,
                    correlationID: apiError?.correlationID
                ))
                diagnostics.record(category: .messaging, summary: "Conversation list could not be loaded.", correlationID: apiError?.correlationID)
            }
        }
    }

    func selectConversation(_ conversationID: UUID) {
        selectedConversationID = conversationID
    }
}
