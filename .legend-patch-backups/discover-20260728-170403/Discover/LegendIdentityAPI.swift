import Foundation

/// Independent identity and discovery authority.
///
/// Messaging intentionally does not own member discovery.
protocol LegendIdentityAPI: Sendable {
    func discover(
        accessToken: String
    ) async throws -> LegendDiscoverSnapshot

    func search(
        query: String,
        accessToken: String
    ) async throws -> [LegendIdentitySearchResult]

    func profile(
        username: String,
        accessToken: String
    ) async throws -> LegendIdentity
}

struct MobileUnavailableLegendIdentityAPI: LegendIdentityAPI {
    func discover(
        accessToken: String
    ) async throws -> LegendDiscoverSnapshot {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func search(
        query: String,
        accessToken: String
    ) async throws -> [LegendIdentitySearchResult] {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func profile(
        username: String,
        accessToken: String
    ) async throws -> LegendIdentity {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionLegendIdentityAPI: LegendIdentityAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func discover(
        accessToken: String
    ) async throws -> LegendDiscoverSnapshot {
        try await client.get(
            "/api/v1/mobile/social/discover",
            accessToken: accessToken,
            headers: participantHeader,
            response: LegendDiscoverSnapshot.self
        )
    }

    func search(
        query: String,
        accessToken: String
    ) async throws -> [LegendIdentitySearchResult] {
        let normalizedQuery = query.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        return try await client.get(
            "/api/v1/mobile/social/search",
            accessToken: accessToken,
            queryItems: [
                URLQueryItem(
                    name: "query",
                    value: normalizedQuery
                )
            ],
            headers: participantHeader,
            response: [LegendIdentitySearchResult].self
        )
    }

    func profile(
        username: String,
        accessToken: String
    ) async throws -> LegendIdentity {
        let normalizedUsername = username
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()

        guard
            !normalizedUsername.isEmpty,
            let encodedUsername = normalizedUsername.addingPercentEncoding(
                withAllowedCharacters: .urlPathAllowed
            )
        else {
            throw LegendIdentityContractError.invalidUsername
        }

        return try await client.get(
            "/api/v1/mobile/social/profiles/\(encodedUsername)",
            accessToken: accessToken,
            headers: participantHeader,
            response: LegendIdentity.self
        )
    }

    private var participantHeader: [String: String] {
        [
            "X-Legend-Participant-Type": participantType.rawValue
        ]
    }
}

enum LegendIdentityContractError: LocalizedError, Equatable {
    case invalidUsername

    var errorDescription: String? {
        switch self {
        case .invalidUsername:
            return "Enter a valid Legend username."
        }
    }
}
