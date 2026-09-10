import SwiftUI
import AVKit
@preconcurrency import WebRTC

// A single account-scoped presentation remains above any existing CRM/chat sheet.
// It owns no call state; all actions go to the same LegendCallStore.
struct LegendCallPresentation: UIViewRepresentable {
    @ObservedObject var store: LegendCallStore
    func makeUIView(context: Context) -> UIView { UIView(frame: .zero) }
    func makeCoordinator() -> Coordinator { Coordinator(store: store) }
    func updateUIView(_ view: UIView, context: Context) {
        DispatchQueue.main.async {
            guard let scene = view.window?.windowScene else { return }
            if store.current != nil || store.failure != nil {
                if context.coordinator.window == nil {
                    let window = UIWindow(windowScene: scene)
                    window.windowLevel = .normal + 2
                    window.rootViewController = UIHostingController(rootView: LegendCallScreen(store: store))
                    context.coordinator.window = window
                }
                let bounds = scene.coordinateSpace.bounds
                context.coordinator.window?.frame = store.minimized
                    ? CGRect(x: 12, y: scene.windows.first(where: { $0 !== context.coordinator.window })?.safeAreaInsets.top ?? 54, width: bounds.width - 24, height: 64)
                    : bounds
                context.coordinator.window?.isHidden = false
            } else { context.coordinator.window?.isHidden = true }
        }
    }
    static func dismantleUIView(_ uiView: UIView, coordinator: Coordinator) {
        coordinator.window?.isHidden = true
        coordinator.window = nil
        coordinator.store.shutdown()
    }
    @MainActor final class Coordinator {
        let store: LegendCallStore
        var window: UIWindow?
        init(store: LegendCallStore) { self.store = store }
    }
}

private struct LegendCallScreen: View {
    @ObservedObject var store: LegendCallStore
    @State private var snapshot: LegendCallImage?
    @State private var snapshotCapture: LegendCallFrameCapture?
    var body: some View {
        if store.minimized {
            HStack {
                Button { store.minimized = false } label: {
                    Label(LegendLocalized("Return to call"), systemImage: "phone.fill")
                }
                Spacer()
                Button(LegendLocalized("Stop sharing")) { store.toggleScreenSharing() }
            }.padding().foregroundStyle(.white).background(LegendNextColor.navy, in: Capsule())
        } else {
        ZStack {
            LinearGradient(colors: [LegendNextColor.navy, LegendNextColor.midnight], startPoint: .topLeading, endPoint: .bottomTrailing).ignoresSafeArea()
            if let remote = store.remoteVideo { LegendRTCVideo(track: remote).ignoresSafeArea() }
            VStack(spacing: 24) {
                Text(LegendLocalized("LEGEND®"))
                    .font(LegendNextTypography.wordmark)
                    .tracking(LegendSharedDesign.tracking("wordmark"))
                    .frame(maxWidth: .infinity)
                if let local = store.localVideo {
                    HStack {
                        Spacer()
                        LegendRTCVideo(track: local).frame(width: 105, height: 145).clipShape(RoundedRectangle(cornerRadius: 22))
                    }
                }
                Spacer()
                if let failure = store.failure {
                    Image(systemName: "phone.down.fill").font(.largeTitle).foregroundStyle(LegendNextColor.gold)
                    Text(LegendLocalized(failure)).multilineTextAlignment(.center)
                    Button(LegendLocalized("Close")) { store.end(); store.dismissFailure() }
                        .buttonStyle(.borderedProminent).tint(LegendNextColor.gold).foregroundStyle(LegendNextColor.midnight)
                } else {
                    Text(store.name).font(.largeTitle.bold()).multilineTextAlignment(.center)
                    Text(statusLabel).font(.headline).foregroundStyle(LegendNextColor.gold)
                    if store.incoming {
                        HStack(spacing: 40) {
                            control(LegendLocalized("Decline"), icon: "phone.down.fill", color: .red) { store.end() }
                            control(LegendLocalized("Answer"), icon: "phone.fill", color: LegendNextColor.gold) { store.answer() }
                        }
                    } else {
                        HStack(spacing: 20) {
                            control(store.muted ? LegendLocalized("Unmute") : LegendLocalized("Mute"), icon: store.muted ? "mic.slash.fill" : "mic.fill") { store.setMuted() }
                            control(LegendLocalized("Speaker"), icon: store.speaker ? "speaker.wave.3.fill" : "ear.fill") { store.toggleSpeaker() }
                            if store.current?.video == true && !store.sharingScreen {
                                control(LegendLocalized("Camera"), icon: store.cameraEnabled ? "video.fill" : "video.slash.fill") { store.toggleCamera() }
                                control(LegendLocalized("Flip"), icon: "arrow.triangle.2.circlepath.camera") { store.switchCamera() }
                            }
                        }
                        if let remote = store.remoteVideo, store.status == "Connected" {
                            Button {
                                snapshotCapture?.cancel()
                                let capture = LegendCallFrameCapture(track: remote) { image in
                                    guard store.current != nil else { return }
                                    snapshotCapture = nil
                                    if let image { snapshot = LegendCallImage(image: image) }
                                    else { store.controlError = LegendLocalized("A video frame is not available yet. Please try again.") }
                                }
                                snapshotCapture = capture
                                capture.start()
                            } label: { Label(LegendLocalized("Take snapshot"), systemImage: "camera") }
                        }
                        if store.current?.video == true && store.status == "Connected" {
                            Button { store.toggleScreenSharing() } label: {
                                Label(store.sharingScreen ? LegendLocalized("Stop sharing") : LegendLocalized("Share Legend® screen"), systemImage: "rectangle.on.rectangle")
                            }.buttonStyle(.bordered)
                        }
                        control(LegendLocalized("End call"), icon: "phone.down.fill", color: .red) { store.end() }
                    }
                }
                Spacer().frame(height: 28)
            }.padding(24)
        }.foregroundStyle(.white)
        .sheet(item: $snapshot) { item in LegendCallImageShare(image: item.image) }
        .onDisappear { snapshotCapture?.cancel(); snapshotCapture = nil }
        .alert(LegendLocalized("Call controls"), isPresented: Binding(get: { store.controlError != nil }, set: { if !$0 { store.controlError = nil } })) {
            Button(LegendLocalized("OK")) { store.controlError = nil }
        } message: { Text(store.controlError ?? "") }
        }
    }
    private var statusLabel: String {
        switch store.status {
        case "Calling": return LegendLocalized("Calling")
        case "Incoming call": return LegendLocalized("Incoming call")
        case "Connecting": return LegendLocalized("Connecting")
        case "Connected": return LegendLocalized("Connected")
        case "Reconnecting": return LegendLocalized("Reconnecting")
        case "Audio interrupted": return LegendLocalized("Audio interrupted")
        default: return LegendLocalized(store.status)
        }
    }
    private func control(_ title: String, icon: String, color: Color = .white.opacity(0.16), action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 8) {
                Image(systemName: icon).font(.title2).frame(width: 54, height: 54).background(color, in: Circle())
                Text(title).font(.caption)
            }
        }.buttonStyle(.plain).accessibilityLabel(title)
    }
}
private struct LegendRTCVideo: UIViewRepresentable {
    let track: RTCVideoTrack
    func makeUIView(context: Context) -> RTCMTLVideoView {
        let view = RTCMTLVideoView(frame: .zero)
        view.videoContentMode = .scaleAspectFill
        view.clipsToBounds = true
        track.add(view)
        context.coordinator.track = track
        return view
    }
    func updateUIView(_ uiView: RTCMTLVideoView, context: Context) {
        if context.coordinator.track !== track {
            context.coordinator.track?.remove(uiView); track.add(uiView); context.coordinator.track = track
        }
    }
    func makeCoordinator() -> Coordinator { Coordinator() }
    static func dismantleUIView(_ uiView: RTCMTLVideoView, coordinator: Coordinator) { coordinator.track?.remove(uiView) }
    final class Coordinator { var track: RTCVideoTrack? }
}

private struct LegendCallImage: Identifiable { let id = UUID(); let image: UIImage }
private struct LegendCallImageShare: UIViewControllerRepresentable {
    let image: UIImage
    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [image], applicationActivities: nil)
    }
    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}

/// Capture one decoded frame on demand; never record or retain the call stream.
private final class LegendCallFrameCapture: NSObject, RTCVideoRenderer, @unchecked Sendable {
    private let track: RTCVideoTrack
    private let completion: @MainActor (UIImage?) -> Void
    private let lock = NSLock()
    private var consumed = false
    init(track: RTCVideoTrack, completion: @escaping @MainActor (UIImage?) -> Void) {
        self.track = track; self.completion = completion
    }
    @MainActor func start() {
        track.add(self)
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.finish(nil) }
    }
    func setSize(_ size: CGSize) {}
    func renderFrame(_ frame: RTCVideoFrame?) {
        guard let frame else { return }
        lock.lock(); let alreadyConsumed = consumed; consumed = true; lock.unlock()
        guard !alreadyConsumed else { return }
        let buffer = frame.buffer.toI420()
        let width = Int(buffer.width), height = Int(buffer.height)
        var output: CVPixelBuffer?
        guard CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &output) == kCVReturnSuccess,
              let output else { deliver(nil); return }
        CVPixelBufferLockBaseAddress(output, [])
        let y = CVPixelBufferGetBaseAddressOfPlane(output, 0)!.assumingMemoryBound(to: UInt8.self)
        let uv = CVPixelBufferGetBaseAddressOfPlane(output, 1)!.assumingMemoryBound(to: UInt8.self)
        for row in 0..<height {
            memcpy(y + row * CVPixelBufferGetBytesPerRowOfPlane(output, 0), buffer.dataY + row * Int(buffer.strideY), width)
        }
        for row in 0..<Int(buffer.chromaHeight) {
            for column in 0..<Int(buffer.chromaWidth) {
                let offset = row * CVPixelBufferGetBytesPerRowOfPlane(output, 1) + column * 2
                uv[offset] = buffer.dataU[row * Int(buffer.strideU) + column]
                uv[offset + 1] = buffer.dataV[row * Int(buffer.strideV) + column]
            }
        }
        CVPixelBufferUnlockBaseAddress(output, [])
        let orientation: CGImagePropertyOrientation = switch frame.rotation {
        case ._90: .right
        case ._180: .down
        case ._270: .left
        default: .up
        }
        let image = CIImage(cvPixelBuffer: output).oriented(orientation)
        let rendered = CIContext().createCGImage(image, from: image.extent).map { UIImage(cgImage: $0) }
        deliver(rendered)
    }
    @MainActor func cancel() { lock.lock(); consumed = true; lock.unlock(); track.remove(self) }
    private func finish(_ image: UIImage?) {
        lock.lock(); let alreadyConsumed = consumed; consumed = true; lock.unlock()
        if !alreadyConsumed { deliver(image) }
    }
    private func deliver(_ image: UIImage?) {
        Task { @MainActor in track.remove(self); completion(image) }
    }
}
