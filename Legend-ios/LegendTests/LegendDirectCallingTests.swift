import XCTest
import AVFoundation
import Network
import UIKit
@preconcurrency import WebRTC
@testable import Legend

@MainActor
final class LegendDirectCallingTests: XCTestCase {
    func testCallingPreferencesStayOnExistingCallContractAndRingtonePathIsBounded() throws {
        let command = LegendCallCommand(action: "preferences", deviceId: UUID(),
            preferences: LegendCallPreferences(ringtoneId: "soft", wallpaperMode: "profile"))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(command)) as? [String: Any])
        XCTAssertEqual(json["action"] as? String, "preferences")
        let preferences = try XCTUnwrap(json["preferences"] as? [String: String])
        XCTAssertEqual(preferences, ["ringtoneId": "soft", "wallpaperMode": "profile"])
        let result = try JSONDecoder().decode(LegendCallResult.self, from: Data(#"{"succeeded":true,"preferences":{"ringtoneId":"soft","wallpaperMode":"profile"},"ringtones":[{"id":"soft","label":"Soft","resource":"legend_ringback"}],"wallpapers":[{"id":"profile","label":"Profile photo"}]}"#.utf8))
        XCTAssertEqual(result.preferences, command.preferences)
        XCTAssertEqual(result.ringtones?.first?.resource, "legend_ringback")
        XCTAssertEqual(result.wallpapers?.first?.id, "profile")
        XCTAssertEqual(LegendCallSystem.ringtoneFilename("legend_ringback"), "legend_ringback.wav")
        XCTAssertEqual(LegendCallSystem.ringtoneFilename("../../private"), "legend_incoming.wav")
        XCTAssertEqual(LegendCallSystem.ringtoneFilename("https://other.test/sound"), "legend_incoming.wav")
        XCTAssertEqual(LegendCallSystem.ringtoneFilename(nil), "legend_incoming.wav")
    }

    func testBroadcastReceiverRejectsStaleHandshakeWithoutEndingCurrentCapture() async throws {
        let directory = URL(fileURLWithPath: "/private/tmp/lcb-" + UUID().uuidString.prefix(8))
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let receiver = LegendCallBroadcastReceiver(rootResolver: { directory })
        defer { receiver.stop(notify: false) }
        var stopped = false
        receiver.onStopped = { stopped = true }
        try await receiver.prepare(width: 1280, height: 720, fps: 10)
        let file = LegendBroadcastProtocol.invitationURL(directory)
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while !FileManager.default.fileExists(atPath: file.path), ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        let invitation = try JSONDecoder().decode(LegendBroadcastProtocol.Invitation.self, from: Data(contentsOf: file))
        let socket = try XCTUnwrap(invitation.validated(root: directory))
        func connection(nonce: String) -> NWConnection {
            let client = NWConnection(to: .unix(path: socket.path), using: .tcp)
            client.stateUpdateHandler = { [weak client] state in
                if case .ready = state { client?.send(content: Data(nonce.utf8), completion: .contentProcessed { _ in }) }
            }
            client.start(queue: DispatchQueue(label: "legend.call.fixture"))
            return client
        }
        let rejected = expectation(description: "Stale broadcast is disconnected")
        let stale = connection(nonce: String(repeating: "0", count: 64))
        defer { stale.cancel() }
        stale.receive(minimumIncompleteLength: 1, maximumLength: 1) { _, _, complete, error in
            if complete || error != nil { rejected.fulfill() }
        }
        await fulfillment(of: [rejected], timeout: 3)
        XCTAssertFalse(stopped)
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
        let received = expectation(description: "Current broadcast reaches existing frame receiver")
        receiver.onFrame = { pixels, timestamp in
            XCTAssertEqual(CVPixelBufferGetWidth(pixels), 2)
            XCTAssertEqual(CVPixelBufferGetHeight(pixels), 2)
            XCTAssertEqual(timestamp, 123)
            received.fulfill()
        }
        let current = connection(nonce: invitation.nonce)
        defer { current.cancel() }
        let format = UIGraphicsImageRendererFormat(); format.scale = 1
        let image = UIGraphicsImageRenderer(size: CGSize(width: 2, height: 2), format: format).image { context in
            UIColor.blue.setFill(); context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        }
        let data = try XCTUnwrap(image.jpegData(compressionQuality: 0.5))
        var frame = LegendBroadcastProtocol.encodeHeader(size: data.count, timestamp: 123); frame.append(data)
        // The connection serializes this after its handshake send.
        let sendDeadline = ContinuousClock.now.advanced(by: .seconds(3))
        while FileManager.default.fileExists(atPath: file.path), ContinuousClock.now < sendDeadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: file.path))
        let acknowledged = expectation(description: "Receiver grants admission for exactly the next frame")
        current.receive(minimumIncompleteLength: 1, maximumLength: 1) { data, _, _, _ in
            XCTAssertEqual(data, Data([1])); acknowledged.fulfill()
        }
        current.send(content: frame, completion: .contentProcessed { _ in })
        await fulfillment(of: [received, acknowledged], timeout: 3)
        XCTAssertFalse(stopped)
        receiver.stop(notify: false)
        XCTAssertFalse(FileManager.default.fileExists(atPath: file.path))
    }

    func testBroadcastInvitationAndFrameBoundsRejectUntrustedIPC() throws {
        let root = URL(fileURLWithPath: "/private/tmp/call-group")
        func invitation(nonce: String = String(repeating: "a", count: 64), socket: String = "call.sock",
                        expiry: TimeInterval = 60, width: Int = 1280, fps: Int = 10) -> LegendBroadcastProtocol.Invitation {
            .init(nonce: nonce, socketName: socket, expiresUtc: Date().addingTimeInterval(expiry), width: width, height: 720, fps: fps)
        }
        XCTAssertNotNil(invitation().validated(root: root))
        XCTAssertNil(invitation(nonce: "unbound").validated(root: root))
        XCTAssertNil(invitation(socket: "../other-call").validated(root: root))
        XCTAssertNil(invitation(expiry: -1).validated(root: root))
        XCTAssertNil(invitation(expiry: 3600).validated(root: root))
        XCTAssertNil(invitation(width: 8192).validated(root: root))
        XCTAssertNil(invitation(fps: 60).validated(root: root))
        XCTAssertNil(invitation().validated(root: URL(fileURLWithPath: "/" + String(repeating: "x", count: 104))))
        let valid = LegendBroadcastProtocol.decodeHeader(LegendBroadcastProtocol.encodeHeader(size: 2_000_000, timestamp: 123456789))
        XCTAssertEqual(valid?.size, 2_000_000)
        XCTAssertEqual(valid?.timestamp, 123456789)
        XCTAssertNil(LegendBroadcastProtocol.decodeHeader(LegendBroadcastProtocol.encodeHeader(size: 2_000_001, timestamp: 1)))
        XCTAssertNil(LegendBroadcastProtocol.decodeHeader(LegendBroadcastProtocol.encodeHeader(size: 0, timestamp: 1)))
        XCTAssertNil(LegendBroadcastProtocol.decodeHeader(LegendBroadcastProtocol.encodeHeader(size: 1, timestamp: -1)))
        XCTAssertNil(LegendBroadcastProtocol.decodeHeader(Data(repeating: 0, count: 13)))
    }

    func testLockedRestoreAndCallWakeRemainFencedDuringBiometricPromptAndSignOut() async throws {
        let service = CallingWakeService(actorID: "callee", suspended: true)
        let biometric = CallingWakeBiometrics(suspended: true)
        let coordinator = wakeCoordinator(tokens: CallingWakeTokens(), service: service, biometric: biometric)
        coordinator.restore()
        while biometric.authenticationCount == 0 { await Task.yield() }
        XCTAssertEqual(coordinator.state, .loading)
        let task = Task { try await coordinator.prepareIncomingCall(wakeCall()) }
        while await service.requests == 0 { await Task.yield() }
        XCTAssertEqual(coordinator.state, .loading, "Incoming calls must not unlock the protected application")
        coordinator.signOut()
        await service.resume()
        biometric.resume(true)
        do { _ = try await task.value; XCTFail("Retired incoming wake must not survive sign-out") } catch {}
        for _ in 0..<10 { await Task.yield() }
        XCTAssertEqual(coordinator.state, .signedOut)
        let requests = await service.requests
        XCTAssertEqual(requests, 1, "The old Face ID result must not begin another bootstrap")
    }

    func testLockedIncomingWakeUsesSelectedKeychainIdentityWithoutUnlockingProtectedUI() async throws {
        let tokens = CallingWakeTokens()
        let service = CallingWakeService(actorID: "callee")
        let biometric = CallingWakeBiometrics()
        let coordinator = wakeCoordinator(tokens: tokens, service: service, biometric: biometric)
        let store = try await coordinator.prepareIncomingCall(wakeCall())
        defer { store.shutdown(); coordinator.signOut() }
        XCTAssertTrue(store.owns(wakeCall()))
        XCTAssertEqual(coordinator.state, .loading)
        XCTAssertEqual(biometric.authenticationCount, 0)
        let requests = await service.requests
        XCTAssertEqual(requests, 1, "A locked launch must still validate its selected actor on the server")
    }

    func testLockedIncomingWakeRejectsWrongPushActorAndServerActorWithoutUnlockingUI() async throws {
        for wrongPush in [true, false] {
            let service = CallingWakeService(actorID: wrongPush ? "callee" : "other-account")
            let biometric = CallingWakeBiometrics()
            let coordinator = wakeCoordinator(tokens: CallingWakeTokens(), service: service, biometric: biometric)
            do {
                _ = try await coordinator.prepareIncomingCall(wakeCall(callee: wrongPush ? "other-account" : "callee"))
                XCTFail("An incoming push and server bootstrap must both match the selected typed account")
            } catch {}
            XCTAssertEqual(coordinator.state, .loading)
            XCTAssertEqual(biometric.authenticationCount, 0)
            let requests = await service.requests
            XCTAssertEqual(requests, wrongPush ? 0 : 1)
            coordinator.signOut()
        }
    }

    func testLockedIncomingWakeRejectsLegacyUnboundCredentialAndRevokedSession() async throws {
        let biometric = CallingWakeBiometrics()
        for legacy in [true, false] {
            let service = CallingWakeService(actorID: "callee", authenticated: false)
            let tokens: any SecureTokenStoring = legacy ? CallingLegacyWakeTokens() : CallingWakeTokens()
            let coordinator = wakeCoordinator(tokens: tokens, service: service, biometric: biometric)
            do { _ = try await coordinator.prepareIncomingCall(wakeCall()); XCTFail("The wake must fail closed") }
            catch {}
            XCTAssertEqual(coordinator.state, .loading)
            let requests = await service.requests
            XCTAssertEqual(requests, legacy ? 0 : 1)
            coordinator.signOut()
        }
        XCTAssertEqual(biometric.authenticationCount, 0)
    }

    func testSignOutDuringLockedBootstrapCannotAttachAnOldCallOwner() async throws {
        let service = CallingWakeService(actorID: "callee", suspended: true)
        let coordinator = wakeCoordinator(tokens: CallingWakeTokens(), service: service, biometric: CallingWakeBiometrics())
        let task = Task { try await coordinator.prepareIncomingCall(wakeCall()) }
        while await service.requests == 0 { await Task.yield() }
        coordinator.signOut()
        await service.resume()
        do { _ = try await task.value; XCTFail("Signing out must invalidate an in-flight call wake") }
        catch {}
        XCTAssertEqual(coordinator.state, .signedOut)
    }

    func testParticipantImagesUseSignedSameOriginCurrentCallAndCorrectDirection() throws {
        var call = wakeCall()
        let base = URL(string: "https://api.example.test")!
        XCTAssertNil(call.participantImageURL(outgoing: true, apiBaseURL: base))
        let path = "/api/v1/mobile/notifications/\(call.id.uuidString)/sender-image"
        call.calleeImagePath = path + "?token=callee-receipt"
        call.callerImagePath = path + "?token=caller-receipt"
        XCTAssertEqual(call.participantImageURL(outgoing: true, apiBaseURL: base)?.query, "token=callee-receipt")
        XCTAssertEqual(call.participantImageURL(outgoing: false, apiBaseURL: base)?.query, "token=caller-receipt")
        call.callerWallpaperMode = "profile"; call.calleeWallpaperMode = "legend"
        XCTAssertNil(call.wallpaperImageURL(outgoing: true, apiBaseURL: base))
        XCTAssertEqual(call.wallpaperImageURL(outgoing: false, apiBaseURL: base)?.query, "token=caller-receipt")
        call.callerWallpaperMode = "legend"; call.calleeWallpaperMode = "profile"
        XCTAssertEqual(call.wallpaperImageURL(outgoing: true, apiBaseURL: base)?.query, "token=callee-receipt")
        XCTAssertNil(call.wallpaperImageURL(outgoing: false, apiBaseURL: base))
        for invalid in ["https://other.test" + path, "//other.test" + path,
                        "/api/v1/mobile/notifications/\(UUID())/sender-image?token=wrong-call",
                        path + "#fragment", "/api/v1/mobile/notifications/../private"] {
            call.calleeImagePath = invalid
            XCTAssertNil(call.participantImageURL(outgoing: true, apiBaseURL: base))
        }
        XCTAssertNil(call.participantImageURL(outgoing: false, apiBaseURL: URL(string: "http://api.example.test")))
    }

    private func wakeCall(callee: String = "callee") -> LegendCallSnapshot {
        LegendCallSnapshot(id: UUID(), conversationId: UUID(), callerUserId: "caller", callerType: "Client",
            calleeUserId: callee, calleeType: "Client", callerDeviceId: UUID(), calleeDeviceId: nil,
            callerName: "Caller", calleeName: "Recipient", video: false, status: "ringing",
            createdUtc: Date(), expiresUtc: Date().addingTimeInterval(45), epoch: 0)
    }

    private func wakeCoordinator(tokens: any SecureTokenStoring, service: CallingWakeService,
                                 biometric: CallingWakeBiometrics) -> MobileSessionCoordinator {
        MobileSessionCoordinator(configuration: MobileConfiguration(bundleIdentifier: "com.mylegnd.legend.registered",
            apiBaseURL: URL(string: "https://api.example.test")!,
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize")!,
            tokenEndpoint: URL(string: "https://identity.example.test/token")!, clientID: "public-client",
            redirectScheme: "com-mylegnd-legend-registered", scope: "openid profile api://legend/mobile_access",
            audience: "api://legend"), tokenStore: tokens, sessionService: service,
            launchCache: CallingLockedLaunchCache(), biometricSecurity: biometric)
    }

    func testDetachingCallPresentationDoesNotEndAnAccountOwnedPendingCall() throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }
        store.start(conversationId: UUID(), video: false, recipientName: "Recipient")
        XCTAssertTrue(store.isStarting)
        let coordinator = LegendCallPresentation.Coordinator(store: store)
        LegendCallPresentation.dismantleUIView(UIView(), coordinator: coordinator)
        XCTAssertTrue(store.isStarting, "A view disappearing must not terminate its account's active call authority")
        XCTAssertEqual(store.name, "Recipient")
        store.end()
    }

    func testAccountLifecycleRetiresRetainedCallsButPreservesSameIdentityRefresh() throws {
        let identity = try LogicalParticipantIdentity(userID: "caller", participantType: .client)
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport, identity: identity)
        defer { store.shutdown() }
        let coordinator = MobileSessionCoordinator(tokenStore: CallingTestTokenStore())
        let actor = try MobileActor(identity: identity, profileID: "00000000-0000-0000-0000-000000000001", displayName: "Caller", avatar: nil)
        store.start(conversationId: UUID(), video: false, recipientName: "Recipient")
        coordinator.handleCallAccountTransition(to: .loading)
        coordinator.handleCallAccountTransition(to: .authenticating)
        coordinator.handleCallAccountTransition(to: .authenticated(MobileSession(actor: actor, capabilities: ["messaging"])))
        XCTAssertTrue(store.isStarting)
        coordinator.signOut()
        XCTAssertFalse(store.isStarting, "Sign-out must retire even a strongly retained call store synchronously")
        store.start(conversationId: UUID(), video: false)
        XCTAssertFalse(store.isStarting)
    }

    func testActualRoleChangeRetiresPreviousCallOwnerSynchronously() throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }
        let coordinator = MobileSessionCoordinator(tokenStore: CallingTestTokenStore())
        let changedActor = try MobileActor(identity: LogicalParticipantIdentity(userID: "caller", participantType: .agent),
            profileID: "00000000-0000-0000-0000-000000000001", displayName: "Caller", avatar: nil)
        store.start(conversationId: UUID(), video: false)
        coordinator.handleCallAccountTransition(to: .authenticated(MobileSession(actor: changedActor, capabilities: ["messaging"])))
        XCTAssertFalse(store.isStarting)
    }

    func testCallKitAudioActivationOrderAndRetiredStoreCannotMuteCurrentOwner() throws {
        func store(_ user: String) throws -> LegendCallStore {
            let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
                apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
                participantType: .client, accessTokenProvider: { throw CancellationError() }))
            return LegendCallStore(transport: transport,
                identity: try LogicalParticipantIdentity(userID: user, participantType: .client))
        }
        let system = LegendCallSystem.shared
        let first = try store("first")
        defer { first.shutdown(); system.audioActivationChanged(false) }
        system.audioActivationChanged(false)
        system.setMediaRequested(true, for: first)
        XCTAssertFalse(RTCAudioSession.sharedInstance().isAudioEnabled)
        system.audioActivationChanged(true)
        XCTAssertTrue(RTCAudioSession.sharedInstance().isAudioEnabled)
        system.setMediaRequested(false, for: first)
        XCTAssertFalse(RTCAudioSession.sharedInstance().isAudioEnabled)
        system.setMediaRequested(true, for: first)
        XCTAssertTrue(RTCAudioSession.sharedInstance().isAudioEnabled)
        let second = try store("second")
        defer { second.shutdown() }
        XCTAssertFalse(RTCAudioSession.sharedInstance().isAudioEnabled)
        system.setMediaRequested(true, for: second)
        XCTAssertTrue(RTCAudioSession.sharedInstance().isAudioEnabled)
        first.shutdown()
        XCTAssertTrue(RTCAudioSession.sharedInstance().isAudioEnabled)
        system.audioActivationChanged(false)
        XCTAssertFalse(RTCAudioSession.sharedInstance().isAudioEnabled)
    }

    func testPresentationMetadataFailureDoesNotRestartMediaAndLegacyPolicyDoesNotSendIt() async throws {
        var policy = LegendCallPolicy(stunUrls: [], wifiWidth: 1280, wifiHeight: 720, wifiFps: 30,
            cellularWidth: 640, cellularHeight: 360, cellularFps: 24, videoBitrate: 900_000,
            audioBitrate: 64_000, ringSeconds: 45, connectSeconds: 30, recoveryAttempts: 3)
        let legacy = try LegendRTCPeer(policy: policy, video: false, caller: true)
        defer { legacy.close() }
        var sent = 0
        legacy.onSignal = { _, _, _ in sent += 1 }
        try await legacy.receive(kind: "media-state", data: "{\"screenSharing\":true,\"request\":true}", epoch: 0)
        XCTAssertEqual(sent, 0)
        let legacySize = LegendCallScreenSharePolicy.fit(width: 1170, height: 2532, targetWidth: policy.cellularWidth, targetHeight: policy.cellularHeight)
        XCTAssertLessThanOrEqual(legacySize.width, policy.cellularHeight)
        XCTAssertLessThanOrEqual(legacySize.height, policy.cellularWidth)
        XCTAssertEqual(Double(legacySize.width) / Double(legacySize.height), 1170.0 / 2532.0, accuracy: 0.005)
        policy.screenShare = LegendCallScreenSharePolicy(highWidth: 1920, highHeight: 1080, highFps: 15, highBitrate: 2_500_000,
            mediumWidth: 1280, mediumHeight: 720, mediumFps: 10, mediumBitrate: 1_200_000,
            lowWidth: 960, lowHeight: 540, lowFps: 5, lowBitrate: 250_000, transportHeadroomFraction: 0.15)
        let peer = try LegendRTCPeer(policy: policy, video: false, caller: true)
        defer { peer.close() }
        var sharing = false
        var states: [String] = []
        peer.onRemoteScreenSharing = { sharing = $0 }
        peer.onState = { states.append($0) }
        peer.onSignal = { _, _, _ in sent += 1; throw CancellationError() }
        try await peer.receive(kind: "media-state", data: "{\"screenSharing\":true,\"request\":true}", epoch: 0)
        XCTAssertTrue(sharing)
        XCTAssertEqual(sent, 1)
        XCTAssertFalse(states.contains("Reconnecting"))
        try await peer.receive(kind: "media-state", data: "invalid", epoch: 0)
        XCTAssertTrue(sharing)
    }

    func testScreenSharingUsesCentralProfilesWithoutCroppingPortraitText() throws {
        let json = """
        {"highWidth":1920,"highHeight":1080,"highFps":15,"highBitrate":2500000,
         "mediumWidth":1280,"mediumHeight":720,"mediumFps":10,"mediumBitrate":1200000,
         "lowWidth":960,"lowHeight":540,"lowFps":5,"lowBitrate":250000,"transportHeadroomFraction":0.15}
        """
        let policy = try JSONDecoder().decode(LegendCallScreenSharePolicy.self, from: Data(json.utf8))
        let portrait = policy.dimensions(width: 1170, height: 2532, quality: 2)
        XCTAssertEqual(portrait.height, 1920)
        XCTAssertLessThanOrEqual(portrait.width, 1080)
        XCTAssertEqual(portrait.width % 2, 0)
        XCTAssertEqual(Double(portrait.width) / Double(portrait.height), 1170.0 / 2532.0, accuracy: 0.002)
        let landscape = policy.dimensions(width: 2532, height: 1170, quality: 2)
        XCTAssertEqual(landscape.width, portrait.height)
        XCTAssertEqual(landscape.height, portrait.width)
        let weakNetwork = policy.dimensions(width: 1920, height: 1080, quality: 0)
        XCTAssertEqual(weakNetwork.width, 960)
        XCTAssertEqual(weakNetwork.height, 540)
        XCTAssertEqual(policy.profile(quality: 0).fps, 5)
        XCTAssertEqual(policy.profile(quality: 1).bitrate, 1_200_000)
        XCTAssertEqual(policy.bitrate(quality: 2, availableBandwidth: 350_000, audioBitrate: 64_000), 233_500)
        XCTAssertEqual(policy.bitrate(quality: 0, availableBandwidth: 2_000_000, audioBitrate: 64_000), 250_000)
        XCTAssertEqual(policy.bitrate(quality: 2, availableBandwidth: 20_000, audioBitrate: 64_000), 0)
        XCTAssertEqual(policy.bitrate(quality: 2, availableBandwidth: nil, audioBitrate: 64_000), 2_500_000)
        let small = policy.dimensions(width: 320, height: 180, quality: 2)
        XCTAssertEqual(small.width, 320)
        XCTAssertEqual(small.height, 180)
    }

    func testAdaptiveQualityRecognizesWeakWifiAndDoesNotRecoverWithoutEvidence() {
        let tuning = LegendCallAdaptationPolicy(sampleSeconds: 3, recoverySamples: 4,
            lowBandwidth: 350_000, highBandwidth: 900_000, highLatencySeconds: 0.6, audioPriority: 4,
            lowWidth: 320, lowHeight: 180, lowFps: 15, lowBitrate: 250_000,
            mediumWidth: 640, mediumHeight: 360, mediumFps: 24, mediumBitrate: 600_000)
        XCTAssertEqual(tuning.targetQuality(bandwidth: 0, latency: nil), 0)
        XCTAssertEqual(tuning.targetQuality(bandwidth: 349_999, latency: 0.1), 0)
        XCTAssertEqual(tuning.targetQuality(bandwidth: 350_000, latency: 0.1), 1)
        XCTAssertEqual(tuning.targetQuality(bandwidth: 900_000, latency: 0.1), 2)
        XCTAssertEqual(tuning.targetQuality(bandwidth: 2_000_000, latency: 0.7), 0)
        XCTAssertEqual(tuning.targetQuality(bandwidth: nil, latency: 0.7), 0)
        XCTAssertNil(tuning.targetQuality(bandwidth: nil, latency: 0.1))
        XCTAssertNil(tuning.targetQuality(bandwidth: .nan, latency: nil))
        XCTAssertNil(tuning.targetQuality(bandwidth: .infinity, latency: nil))
        XCTAssertNil(tuning.targetQuality(bandwidth: -1, latency: nil))
    }

    func testOnlyConfirmedReceiptShowsRingingAndStaleEventsCannotUndoIt() async throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }
        var call = LegendCallSnapshot(id: UUID(), conversationId: UUID(),
            callerUserId: "caller", callerType: "Client", calleeUserId: "callee", calleeType: "Agent",
            callerDeviceId: store.deviceId, calleeDeviceId: nil, callerName: "Caller", calleeName: "Callee",
            video: false, status: "ringing", createdUtc: Date(), expiresUtc: Date().addingTimeInterval(45), epoch: 0)
        let unconfirmed = LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil)
        await store.receive(unconfirmed)
        XCTAssertEqual(store.status, "Calling")
        store.audioActivated(false)
        XCTAssertFalse(store.outgoingToneEligible, "CallKit still owns audio activation")
        store.audioActivated(true)
        XCTAssertTrue(store.outgoingToneEligible, "Local dialing must sound before a remote receipt arrives")
        XCTAssertNil(store.controlError, "The bundled outgoing cue must be accepted by the existing audio player")
        XCTAssertNil(store.current?.receivedUtc)
        XCTAssertEqual(store.status, "Calling", "A dialing cue must not manufacture delivery confirmation")
        call.receivedUtc = Date()
        await store.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
        XCTAssertEqual(store.status, "Ringing")
        await store.receive(unconfirmed)
        XCTAssertEqual(store.status, "Ringing")
        XCTAssertNotNil(store.current?.receivedUtc)
        XCTAssertTrue(store.outgoingToneEligible)
        store.audioActivated(false)
        XCTAssertFalse(store.outgoingToneEligible)
        store.shutdown()
        store.audioActivated(true)
        XCTAssertFalse(store.outgoingToneEligible, "A retired call must not resume the cue")
    }

    func testOutgoingCueWaitsForCallKitStartEvenWhenOldAudioActivationIsRetained() throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }
        store.audioActivated(true)
        XCTAssertFalse(store.outgoingToneEligible)
        store.start(conversationId: UUID(), video: false, recipientName: "Recipient")
        XCTAssertTrue(store.isStarting)
        XCTAssertFalse(store.outgoingToneEligible, "A tap awaiting permissions/CallKit is not an admitted outgoing call")
        store.end()
        XCTAssertFalse(store.outgoingToneEligible)
    }


    func testCancelledAnswerAfterRetirementCannotRestoreCall() async throws {
        try await verifyDelayedAnswer(throwsCancellation: true)
    }

    func testDelayedAnswerCannotResurrectEndedCallOrReplaceNewCall() async throws {
        try await verifyDelayedAnswer(throwsCancellation: false)
    }

    private func verifyDelayedAnswer(throwsCancellation: Bool) async throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client, accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }
        var call = LegendCallSnapshot(id: UUID(), conversationId: UUID(),
            callerUserId: "caller", callerType: "Client", calleeUserId: "callee", calleeType: "Agent",
            callerDeviceId: store.deviceId, calleeDeviceId: nil, callerName: "Caller", calleeName: "Callee",
            video: false, status: "ringing", createdUtc: Date(), expiresUtc: Date().addingTimeInterval(45), epoch: 0)
        await store.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
        let original = call
        var continuation: CheckedContinuation<LegendCallResult, Never>?
        let answer = Task { @MainActor in
            try await store.completeAnswer(callId: original.id) {
                let response = await withCheckedContinuation { continuation = $0 }
                if throwsCancellation { throw CancellationError() }
                return response
            }
        }
        while continuation == nil { await Task.yield() }
        call = LegendCallSnapshot(id: call.id, conversationId: call.conversationId,
            callerUserId: call.callerUserId, callerType: call.callerType,
            calleeUserId: call.calleeUserId, calleeType: call.calleeType,
            callerDeviceId: call.callerDeviceId, calleeDeviceId: call.calleeDeviceId,
            callerName: call.callerName, calleeName: call.calleeName, video: call.video,
            status: "ended", createdUtc: call.createdUtc, expiresUtc: call.expiresUtc, epoch: call.epoch)
        await store.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
        XCTAssertNil(store.current)
        store.start(conversationId: UUID(), video: false, recipientName: "New recipient")
        continuation?.resume(returning: LegendCallResult(succeeded: true, error: nil, call: original, activeCalls: nil, policy: nil))
        do { try await answer.value; XCTFail("An ended call's answer must be rejected") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertNil(store.current)
        XCTAssertTrue(store.isStarting, "The old answer must not clear the new call")
        XCTAssertEqual(store.name, "New recipient")
        await store.receive(LegendCallEvent(call: original, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
        XCTAssertNil(store.current, "Finished call events remain rejected")
    }

    func testCallingSoundsArePackagedAndDecodable() throws {
        for name in ["legend_ringback", "legend_incoming"] {
            let url = try XCTUnwrap(Bundle.main.url(forResource: name, withExtension: "wav"))
            let player = try AVAudioPlayer(contentsOf: url)
            XCTAssertGreaterThan(player.duration, 2)
            XCTAssertEqual(player.numberOfChannels, 1)
        }
    }

    func testReceiptAndTerminalFailureDecodeFromSharedWireContract() throws {
        let json = """
        {"id":"00000000-0000-0000-0000-000000000001","conversationId":"00000000-0000-0000-0000-000000000002",
         "callerUserId":"caller","callerType":"Client","calleeUserId":"callee","calleeType":"Agent",
         "callerDeviceId":"00000000-0000-0000-0000-000000000003","callerName":"Caller","calleeName":"Callee",
         "video":false,"status":"missed","createdUtc":"2026-09-10T08:00:00Z","expiresUtc":"2026-09-10T08:00:45Z",
         "epoch":0,"receivedUtc":"2026-09-10T08:00:01.1234567Z","failureMessage":"The call was not answered."}
        """
        let call = try JSONDecoder.mobile.decode(LegendCallSnapshot.self, from: Data(json.utf8))
        XCTAssertNotNil(call.receivedUtc)
        XCTAssertTrue(call.terminal)
        XCTAssertEqual(call.failureMessage, "The call was not answered.")
    }

    func testOutgoingCallAppearsBeforeAsyncSetupAndCanBeCancelledImmediately() throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client,
            accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        defer { store.shutdown() }

        store.start(conversationId: UUID(), video: false, recipientName: "Alex")
        // No suspension: presentation must be ready before permission/network work.
        XCTAssertTrue(store.isStarting)
        XCTAssertNil(store.current)
        XCTAssertEqual(store.name, "Alex")
        XCTAssertEqual(store.status, "Calling")
        store.start(conversationId: UUID(), video: true, recipientName: "Someone else")
        XCTAssertEqual(store.name, "Alex", "Repeated taps must not replace the pending call")

        store.end()
        XCTAssertFalse(store.isStarting)
        XCTAssertNil(store.current)
        XCTAssertNil(store.failure)
        store.start(conversationId: UUID(), video: true, recipientName: "Taylor")
        XCTAssertTrue(store.isStarting)
        XCTAssertEqual(store.name, "Taylor")
        store.end()
    }

    func testShutdownClearsImmediateOutgoingPresentation() throws {
        let transport = try XCTUnwrap(MobileMessagingRealtimeClient(
            apiBaseURL: URL(string: "https://example.invalid/api/v1/mobile")!,
            participantType: .client,
            accessTokenProvider: { throw CancellationError() }))
        let store = LegendCallStore(transport: transport,
            identity: try LogicalParticipantIdentity(userID: "caller", participantType: .client))
        store.start(conversationId: UUID(), video: true, recipientName: "Alex")
        store.shutdown()
        XCTAssertFalse(store.isStarting)
        store.start(conversationId: UUID(), video: false, recipientName: "Taylor")
        XCTAssertFalse(store.isStarting)
    }

    func testNativePeersConnectWithTrickleCandidatesAndRenegotiate() async throws {
        let policy = LegendCallPolicy(stunUrls: [], wifiWidth: 1280, wifiHeight: 720, wifiFps: 30,
            cellularWidth: 640, cellularHeight: 480, cellularFps: 24, videoBitrate: 1_000_000,
            audioBitrate: 64_000, ringSeconds: 45, connectSeconds: 20, recoveryAttempts: 3)
        let caller = try LegendRTCPeer(policy: policy, video: false, caller: true)
        let callee = try LegendRTCPeer(policy: policy, video: false, caller: false)
        defer { caller.close(); callee.close() }
        var callerConnected = false
        var calleeConnected = false
        var candidateCount = 0
        var epochs = Set<Int>()
        caller.onState = { if $0 == "Connected" { callerConnected = true } }
        callee.onState = { if $0 == "Connected" { calleeConnected = true } }
        caller.onSignal = { kind, data, epoch in
            if kind == "candidate" { candidateCount += 1 }
            if kind == "offer" { epochs.insert(epoch) }
            try await callee.receive(kind: kind, data: data, epoch: epoch)
        }
        callee.onSignal = { kind, data, epoch in try await caller.receive(kind: kind, data: data, epoch: epoch) }
        try await caller.offer()
        for _ in 0..<150 where !callerConnected || !calleeConnected { try await Task.sleep(for: .milliseconds(100)) }
        XCTAssertTrue(callerConnected)
        XCTAssertTrue(calleeConnected)
        XCTAssertGreaterThan(candidateCount, 0)
        try await caller.offer(restart: true)
        XCTAssertEqual(epochs, [1, 2])
        caller.setMuted(true)
        caller.close()
        caller.close() // Closing after a network failure and a UI dismissal is safe.
    }
}

private struct CallingTestTokenStore: SecureTokenStoring {
    func read() throws -> OAuthTokenSet? { nil }
    func save(_ tokens: OAuthTokenSet) throws {}
    func clear() throws {}
}

private struct CallingLegacyWakeTokens: SecureTokenStoring {
    func read() throws -> OAuthTokenSet? {
        OAuthTokenSet(accessToken: "synthetic-call-token", refreshToken: nil, expiresAt: Date().addingTimeInterval(3600))
    }
    func save(_ tokens: OAuthTokenSet) throws {}
    func clear() throws {}
}
private struct CallingWakeTokens: MultiAccountSecureTokenStoring {
    func read() throws -> OAuthTokenSet? { try CallingLegacyWakeTokens().read() }
    func save(_ tokens: OAuthTokenSet) throws {}
    func clear() throws {}
    func signedInAccounts() throws -> [MobileSignedInAccount] {
        [MobileSignedInAccount(id: "callee", displayName: "Recipient", participantType: .client)]
    }
    func selectedAccountID() throws -> String? { "callee" }
    func selectAccount(id: String) throws -> OAuthTokenSet? { try read() }
    func upsert(_ tokens: OAuthTokenSet, for account: MobileSignedInAccount) throws -> MobileSignedInAccount { account }
    func removeAccount(id: String) throws {}
}
private struct CallingLockedLaunchCache: LegendLaunchCaching {
    func readSession() -> MobileSessionCacheEntry? { nil }
    func writeSession(_ entry: MobileSessionCacheEntry) {}
    func readPayload(_ kind: LegendLaunchPayloadKind, actorKey: String) -> Data? { nil }
    func writePayload(_ data: Data, kind: LegendLaunchPayloadKind, actorKey: String) {}
    func readProtectedImage(resourcePath: String) -> Data? { nil }
    func readLastKnownProtectedImage(resourcePath: String) -> Data? { nil }
    func writeProtectedImage(_ data: Data, resourcePath: String) {}
    func clear() {}
}
@MainActor private final class CallingWakeBiometrics: MobileBiometricSessionSecuring {
    var authenticationCount = 0
    private let suspended: Bool
    private var continuation: CheckedContinuation<Bool, Never>?
    init(suspended: Bool = false) { self.suspended = suspended }
    func resume(_ result: Bool) { continuation?.resume(returning: result); continuation = nil }
    var isAvailable: Bool { true }
    func hasPrompted(for identity: LogicalParticipantIdentity) -> Bool { true }
    func markPrompted(for identity: LogicalParticipantIdentity) {}
    func isEnabled(for identity: LogicalParticipantIdentity) -> Bool { true }
    func disable(for identity: LogicalParticipantIdentity) {}
    func enable(for identity: LogicalParticipantIdentity) async -> Bool { false }
    func authenticate() async -> Bool {
        authenticationCount += 1
        if suspended { return await withCheckedContinuation { continuation = $0 } }
        return false
    }
}
private actor CallingWakeService: MobileSessionServicing {
    let actorID: String
    let authenticated: Bool
    let suspended: Bool
    var requests = 0
    private var continuation: CheckedContinuation<Void, Never>?
    init(actorID: String, authenticated: Bool = true, suspended: Bool = false) {
        self.actorID = actorID; self.authenticated = authenticated; self.suspended = suspended
    }
    func resume() { continuation?.resume(); continuation = nil }
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        requests += 1
        if suspended { await withCheckedContinuation { continuation = $0 } }
        return MobileBootstrapResponse(authenticated: authenticated,
            actor: try MobileActor(identity: LogicalParticipantIdentity(userID: actorID, participantType: .client),
                profileID: "00000000-0000-0000-0000-000000000001", displayName: "Recipient", avatar: nil),
            permittedParticipantTypes: [.client], requiresParticipantSelection: false,
            capabilities: MobileCapabilities(messaging: true), correlationID: "call-wake-fixture")
    }
    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        throw CancellationError()
    }
}
