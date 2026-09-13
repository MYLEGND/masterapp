import ReplayKit
import Network
import CoreImage
import ImageIO

final class SampleHandler: RPBroadcastSampleHandler {
    private let queue = DispatchQueue(label: "com.mylegnd.call-broadcast")
    private let context = CIContext(options: [.cacheIntermediates: false])
    private var connection: NWConnection?
    private var invitation: LegendBroadcastProtocol.Invitation?
    private var stopped = false
    private var ready = false
    private var sending = false
    private var lastFrame = 0.0

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        queue.async { [weak self] in
            guard let self else { return }
            do {
                let root = try LegendBroadcastProtocol.root()
                let url = LegendBroadcastProtocol.invitationURL(root)
                let values = try url.resourceValues(forKeys: [.fileSizeKey])
                guard (values.fileSize ?? 0) <= 4096 else { throw self.failure() }
                let invitation = try JSONDecoder().decode(LegendBroadcastProtocol.Invitation.self, from: Data(contentsOf: url))
                guard let socket = invitation.validated(root: root) else { throw self.failure() }
                self.invitation = invitation
                let connection = NWConnection(to: .unix(path: socket.path), using: .tcp)
                self.connection = connection
                connection.stateUpdateHandler = { [weak self, weak connection] state in
                    guard let self, let connection else { return }
                    switch state {
                    case .ready:
                        connection.send(content: Data(invitation.nonce.utf8), completion: .contentProcessed { [weak self] error in
                            guard let self else { return }
                            if error == nil { self.ready = true; self.receiveAcknowledgement(connection) }
                            else { self.stopWithFailure() }
                        })
                    case .failed: self.stopWithFailure()
                    default: break
                    }
                }
                connection.start(queue: self.queue)
                self.queue.asyncAfter(deadline: .now() + 10) { [weak self] in
                    if let self, !self.ready { self.stopWithFailure() }
                }
            } catch { self.stopWithFailure() }
        }
    }
    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        // Synchronous admission means only one retained sample/encoded frame can exist.
        guard sampleBufferType == .video else { return } // Existing call RTC owns microphone audio.
        let timestamp = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        let accepted: Bool = queue.sync {
            guard ready, !sending, let invitation, timestamp.isFinite,
                  timestamp - lastFrame >= 1.0 / Double(invitation.fps) else { return false }
            sending = true; lastFrame = timestamp; return true
        }
        guard accepted else { return }
        // ReplayKit owns the sample only until this callback returns. Encode here;
        // the asynchronous network send retains only our bounded Data copy.
        queue.sync { [weak self] in
            guard let self else { return }
            guard let connection = self.connection, let invitation = self.invitation,
                  let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { self.sending = false; return }
            autoreleasepool {
                let orientation = (CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString,
                    attachmentModeOut: nil) as? NSNumber)?.uint32Value ?? 1
                let original = CIImage(cvPixelBuffer: pixels).oriented(CGImagePropertyOrientation(rawValue: orientation) ?? .up)
                let maxLong = Double(max(invitation.width, invitation.height))
                let maxShort = Double(min(invitation.width, invitation.height))
                let scale = min(1, min(maxLong / max(original.extent.width, original.extent.height),
                                       maxShort / min(original.extent.width, original.extent.height)))
                let image = original.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
                guard let bytes = self.context.jpegRepresentation(of: image, colorSpace: CGColorSpaceCreateDeviceRGB(),
                    options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.65]),
                      bytes.count <= LegendBroadcastProtocol.maximumFrameBytes else {
                    self.sending = false; return
                }
                var frame = LegendBroadcastProtocol.encodeHeader(size: bytes.count, timestamp: Int64(timestamp * 1_000_000_000))
                frame.append(bytes)
                connection.send(content: frame, completion: .contentProcessed { [weak self] error in
                    guard let self else { return }
                    if error != nil { self.stopWithFailure() }
                    // Keep admission closed until the RTC receiver acknowledges this frame.
                })
            }
        }
    }
    override func broadcastPaused() { queue.async { [weak self] in self?.ready = false } }
    override func broadcastResumed() { queue.async { [weak self] in self?.ready = self?.connection != nil } }
    override func broadcastFinished() { queue.async { [weak self] in self?.ready = false; self?.connection?.cancel(); self?.connection = nil } }
    private func receiveAcknowledgement(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [weak self] data, _, _, error in
            guard let self else { return }
            guard error == nil, data == Data([1]), self.connection === connection else { self.stopWithFailure(); return }
            self.sending = false
            self.receiveAcknowledgement(connection)
        }
    }
    private func failure() -> NSError {
        NSError(domain: "LegendBroadcast", code: 2, userInfo: [NSLocalizedDescriptionKey: "The LEGEND call is no longer available for screen sharing."])
    }
    private func stopWithFailure() {
        guard !stopped else { return }; stopped = true
        ready = false; connection?.cancel(); connection = nil
        finishBroadcastWithError(failure())
    }
}
