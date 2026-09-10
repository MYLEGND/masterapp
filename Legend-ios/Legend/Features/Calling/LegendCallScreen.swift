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
    var body: some View {
        ZStack {
            LinearGradient(colors: [LegendNextColor.navy, LegendNextColor.midnight], startPoint: .topLeading, endPoint: .bottomTrailing).ignoresSafeArea()
            if let remote = store.remoteVideo { LegendRTCVideo(track: remote).ignoresSafeArea() }
            VStack(spacing: 24) {
                HStack {
                    Text(LegendLocalized("LEGEND®")).font(.headline).tracking(4)
                    Spacer()
                    if let local = store.localVideo {
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
                            if store.current?.video == true {
                                control(LegendLocalized("Camera"), icon: store.cameraEnabled ? "video.fill" : "video.slash.fill") { store.toggleCamera() }
                                control(LegendLocalized("Flip"), icon: "arrow.triangle.2.circlepath.camera") { store.switchCamera() }
                            }
                        }
                        control(LegendLocalized("End call"), icon: "phone.down.fill", color: .red) { store.end() }
                    }
                }
                Spacer().frame(height: 28)
            }.padding(24)
        }.foregroundStyle(.white)
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
