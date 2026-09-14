import Foundation
import Network
import CoreImage
import ImageIO
import ReplayKit
import SwiftUI

// An out-of-process capture adapter for the existing RTC video source. It owns no call signaling.
@MainActor final class LegendCallBroadcastReceiver {
    private let rootResolver: () throws -> URL
    init(rootResolver: @escaping () throws -> URL = LegendBroadcastProtocol.root) { self.rootResolver = rootResolver }
    private let queue = DispatchQueue(label: "com.mylegnd.call-broadcast-receiver")
    private let context = CIContext(options: [.cacheIntermediates: false])
    private var listener: NWListener?
    private var connection: NWConnection?
    private var invitation: LegendBroadcastProtocol.Invitation?
    private var root: URL?
    private var generation = UUID()
    private var lastTimestamp: Int64 = -1
    private var prepared: CheckedContinuation<Void, Error>?
    var onFrame: ((CVPixelBuffer, Int64) -> Void)?
    var onStopped: (() -> Void)?

    func prepare(width: Int, height: Int, fps: Int) async throws {
        stop(notify: false)
        let root = try rootResolver()
        let invitation = LegendBroadcastProtocol.Invitation(nonce: UUID().uuidString.replacingOccurrences(of: "-", with: "") + UUID().uuidString.replacingOccurrences(of: "-", with: ""),
            socketName: "call.sock", expiresUtc: Date().addingTimeInterval(120),
            width: min(1920, max(2, width)), height: min(1920, max(2, height)), fps: min(15, max(1, fps)))
        guard let socket = invitation.validated(root: root) else { throw LegendCallingError.unavailable("Screen sharing is unavailable in this app installation.") }
        try? FileManager.default.removeItem(at: socket)
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .unix(path: socket.path)
        let listener = try NWListener(using: parameters)
        self.root = root; self.invitation = invitation; self.listener = listener
        let generation = self.generation
        listener.newConnectionHandler = { [weak self] connection in
            Task { @MainActor in
                guard let self, self.generation == generation, self.connection == nil,
                      Date() < invitation.expiresUtc else { connection.cancel(); return }
                self.connection = connection
                connection.start(queue: self.queue)
                Task { [weak self, weak connection] in
                    try? await Task.sleep(for: .seconds(5))
                    guard let self, let connection, self.generation == generation,
                          self.connection === connection, self.invitation != nil else { return }
                    connection.cancel(); self.connection = nil
                }
                LegendBroadcastProtocol.readExactly(64, from: connection) { [weak self] data in
                    Task { @MainActor in
                        guard let self, self.generation == generation else { return }
                        guard self.connection === connection else { return }
                        guard data == Data(invitation.nonce.utf8) else {
                            connection.cancel(); self.connection = nil; return
                        }
                        self.invitation = nil
                        // A consumed invitation cannot be reused by a later broadcast.
                        try? FileManager.default.removeItem(at: LegendBroadcastProtocol.invitationURL(root))
                        self.readFrame(connection, generation: generation)
                    }
                }
            }
        }
        listener.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                guard let self, self.generation == generation else { return }
                if case .ready = state {
                    do {
                        try JSONEncoder().encode(invitation).write(to: LegendBroadcastProtocol.invitationURL(root), options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
                        let continuation = self.prepared; self.prepared = nil
                        continuation?.resume()
                    } catch { self.stop() }
                } else if case .failed = state { self.stop() }
            }
        }
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(120))
            guard let self, self.generation == generation, self.lastTimestamp < 0 else { return }
            self.stop()
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                prepared = continuation
                listener.start(queue: queue)
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(5))
                    guard let self, self.generation == generation, self.prepared != nil else { return }
                    self.stop()
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                if self?.generation == generation { self?.stop() }
            }
        }
    }
    private func readFrame(_ connection: NWConnection, generation: UUID) {
        LegendBroadcastProtocol.readExactly(12, from: connection) { [weak self] bytes in
            guard let bytes, let header = LegendBroadcastProtocol.decodeHeader(bytes) else {
                Task { @MainActor in if self?.generation == generation { self?.stop() } }; return
            }
            LegendBroadcastProtocol.readExactly(header.size, from: connection) { [weak self] data in
                Task { @MainActor in
                    guard let self, self.generation == generation else { return }
                    guard let data, header.timestamp > self.lastTimestamp,
                          let source = CGImageSourceCreateWithData(data as CFData, nil),
                          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                          let width = properties[kCGImagePropertyPixelWidth] as? Int,
                          let height = properties[kCGImagePropertyPixelHeight] as? Int,
                          (1...1920).contains(width), (1...1920).contains(height),
                          let image = CIImage(data: data) else { self.stop(); return }
                    var pixels: CVPixelBuffer?
                    let result = CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                        [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &pixels)
                    guard result == kCVReturnSuccess, let pixels else { self.stop(); return }
                    self.context.render(image, to: pixels)
                    self.lastTimestamp = header.timestamp
                    self.onFrame?(pixels, header.timestamp)
                    connection.send(content: Data([1]), completion: .contentProcessed { _ in })
                    self.readFrame(connection, generation: generation)
                }
            }
        }
    }
    func stop(notify: Bool = true) {
        generation = UUID(); lastTimestamp = -1
        let continuation = prepared; prepared = nil
        continuation?.resume(throwing: CancellationError())
        connection?.cancel(); connection = nil
        listener?.cancel(); listener = nil
        if let root {
            try? FileManager.default.removeItem(at: LegendBroadcastProtocol.invitationURL(root))
            try? FileManager.default.removeItem(at: root.appendingPathComponent("call.sock"))
        }
        root = nil; invitation = nil
        if notify { onStopped?() }
    }
}

struct LegendSystemBroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 60, height: 60))
        picker.preferredExtension = LegendBroadcastProtocol.extensionID
        picker.showsMicrophoneButton = false // The existing call owns microphone capture.
        return picker
    }
    func updateUIView(_ view: RPSystemBroadcastPickerView, context: Context) {}
}
