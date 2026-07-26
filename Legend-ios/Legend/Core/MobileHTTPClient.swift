import Foundation

struct MobileHTTPClient: Sendable {
    let baseURL: URL
    let session: URLSession

    init(baseURL: URL, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.session = session
    }

    func get<Response: Decodable>(
        _ path: String,
        accessToken: String,
        queryItems: [URLQueryItem] = [],
        headers: [String: String] = [:],
        response: Response.Type
    ) async throws -> Response {
        var request = URLRequest(url: try endpointURL(path, queryItems: queryItems))
        request.httpMethod = "GET"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        headers.forEach { request.setValue($0.value, forHTTPHeaderField: $0.key) }
        return try await perform(request, response: response)
    }

    func post<Body: Encodable, Response: Decodable>(
        _ path: String,
        body: Body,
        accessToken: String,
        idempotencyKey: UUID? = nil,
        headers: [String: String] = [:],
        response: Response.Type
    ) async throws -> Response {
        var request = URLRequest(url: try endpointURL(path, queryItems: []))
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        headers.forEach { request.setValue($0.value, forHTTPHeaderField: $0.key) }
        if let idempotencyKey {
            request.setValue(idempotencyKey.uuidString, forHTTPHeaderField: "Idempotency-Key")
        }
        request.httpBody = try JSONEncoder.mobile.encode(body)
        return try await perform(request, response: response)
    }

    func post<Body: Encodable>(
        _ path: String,
        body: Body,
        accessToken: String,
        idempotencyKey: UUID? = nil,
        headers: [String: String] = [:]
    ) async throws {
        var request = URLRequest(url: try endpointURL(path, queryItems: []))
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        headers.forEach { request.setValue($0.value, forHTTPHeaderField: $0.key) }
        if let idempotencyKey {
            request.setValue(idempotencyKey.uuidString, forHTTPHeaderField: "Idempotency-Key")
        }
        request.httpBody = try JSONEncoder.mobile.encode(body)
        try await performEmpty(request)
    }

    private func endpointURL(_ path: String, queryItems: [URLQueryItem]) throws -> URL {
        guard path.hasPrefix("/") else { throw MobileAPIError.invalidPath }
        guard var components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else {
            throw MobileAPIError.invalidBaseURL
        }
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let suffix = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, suffix].filter { !$0.isEmpty }.joined(separator: "/")
        components.queryItems = queryItems.isEmpty ? nil : queryItems
        guard let url = components.url else { throw MobileAPIError.invalidBaseURL }
        return url
    }

    private func perform<Response: Decodable>(_ request: URLRequest, response: Response.Type) async throws -> Response {
        let hasAuthorizationHeader = request.value(forHTTPHeaderField: "Authorization") != nil
        MobileDebugDiagnostics.record("Mobile API request started. Authorization header present: \(hasAuthorizationHeader).")
        let (data, urlResponse) = try await requestData(for: request)
        guard let http = urlResponse as? HTTPURLResponse else {
            throw MobileAPIError.invalidServerResponse
        }

        let correlationID = http.value(forHTTPHeaderField: "X-Correlation-ID")
        let problem = MobileAPIProblem.decode(from: data, fallbackCorrelationID: correlationID)
        MobileDebugDiagnostics.record(
            "Mobile API response status \(http.statusCode).",
            correlationID: problem.correlationID)
        switch http.statusCode {
        case 200 ... 299:
            do {
                return try JSONDecoder.mobile.decode(Response.self, from: data)
            } catch {
                throw MobileAPIError.decodingFailed(correlationID: correlationID)
            }
        case 401:
            throw MobileAPIError.apiUnauthorized(code: problem.code, correlationID: problem.correlationID)
        case 403:
            throw MobileAPIError.apiForbidden(code: problem.code, correlationID: problem.correlationID)
        case 409:
            throw MobileAPIError.apiConflict(code: problem.code, correlationID: problem.correlationID)
        default:
            throw MobileAPIError.server(statusCode: http.statusCode, correlationID: problem.correlationID)
        }
    }

    private func performEmpty(_ request: URLRequest) async throws {
        let (data, urlResponse) = try await requestData(for: request)
        guard let http = urlResponse as? HTTPURLResponse else {
            throw MobileAPIError.invalidServerResponse
        }
        let correlationID = http.value(forHTTPHeaderField: "X-Correlation-ID")
        let problem = MobileAPIProblem.decode(from: data, fallbackCorrelationID: correlationID)
        MobileDebugDiagnostics.record("Mobile API response status \(http.statusCode).", correlationID: problem.correlationID)
        switch http.statusCode {
        case 200 ... 299:
            return
        case 401:
            throw MobileAPIError.apiUnauthorized(code: problem.code, correlationID: problem.correlationID)
        case 403:
            throw MobileAPIError.apiForbidden(code: problem.code, correlationID: problem.correlationID)
        case 409:
            throw MobileAPIError.apiConflict(code: problem.code, correlationID: problem.correlationID)
        default:
            throw MobileAPIError.server(statusCode: http.statusCode, correlationID: problem.correlationID)
        }
    }

    private func requestData(for request: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: request)
        } catch let error as URLError {
            switch error.code {
            case .notConnectedToInternet,
                 .networkConnectionLost,
                 .cannotConnectToHost,
                 .cannotFindHost,
                 .dnsLookupFailed,
                 .timedOut:
                throw MobileAPIError.networkUnavailable
            default:
                throw error
            }
        }
    }
}

private struct MobileAPIProblem: Decodable {
    let code: String?
    let correlationID: String?

    private enum CodingKeys: String, CodingKey {
        case code
        case correlationID = "correlationId"
    }

    static func decode(from data: Data, fallbackCorrelationID: String?) -> MobileAPIProblem {
        let decoded = try? JSONDecoder.mobile.decode(MobileAPIProblem.self, from: data)
        return MobileAPIProblem(
            code: decoded?.code,
            correlationID: decoded?.correlationID ?? fallbackCorrelationID)
    }
}

enum MobileAPIError: LocalizedError, Equatable {
    case invalidBaseURL
    case invalidPath
    case invalidServerResponse
    case networkUnavailable
    case decodingFailed(correlationID: String?)
    case unauthorized(correlationID: String?)
    case forbidden(correlationID: String?)
    case conflict(correlationID: String?)
    case apiUnauthorized(code: String?, correlationID: String?)
    case apiForbidden(code: String?, correlationID: String?)
    case apiConflict(code: String?, correlationID: String?)
    case server(statusCode: Int, correlationID: String?)

    var correlationID: String? {
        switch self {
        case .decodingFailed(let correlationID), .unauthorized(let correlationID), .forbidden(let correlationID), .conflict(let correlationID), .server(_, let correlationID), .apiUnauthorized(_, let correlationID), .apiForbidden(_, let correlationID), .apiConflict(_, let correlationID):
            return correlationID
        case .invalidBaseURL, .invalidPath, .invalidServerResponse, .networkUnavailable:
            return nil
        }
    }

    var safeCode: String? {
        switch self {
        case .apiUnauthorized(let code, _), .apiForbidden(let code, _), .apiConflict(let code, _):
            return code
        case .invalidBaseURL, .invalidPath, .invalidServerResponse, .networkUnavailable, .decodingFailed, .unauthorized, .forbidden, .conflict, .server:
            return nil
        }
    }

    var provesInvalidBearerCredential: Bool {
        if case .apiUnauthorized(let code, _) = self {
            return code == "mobile_authentication_required"
        }
        return false
    }

    var errorDescription: String? {
        switch self {
        case .invalidBaseURL, .invalidPath:
            return "The mobile service configuration is invalid."
        case .invalidServerResponse, .decodingFailed:
            return "The service returned an unexpected response."
        case .networkUnavailable:
            return "The network connection is unavailable."
        case .unauthorized, .apiUnauthorized:
            return "Your session has ended. Please sign in again."
        case .forbidden, .apiForbidden:
            return "You do not have access to this action."
        case .conflict, .apiConflict:
            return "Choose an authorized role before continuing."
        case .server:
            return "The service could not complete this request."
        }
    }
}

extension JSONEncoder {
    static let mobile: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()
}

extension JSONDecoder {
    static let mobile: JSONDecoder = {
        let decoder = JSONDecoder()
        // ASP.NET Core serializes UTC DateTime values with up to seven
        // fractional-second digits. JSONDecoder.iso8601 only accepts the
        // whole-second form, which made otherwise valid mobile responses fail
        // as soon as any dated field was present.
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let value = try container.decode(String.self)
            if let date = MobileAPIDateParser.parse(value) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "The mobile API returned an invalid UTC timestamp.")
        }
        return decoder
    }()
}

private enum MobileAPIDateParser {
    private static let fractionalSeconds: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private static let wholeSeconds: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()

    static func parse(_ value: String) -> Date? {
        fractionalSeconds.date(from: value) ?? wholeSeconds.date(from: value)
    }
}
