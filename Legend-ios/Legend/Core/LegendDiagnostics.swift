import Combine
import Foundation
#if DEBUG
import os
#endif

enum DiagnosticCategory: String, Sendable {
    case configuration
    case authentication
    case networking
    case messaging
}

struct DiagnosticEvent: Equatable, Sendable, Identifiable {
    let id: UUID
    let timestamp: Date
    let category: DiagnosticCategory
    let summary: String
    let correlationID: String?

    init(category: DiagnosticCategory, summary: String, correlationID: String? = nil) {
        self.id = UUID()
        self.timestamp = Date()
        self.category = category
        self.summary = DiagnosticRedactor.redact(summary)
        self.correlationID = correlationID.map(DiagnosticRedactor.redact)
    }
}

enum DiagnosticRedactor {
    static func redact(_ value: String) -> String {
        value
            .replacingOccurrences(of: #"(?i)bearer\s+[A-Za-z0-9._\-]+"#, with: "Bearer [redacted]", options: .regularExpression)
            .replacingOccurrences(of: #"(?i)(access_token|refresh_token|id_token)=([^&\s]+)"#, with: "$1=[redacted]", options: .regularExpression)
    }
}

@MainActor
final class LegendDiagnostics: ObservableObject {
    @Published private(set) var events: [DiagnosticEvent] = []
    private let maximumEvents = 50

    func record(category: DiagnosticCategory, summary: String, correlationID: String? = nil) {
        let event = DiagnosticEvent(category: category, summary: summary, correlationID: correlationID)
        events.insert(event, at: 0)
        if events.count > maximumEvents {
            events.removeLast(events.count - maximumEvents)
        }
        MobileDebugDiagnostics.record(event.summary, correlationID: event.correlationID)
    }
}

enum MobileDebugDiagnostics {
    static func record(_ summary: String, correlationID: String? = nil) {
        #if DEBUG
        let suffix = correlationID.map { " correlation=\($0)" } ?? ""
        Logger(subsystem: "com.mylegnd.legend", category: "mobile-auth")
            .debug("\(DiagnosticRedactor.redact(summary + suffix), privacy: .public)")
        #endif
    }
}
