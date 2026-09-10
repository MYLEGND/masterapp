import XCTest
@testable import Legend

@MainActor
final class LegendDirectCallingTests: XCTestCase {
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
