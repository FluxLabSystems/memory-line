import Foundation
import os

/// Default status-code retry classification (design §16.2): request timeout,
/// rate limiting, and server errors can be retried as-is; other client errors
/// cannot. The server's own `ApiError.retryable` flag can upgrade a 4xx (e.g.
/// 409 `artifact_incomplete_parts`) to retryable, never downgrade.
private func defaultRetryable(forStatus status: Int) -> Bool {
    status == 408 || status == 429 || (500...599).contains(status)
}

/// Concrete `SyncAPI` client for the Memory Line Sync Service v1
/// (`shared-contracts/openapi/memory-line-sync-v1.yaml`).
///
/// - Base URL comes from `SettingsStore` (`AppSettingsKey.serverURL`, trailing
///   slashes trimmed); every path lives under `/api/v1`.
/// - Every authenticated call attaches the bearer access token from
///   `TokenStore`. A 401 triggers exactly one token refresh (serialized by
///   `TokenRefresher`) followed by one retry of the original request.
/// - All failures surface as `SyncAPIError`; transport failures (`URLError`)
///   are retryable, HTTP failures follow `defaultRetryable(forStatus:)` plus
///   the server's `ApiError.retryable` flag.
/// - Never logs request/response bodies, tokens, or the pairing code
///   (design §14.5) — only methods, paths, IDs, and status codes.
final class SyncAPIClient: SyncAPI {
    private let settings: any SettingsStore
    private let tokens: any TokenStore
    private let session: URLSession
    private let refresher: TokenRefresher
    private let logger = Logger(subsystem: "com.memoryline.companion", category: "SyncAPIClient")

    /// - Parameters:
    ///   - settings: source of the configured server URL and device ID.
    ///   - tokens: Keychain-backed token storage.
    ///   - session: injectable for tests; defaults to `URLSession.shared`.
    init(settings: any SettingsStore, tokens: any TokenStore, session: URLSession = .shared) {
        self.settings = settings
        self.tokens = tokens
        self.session = session
        self.refresher = TokenRefresher(tokens: tokens, session: session)
    }

    // MARK: - SyncAPI

    /// Registers this device using the pairing code. Ignores any stored server
    /// URL/credentials — the explicit `serverURL` is used, and the caller is
    /// responsible for persisting the returned identity and tokens.
    func register(serverURL: String, request: DeviceRegisterRequest) async throws -> DeviceRegisterResponse {
        guard let base = Self.normalizedBaseURL(serverURL) else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "Enter the sync server URL first.")
        }
        let url = try Self.endpointURL(base: base, path: "/devices/register")
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("application/json", forHTTPHeaderField: "Accept")
        urlRequest.httpBody = try encode(request)
        let (data, http) = try await send(urlRequest, uploadFile: nil)
        guard (200...299).contains(http.statusCode) else {
            logger.warning("device registration failed status=\(http.statusCode)")
            throw failure(status: http.statusCode, data: data)
        }
        logger.info("device registered")
        return try decode(DeviceRegisterResponse.self, from: data)
    }

    /// `DELETE /api/v1/devices/{deviceId}` for the stored device ID.
    func revokeDevice() async throws {
        guard let deviceId = settings.string(AppSettingsKey.deviceId), !deviceId.isEmpty else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "This device is not paired with a sync server.")
        }
        _ = try await authenticated(method: "DELETE", path: "/devices/\(deviceId)")
        logger.info("device revoked")
    }

    /// `POST /api/v1/captures` — idempotent by client-generated captureId
    /// (a replay returns the existing capture with 200).
    func createCapture(_ request: CaptureCreateRequest) async throws -> CaptureResponse {
        let data = try await authenticated(
            method: "POST", path: "/captures",
            body: encode(request), contentType: "application/json")
        return try decode(CaptureResponse.self, from: data)
    }

    /// `POST /api/v1/captures/{captureId}/artifacts/initiate` — an identical
    /// re-declaration replays the upload plan with 200.
    func initiateArtifact(captureId: String, _ request: ArtifactInitiateRequest) async throws -> ArtifactInitiateResponse {
        let data = try await authenticated(
            method: "POST", path: "/captures/\(captureId)/artifacts/initiate",
            body: encode(request), contentType: "application/json")
        return try decode(ArtifactInitiateResponse.self, from: data)
    }

    /// `PUT /api/v1/artifacts/{artifactId}/parts/{partNumber}` — streams the
    /// file's bytes as `application/octet-stream`. Re-PUTting a part number
    /// overwrites it, so this call is inherently retry-safe.
    func uploadArtifactPart(artifactId: String, partNumber: Int, fileURL: URL) async throws {
        _ = try await authenticated(
            method: "PUT", path: "/artifacts/\(artifactId)/parts/\(partNumber)",
            uploadFile: fileURL, contentType: "application/octet-stream")
    }

    /// `POST /api/v1/artifacts/{artifactId}/complete` — idempotent for the
    /// same sha256 once the artifact is complete.
    func completeArtifact(artifactId: String, _ request: ArtifactCompleteRequest) async throws -> ArtifactCompleteResponse {
        let data = try await authenticated(
            method: "POST", path: "/artifacts/\(artifactId)/complete",
            body: encode(request), contentType: "application/json")
        return try decode(ArtifactCompleteResponse.self, from: data)
    }

    /// `GET /api/v1/sync/pull?cursor=&limit=`.
    func pull(cursor: Int64, limit: Int) async throws -> SyncPullResponse {
        let query = [
            URLQueryItem(name: "cursor", value: String(cursor)),
            URLQueryItem(name: "limit", value: String(limit)),
        ]
        let data = try await authenticated(method: "GET", path: "/sync/pull", query: query)
        return try decode(SyncPullResponse.self, from: data)
    }

    /// `POST /api/v1/sync/push`.
    ///
    /// Not a `SyncAPI` requirement, deliberately — see the note on
    /// `SyncPushRequest`. It is a concrete method on the client so the one app
    /// that pushes can call it without every fake in both test suites having to
    /// implement it.
    ///
    /// The response is per-entry and a 200 does **not** mean everything was
    /// accepted: the server reports each entry's outcome in `results`, so the
    /// caller must inspect them rather than treating the call's success as the
    /// entries' success.
    func push(_ request: SyncPushRequest) async throws -> SyncPushResponse {
        let data = try await authenticated(
            method: "POST", path: "/sync/push",
            body: encode(request), contentType: "application/json")
        return try decode(SyncPushResponse.self, from: data)
    }

    /// `POST /api/v1/sync/ack`.
    func ack(cursor: Int64) async throws {
        _ = try await authenticated(
            method: "POST", path: "/sync/ack",
            body: encode(SyncAckRequest(cursor: cursor)), contentType: "application/json")
    }

    // MARK: - Assistant (Phase 4)

    /// `POST /api/v1/assistant/sessions`.
    ///
    /// Like `push`, these four are concrete methods rather than `SyncAPI`
    /// requirements: only the Mac has an Ask surface today, and a protocol
    /// requirement would oblige every fake in both test suites to implement
    /// calls the phone never makes.
    func createAssistantSession(_ request: AssistantSessionCreateRequest) async throws -> AssistantSessionResponse {
        let data = try await authenticated(
            method: "POST", path: "/assistant/sessions",
            body: encode(request), contentType: "application/json")
        return try decode(AssistantSessionResponse.self, from: data)
    }

    /// `POST /api/v1/assistant/sessions/{sessionId}/turns` — idempotent on the
    /// client-generated turnId (a replay returns the stored turn with 200,
    /// answer included if one has arrived).
    func submitAssistantTurn(sessionId: String, _ request: AssistantTurnCreateRequest) async throws -> AssistantTurnResponse {
        let data = try await authenticated(
            method: "POST", path: "/assistant/sessions/\(sessionId)/turns",
            body: encode(request), contentType: "application/json")
        return try decode(AssistantTurnResponse.self, from: data)
    }

    /// `GET /api/v1/assistant/turns/{turnId}` — the poll for an answer.
    func assistantTurn(turnId: String) async throws -> AssistantTurnResponse {
        let data = try await authenticated(method: "GET", path: "/assistant/turns/\(turnId)")
        return try decode(AssistantTurnResponse.self, from: data)
    }

    /// `POST /api/v1/assistant/turns/{turnId}/cancel` — terminal; a late answer
    /// for a cancelled turn is discarded by the service.
    func cancelAssistantTurn(turnId: String) async throws -> AssistantTurnResponse {
        let data = try await authenticated(method: "POST", path: "/assistant/turns/\(turnId)/cancel")
        return try decode(AssistantTurnResponse.self, from: data)
    }

    // MARK: - Request plumbing

    /// Executes an authenticated request. On a 401 the token pair is refreshed
    /// once (serialized across concurrent callers by `TokenRefresher`) and the
    /// original request is retried exactly once with the new access token.
    private func authenticated(
        method: String,
        path: String,
        query: [URLQueryItem]? = nil,
        body: Data? = nil,
        uploadFile: URL? = nil,
        contentType: String? = nil
    ) async throws -> Data {
        let url = try apiURL(path: path, query: query)
        guard let accessToken = tokens.accessToken(), !accessToken.isEmpty else {
            throw SyncAPIError(
                statusCode: 401, apiError: nil, retryable: false,
                message: "This device is not paired. Pair with your sync server in Settings.")
        }
        // Captured before the request so a concurrent refresh is detectable.
        let observedRefreshToken = tokens.refreshToken()

        func makeRequest(token: String) -> URLRequest {
            var request = URLRequest(url: url)
            request.httpMethod = method
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            if let contentType {
                request.setValue(contentType, forHTTPHeaderField: "Content-Type")
            }
            if uploadFile == nil {
                request.httpBody = body
            }
            return request
        }

        var (data, http) = try await send(makeRequest(token: accessToken), uploadFile: uploadFile)

        if http.statusCode == 401 {
            try await refreshAccessToken(observedRefreshToken: observedRefreshToken)
            guard let renewed = tokens.accessToken(), !renewed.isEmpty else {
                throw SyncAPIError(
                    statusCode: 401, apiError: nil, retryable: false,
                    message: "Sync credentials are missing. Re-pair this device in Settings.")
            }
            (data, http) = try await send(makeRequest(token: renewed), uploadFile: uploadFile)
        }

        guard (200...299).contains(http.statusCode) else {
            logger.warning("\(method, privacy: .public) \(path, privacy: .public) failed status=\(http.statusCode)")
            throw failure(status: http.statusCode, data: data)
        }
        logger.debug("\(method, privacy: .public) \(path, privacy: .public) status=\(http.statusCode)")
        return data
    }

    /// Refreshes the token pair via the serialized `TokenRefresher`.
    private func refreshAccessToken(observedRefreshToken: String?) async throws {
        guard let deviceId = settings.string(AppSettingsKey.deviceId), !deviceId.isEmpty else {
            throw SyncAPIError(
                statusCode: 401, apiError: nil, retryable: false,
                message: "This device is not paired. Pair with your sync server in Settings.")
        }
        let endpoint = try apiURL(path: "/devices/\(deviceId)/refresh")
        try await refresher.refresh(endpoint: endpoint, observedRefreshToken: observedRefreshToken)
    }

    /// Performs the transfer and maps transport failures to retryable
    /// `SyncAPIError`s. Uses `URLSession.upload(for:fromFile:)` when a file is
    /// supplied so part bodies stream from disk instead of loading into memory.
    private func send(_ request: URLRequest, uploadFile: URL?) async throws -> (Data, HTTPURLResponse) {
        do {
            let (data, response): (Data, URLResponse)
            if let uploadFile {
                (data, response) = try await session.upload(for: request, fromFile: uploadFile)
            } else {
                (data, response) = try await session.data(for: request)
            }
            guard let http = response as? HTTPURLResponse else {
                throw SyncAPIError(
                    statusCode: nil, apiError: nil, retryable: true,
                    message: "The server returned a non-HTTP response.")
            }
            return (data, http)
        } catch let error as SyncAPIError {
            throw error
        } catch let error as URLError {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: true,
                message: error.localizedDescription)
        } catch {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: true,
                message: "Network error: \(error.localizedDescription)")
        }
    }

    // MARK: - Error / coding helpers

    /// Builds the error for a non-2xx response, decoding the `ApiError`
    /// envelope when the body carries one.
    private func failure(status: Int, data: Data) -> SyncAPIError {
        let apiError = try? SyncJSON.decoder.decode(ApiError.self, from: data)
        let retryable = defaultRetryable(forStatus: status) || (apiError?.retryable ?? false)
        let message = apiError?.message
            ?? "The server returned HTTP \(status) (\(HTTPURLResponse.localizedString(forStatusCode: status)))."
        return SyncAPIError(statusCode: status, apiError: apiError, retryable: retryable, message: message)
    }

    private func encode<T: Encodable>(_ value: T) throws -> Data {
        do {
            return try SyncJSON.encoder.encode(value)
        } catch {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "Could not encode the request.")
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        do {
            return try SyncJSON.decoder.decode(type, from: data)
        } catch {
            logger.error("response decoding failed for \(String(describing: type), privacy: .public)")
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "The server returned an unexpected response.")
        }
    }

    // MARK: - URL building

    /// Builds a URL under `{serverURL}/api/v1` from the stored server URL.
    private func apiURL(path: String, query: [URLQueryItem]? = nil) throws -> URL {
        guard
            let raw = settings.string(AppSettingsKey.serverURL),
            let base = Self.normalizedBaseURL(raw)
        else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "No sync server is configured. Pair with your server in Settings.")
        }
        return try Self.endpointURL(base: base, path: path, query: query)
    }

    /// Trims whitespace and trailing slashes; nil when nothing is left.
    static func normalizedBaseURL(_ raw: String) -> String? {
        var trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        while trimmed.hasSuffix("/") {
            trimmed.removeLast()
        }
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func endpointURL(base: String, path: String, query: [URLQueryItem]? = nil) throws -> URL {
        guard var components = URLComponents(string: base + "/api/v1" + path) else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "The configured server URL is not valid.")
        }
        if let query, !query.isEmpty {
            components.queryItems = query
        }
        guard let url = components.url, components.scheme != nil, components.host != nil else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "The configured server URL is not valid.")
        }
        return url
    }
}

/// Serializes refresh-token rotation (design §14.1) so that any number of
/// concurrent 401s produce at most one network refresh:
///
/// - callers that arrive while a refresh is in flight await the same task;
/// - a caller whose observed refresh token no longer matches the stored one
///   skips the network entirely — another caller already rotated the pair.
///
/// On success the rotated pair is persisted via `TokenStore.save` before any
/// caller resumes, so the retried request always reads a fresh access token.
actor TokenRefresher {
    private let tokens: any TokenStore
    private let session: URLSession
    private let logger = Logger(subsystem: "com.memoryline.companion", category: "TokenRefresher")
    private var inFlight: Task<Void, Error>?

    init(tokens: any TokenStore, session: URLSession) {
        self.tokens = tokens
        self.session = session
    }

    /// Ensures a fresh token pair is stored.
    ///
    /// - Parameters:
    ///   - endpoint: full URL of `POST /api/v1/devices/{deviceId}/refresh`.
    ///   - observedRefreshToken: the refresh token the caller read before its
    ///     request failed with 401; when the stored token differs, a
    ///     concurrent caller already refreshed and no network call is made.
    func refresh(endpoint: URL, observedRefreshToken: String?) async throws {
        if let inFlight {
            // Piggyback on the refresh already in progress.
            try await inFlight.value
            return
        }
        guard let current = tokens.refreshToken(), !current.isEmpty else {
            throw SyncAPIError(
                statusCode: 401, apiError: nil, retryable: false,
                message: "This device is not paired. Pair with your sync server in Settings.")
        }
        if let observed = observedRefreshToken, observed != current {
            // The stored pair rotated since the caller read it; reuse it.
            return
        }
        // No suspension between the checks above and this assignment, so
        // concurrent callers always find `inFlight` set.
        let task = Task { try await self.performRefresh(endpoint: endpoint, refreshToken: current) }
        inFlight = task
        defer { inFlight = nil }
        try await task.value
    }

    private func performRefresh(endpoint: URL, refreshToken: String) async throws {
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        do {
            request.httpBody = try SyncJSON.encoder.encode(TokenRefreshRequest(refreshToken: refreshToken))
        } catch {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "Could not encode the token refresh request.")
        }

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: true,
                message: "Network error while renewing sync credentials.")
        }
        guard let http = response as? HTTPURLResponse else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: true,
                message: "Unexpected response while renewing sync credentials.")
        }
        guard (200...299).contains(http.statusCode) else {
            let apiError = try? SyncJSON.decoder.decode(ApiError.self, from: data)
            logger.warning("token refresh failed status=\(http.statusCode) code=\(apiError?.code ?? "unknown", privacy: .public)")
            if http.statusCode == 401 {
                throw SyncAPIError(
                    statusCode: 401, apiError: apiError, retryable: false,
                    message: "This device's sync access was revoked or expired. Re-pair in Settings.")
            }
            let retryable = defaultRetryable(forStatus: http.statusCode) || (apiError?.retryable ?? false)
            throw SyncAPIError(
                statusCode: http.statusCode, apiError: apiError, retryable: retryable,
                message: apiError?.message ?? "Token refresh failed (HTTP \(http.statusCode)).")
        }

        let refreshed: TokenRefreshResponse
        do {
            refreshed = try SyncJSON.decoder.decode(TokenRefreshResponse.self, from: data)
        } catch {
            throw SyncAPIError(
                statusCode: http.statusCode, apiError: nil, retryable: true,
                message: "The server returned an unexpected token refresh response.")
        }
        do {
            try tokens.save(accessToken: refreshed.accessToken, refreshToken: refreshed.refreshToken)
        } catch {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "Could not store the renewed sync credentials in the Keychain.")
        }
        logger.info("access token refreshed")
    }
}
