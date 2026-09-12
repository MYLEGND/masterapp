import XCTest
import AVFoundation
@testable import Legend

@MainActor
final class LegendDirectCallingTests: XCTestCase {
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
        call.receivedUtc = Date()
        await store.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
        XCTAssertEqual(store.status, "Ringing")
        await store.receive(unconfirmed)
        XCTAssertEqual(store.status, "Ringing")
        XCTAssertNotNil(store.current?.receivedUtc)
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
