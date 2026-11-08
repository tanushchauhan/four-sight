import MetalKit
import SwiftUI
import UIKit
import CoreMotion

struct VisorView: View {
    @StateObject private var controller = ARSessionController()
    @AppStorage("cameraURL") private var address = "http://prada.local:8080/stream"
    @State private var settings = true
    @StateObject private var steering = HeadSteering()
    @State private var token = ""
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        MetalVisor(session: controller)
            .ignoresSafeArea()
            .overlay(alignment: .topLeading) {
                VStack(alignment: .leading) {
                    Button(settings ? "Hide controls" : "Camera settings") { settings.toggle() }
                    if settings {
                        TextField("Camera URL", text: $address)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                            .textFieldStyle(.roundedBorder).frame(width: 360)
                        Button("Connect") { steering.stop(); controller.start(address) }
                        SecureField("Robot control token", text: $token)
                            .textFieldStyle(.roundedBorder).frame(width: 360)
                        Button(steering.active ? "Stop head steering" : "Enable head steering") {
                            if steering.active { steering.stop() }
                            else { steering.start(address: address, token: token, video: controller) }
                        }
                        Button("Recenter head") { steering.recenter() }
                        Toggle("Reverse steering", isOn: $steering.reverse).frame(width: 220)
                        Text(steering.status).foregroundStyle(.white)
                    }
                    if settings || controller.status != "Live" { Text(controller.status).foregroundStyle(.white) }
                }.padding().background(.black.opacity(0.7))
            }
            .onChange(of: scenePhase) { phase in
                if phase != .active { steering.stop() }
            }
            .onChange(of: controller.status) { status in
                if status != "Live" { steering.stop() }
            }
            .statusBarHidden(true)
            .persistentSystemOverlays(.hidden)
            .onAppear {
                UIApplication.shared.isIdleTimerDisabled = true
                controller.start(address)
            }
            .onDisappear {
                steering.stop()
                controller.pause()
                UIApplication.shared.isIdleTimerDisabled = false
            }
    }
}

private struct MetalVisor: UIViewRepresentable {
    let session: ARSessionController

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeUIView(context: Context) -> MTKView {
        let view = MTKView(frame: .zero, device: MTLCreateSystemDefaultDevice())
        view.isOpaque = true
        view.backgroundColor = .black
        if let renderer = StereoRenderer(view: view, session: session) {
            context.coordinator.renderer = renderer
            view.delegate = renderer
        }
        return view
    }

    func updateUIView(_ uiView: MTKView, context: Context) {}

    final class Coordinator {
        var renderer: StereoRenderer?
    }
}


@MainActor
final class HeadSteering: ObservableObject {
    @Published var active = false
    @Published var status = "Head steering off"
    @Published var reverse = false
    private let motion = CMMotionManager()
    private var socket: URLSessionWebSocketTask?
    private var loop: Task<Void, Never>?
    private var origin: Double?
    private var filtered = 0.0

    func recenter() { origin = nil; filtered = 0 }

    func start(address: String, token: String, video: ARSessionController) {
        stop()
        guard motion.isDeviceMotionAvailable, token.count >= 16,
              var url = URLComponents(string: address), url.host != nil else {
            status = "Motion unavailable or control token missing"; return
        }
        url.scheme = "ws"; url.port = 8766; url.path = "/"; url.query = nil
        guard let endpoint = url.url else { return }
        let ws = URLSession.shared.webSocketTask(with: endpoint)
        socket = ws; ws.resume()
        motion.deviceMotionUpdateInterval = 1.0 / 30
        motion.startDeviceMotionUpdates(using: .xArbitraryZVertical)
        active = true; status = "Connecting steering…"; recenter()
        loop = Task { [weak self] in
            guard let self else { return }
            do {
                let hello = try JSONSerialization.data(withJSONObject: ["token": token])
                try await ws.send(.string(String(decoding: hello, as: UTF8.self)))
                _ = try await ws.receive()
                status = "Ready — click joystick to enable; center stick for head steering"
                while !Task.isCancelled {
                    if Date().timeIntervalSince(video.lastFrameTime) > 1 {
                        stop(); status = "Video is stale — reconnect video, then enable steering"; return
                    }
                    guard let sample = motion.deviceMotion,
                          ProcessInfo.processInfo.systemUptime - sample.timestamp < 0.3 else {
                        try await Task.sleep(nanoseconds: 50_000_000)
                        continue
                    }
                    // In landscape, the camera/display normal is device Z. Project it onto
                    // the gravity-horizontal reference plane; this avoids Euler-yaw roll coupling.
                    let r = sample.attitude.rotationMatrix
                    let x = -r.m31, y = -r.m32
                    var turn = 0.0
                    if hypot(x, y) > 0.3 {
                        let heading = atan2(y, x)
                        if origin == nil { origin = heading }
                        let delta = atan2(sin(heading - origin!), cos(heading - origin!)) * 180 / .pi
                        filtered += 0.3 * (delta - filtered)
                        if abs(filtered) > 12 {
                            turn = min(0.4, 0.18 + (abs(filtered) - 12) / 100) * (filtered > 0 ? 1 : -1)
                            if reverse { turn = -turn }
                        }
                    }
                    try await ws.send(.string("{\"turn\":\(turn)}"))
                    _ = try await ws.receive()
                    try await Task.sleep(nanoseconds: 50_000_000)
                }
            } catch {
                if !Task.isCancelled { stop(); status = "Steering disconnected — enable again" }
            }
        }
    }

    func stop() {
        loop?.cancel(); loop = nil
        socket?.cancel(with: .goingAway, reason: nil); socket = nil
        motion.stopDeviceMotionUpdates()
        active = false; origin = nil; filtered = 0
        status = "Head steering off"
    }
}
