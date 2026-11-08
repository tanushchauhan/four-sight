import CoreMotion
import AVFoundation
import MetalKit
import SwiftUI
import UIKit

/// Everything worn is drawn inside the lenses by `StereoRenderer`. The camera address is the only
/// flat control: it shows while the camera is not live, folds away once it is, and a tap brings it back.
struct VisorView: View {
    @StateObject private var controller = ARSessionController()
    @StateObject private var steering = HeadSteering()
    @StateObject private var survivorDetector = SurvivorDetector()
    @StateObject private var survivorFeedback = SurvivorFeedback()
    @AppStorage("cameraURL") private var address = "http://prada.local:8080/stream"
    @AppStorage("cameraSource") private var sourceName = CameraSource.robot.rawValue
    @State private var phone = PhoneCamera()
    @State private var settings = true
    @State private var layerStatus = "Lens layer: drawing"
    @FocusState private var focused: Control?
    @Environment(\.scenePhase) private var scenePhase

    private enum Control: Hashable {
        case address
        case connect
        case wear
        case robot
        case phone
        case recenter
    }

    private var source: CameraSource { CameraSource(rawValue: sourceName) ?? .robot }

    private var briefing: VisorBriefing {
        var briefing = VisorBriefing.sample
        switch survivorDetector.state {
        case .loading:
            briefing.survivors = .loading
        case .ready(let count, let confidence):
            briefing.survivors = count == 0
                ? .empty(value: "0", detail: "Area clear")
                : .ready(value: "\(count)", detail: String(format: "Survivor alert · %.0f%%", confidence * 100))
        case .failed:
            briefing.survivors = .failed(value: "Offline", detail: "Detection unavailable")
        }
        if source == .phone {
            briefing.camera = .ready(value: "Phone", detail: "")
            return briefing
        }
        switch controller.status {
        case "Live": briefing.camera = .ready(value: "Robot live", detail: "")
        case "Connecting…", "Disconnected": briefing.camera = .loading
        case "Feed disconnected; retrying…": briefing.camera = .failed(value: "Retrying", detail: "")
        default: briefing.camera = .failed(value: "No camera", detail: "")
        }
        return briefing
    }

    var body: some View {
        ZStack {
            MetalVisor(session: controller, phone: phone, detector: survivorDetector, source: source, briefing: briefing, layerStatus: $layerStatus)
                .ignoresSafeArea()
                .contentShape(Rectangle())
                .onTapGesture { settings.toggle() }
            if settings {
                titleBlock
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(VisorToken.paper3.opacity(0.6).color)
                    .ignoresSafeArea()
            }
        }
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
        .onAppear {
            UIApplication.shared.isIdleTimerDisabled = true
            apply(source)
        }
        .onDisappear {
            steering.stop()
            controller.pause()
            phone.pause()
            UIApplication.shared.isIdleTimerDisabled = false
        }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active {
                steering.stop()
            } else if source == .robot, controller.status == "Live" {
                steering.start(address: address, video: controller)
            }
        }
        .onChange(of: controller.status) { _, status in
            if source == .robot, status == "Live" {
                if focused == nil { settings = false }
                steering.start(address: address, video: controller)
            } else {
                steering.stop()
            }
        }
        .onChange(of: survivorDetector.state) { oldState, newState in
            survivorFeedback.update(from: oldState, to: newState)
        }
    }

    private func apply(_ source: CameraSource) {
        sourceName = source.rawValue
        steering.stop()
        switch source {
        case .robot:
            phone.pause()
            controller.start(address)
        case .phone:
            controller.pause()
            phone.start()
        }
    }

    /// A drawing title block: keylined cells, each label at the cell's top left, the action at its foot.
    private var titleBlock: some View {
        VStack(alignment: .leading, spacing: 0) {
            cell {
                Text("Visor")
                    .font(VisorToken.display(28))
                    .textCase(.uppercase)
                    .foregroundStyle(VisorToken.ink.color)
                Text(layerStatus)
                    .font(VisorToken.body(16))
                    .foregroundStyle(VisorToken.ink2.color)
            }
            rule
            cell {
                Text("Camera")
                    .font(VisorToken.label(14))
                    .foregroundStyle(VisorToken.muted.color)
                HStack(spacing: 0) {
                    Button("Robot feed") { apply(.robot) }
                        .buttonStyle(SheetButtonStyle(primary: source == .robot, focused: focused == .robot))
                        .focused($focused, equals: .robot)
                        .accessibilityAddTraits(source == .robot ? .isSelected : [])
                    Button("Phone camera") { apply(.phone) }
                        .buttonStyle(SheetButtonStyle(primary: source == .phone, focused: focused == .phone))
                        .focused($focused, equals: .phone)
                        .accessibilityAddTraits(source == .phone ? .isSelected : [])
                }
            }
            if source == .robot {
                rule
                cell {
                    Text("Camera URL")
                        .font(VisorToken.label(14))
                        .foregroundStyle(VisorToken.muted.color)
                    TextField("http://prada.local:8080/stream", text: $address)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                        .textContentType(.URL)
                        .submitLabel(.go)
                        .font(VisorToken.measured(16))
                        .foregroundStyle(VisorToken.ink.color)
                        .tint(VisorToken.ink.color)
                        .padding(.horizontal, VisorToken.spaceSm)
                        .frame(height: VisorToken.controlHeight)
                        .background(VisorToken.paper2.color)
                        .overlay(Rectangle().strokeBorder(VisorToken.rule.color, lineWidth: VisorToken.hairline))
                        .overlay(focusRing(focused == .address))
                        .focused($focused, equals: .address)
                        .onSubmit { steering.stop(); controller.start(address) }
                    Text(controller.status)
                        .font(VisorToken.body(16))
                        .foregroundStyle(VisorToken.ink2.color)
                        .frame(minHeight: 22, alignment: .leading)
                    Text(steering.status)
                        .font(VisorToken.body(16))
                        .foregroundStyle(VisorToken.ink2.color)
                        .frame(minHeight: 22, alignment: .leading)
                    Toggle("Reverse steering", isOn: $steering.reverse)
                        .font(VisorToken.label(16))
                        .foregroundStyle(VisorToken.ink.color)
                        .tint(VisorToken.ink.color)
                }
            }
            rule
            HStack(spacing: VisorToken.spaceSm) {
                if source == .robot {
                    Button(controller.status == "Connecting…" ? "Connecting" : "Connect") {
                        focused = nil
                        steering.stop()
                        controller.start(address)
                    }
                    .buttonStyle(SheetButtonStyle(primary: true, focused: focused == .connect))
                    .focused($focused, equals: .connect)
                    .disabled(controller.status == "Connecting…")
                    Button("Recenter head") { steering.recenter() }
                        .buttonStyle(SheetButtonStyle(primary: false, focused: focused == .recenter))
                        .focused($focused, equals: .recenter)
                }
                Button("Wear it") {
                    focused = nil
                    settings = false
                }
                .buttonStyle(SheetButtonStyle(primary: false, focused: focused == .wear))
                .focused($focused, equals: .wear)
            }
            .padding(VisorToken.spaceMd)
        }
        .frame(width: 420)
        .background(VisorToken.paper.color)
        .overlay(Rectangle().strokeBorder(VisorToken.rule.color, lineWidth: VisorToken.hairline))
    }

    private var rule: some View {
        Rectangle().fill(VisorToken.rule.color).frame(height: VisorToken.hairline)
    }

    private func cell<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: VisorToken.spaceXs) { content() }
            .padding(VisorToken.spaceMd)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func focusRing(_ on: Bool) -> some View {
        Rectangle()
            .strokeBorder(VisorToken.focus.color, lineWidth: 2)
            .padding(-4)
            .opacity(on ? 1 : 0)
            .animation(nil, value: on)
    }
}

private struct SheetButtonStyle: ButtonStyle {
    var primary: Bool
    var focused: Bool

    func makeBody(configuration: Configuration) -> some View {
        SheetButton(configuration: configuration, primary: primary, focused: focused)
    }
}

private struct SheetButton: View {
    let configuration: SheetButtonStyle.Configuration
    let primary: Bool
    let focused: Bool
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        configuration.label
            .font(VisorToken.label(16))
            .foregroundStyle(primary ? VisorToken.accentInk.color : VisorToken.ink.color)
            .padding(.horizontal, VisorToken.spaceLg)
            .frame(minHeight: VisorToken.controlHeight)
            .background(background)
            .overlay(Rectangle().strokeBorder(VisorToken.rule.color, lineWidth: VisorToken.hairline))
            .overlay(
                Rectangle()
                    .strokeBorder(VisorToken.focus.color, lineWidth: 2)
                    .padding(-4)
                    .opacity(focused ? 1 : 0)
            )
            .opacity(enabled ? 1 : 0.55)
            .transformEffect(CGAffineTransform(translationX: 0, y: configuration.isPressed ? 1 : 0))
            .contentShape(Rectangle())
            .animation(nil, value: focused)
    }

    private var background: Color {
        if primary { return configuration.isPressed ? VisorToken.ink2.color : VisorToken.ink.color }
        return configuration.isPressed ? VisorToken.paper3.color : Color.clear
    }
}

private struct MetalVisor: UIViewRepresentable {
    let session: ARSessionController
    let phone: PhoneCamera
    let detector: SurvivorDetector
    let source: CameraSource
    let briefing: VisorBriefing
    @Binding var layerStatus: String

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> MTKView {
        let view = MTKView(frame: .zero, device: MTLCreateSystemDefaultDevice())
        view.isOpaque = true
        view.backgroundColor = VisorToken.paper3.uiColor
        if let renderer = StereoRenderer(view: view, session: session, phone: phone, detector: detector) {
            renderer.source = source
            context.coordinator.renderer = renderer
            view.delegate = renderer
            draw(context)
        } else {
            report("Lens layer: Metal renderer failed to start")
        }
        return view
    }

    func updateUIView(_ uiView: MTKView, context: Context) {
        context.coordinator.renderer?.source = source
        if context.coordinator.drawn != briefing { draw(context) }
    }

    private func draw(_ context: Context) {
        guard let renderer = context.coordinator.renderer else { return }
        context.coordinator.drawn = briefing
        guard let layer = LensLayer.render(briefing) else {
            report("Lens layer: SwiftUI could not render it")
            return
        }
        report(renderer.setPlate(layer)
            ? "Lens layer: ready, \(layer.width) by \(layer.height) px"
            : "Lens layer: upload to Metal failed")
    }

    private func report(_ text: String) {
        DispatchQueue.main.async { layerStatus = text }
    }

    final class Coordinator {
        var renderer: StereoRenderer?
        var drawn: VisorBriefing?
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
    private var reconnect: Task<Void, Never>?
    private var origin: Double?
    private var filtered = 0.0

    func recenter() { origin = nil; filtered = 0 }

    func start(address: String, video: ARSessionController) {
        guard !active else { return }
        stop()
        guard motion.isDeviceMotionAvailable,
              var url = URLComponents(string: address), url.host != nil else {
            status = "Motion unavailable"; return
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
                status = "Steering ready — click joystick to enable"
                while !Task.isCancelled {
                    if Date().timeIntervalSince(video.lastFrameTime) > 1 {
                        scheduleReconnect(address: address, video: video, status: "Waiting for live video")
                        return
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
                        if abs(filtered) > 18 {
                            turn = min(0.4, 0.18 + (abs(filtered) - 18) / 100) * (filtered > 0 ? 1 : -1)
                            if reverse { turn = -turn }
                        }
                    }
                    try await ws.send(.string("{\"turn\":\(turn)}"))
                    _ = try await ws.receive()
                    try await Task.sleep(nanoseconds: 50_000_000)
                }
            } catch {
                if !Task.isCancelled {
                    scheduleReconnect(address: address, video: video, status: "Steering reconnecting…")
                }
            }
        }
    }

    private func scheduleReconnect(address: String, video: ARSessionController, status: String) {
        loop?.cancel(); loop = nil
        socket?.cancel(with: .goingAway, reason: nil); socket = nil
        motion.stopDeviceMotionUpdates()
        active = false; origin = nil; filtered = 0
        self.status = status
        reconnect?.cancel()
        reconnect = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                guard !Task.isCancelled else { return }
                if Date().timeIntervalSince(video.lastFrameTime) <= 1 {
                    self?.start(address: address, video: video)
                    return
                }
            }
        }
    }

    func stop() {
        reconnect?.cancel(); reconnect = nil
        loop?.cancel(); loop = nil
        socket?.cancel(with: .goingAway, reason: nil); socket = nil
        motion.stopDeviceMotionUpdates()
        active = false; origin = nil; filtered = 0
        status = "Head steering off"
    }
}

@MainActor
final class SurvivorFeedback: ObservableObject {
    private var player: AVAudioPlayer?

    func update(from oldState: SurvivorDetectionState, to newState: SurvivorDetectionState) {
        guard newState.count > 0, oldState.count == 0 else { return }

        let haptic = UIImpactFeedbackGenerator(style: .soft)
        haptic.prepare()
        haptic.impactOccurred(intensity: 0.32)

        do {
            player = try AVAudioPlayer(data: Self.chime)
            player?.volume = 0.22
            player?.play()
        } catch {
            // Detection remains functional if audio is unavailable or another session owns output.
        }
    }

    private static let chime: Data = {
        let sampleRate = 22_050
        let sampleCount = Int(Double(sampleRate) * 0.14)
        var pcm = [Int16](repeating: 0, count: sampleCount)
        for index in pcm.indices {
            let time = Double(index) / Double(sampleRate)
            let envelope = sin(.pi * Double(index) / Double(sampleCount))
            let tone = 0.7 * sin(2 * .pi * 740 * time) + 0.3 * sin(2 * .pi * 988 * time)
            pcm[index] = Int16(max(-1, min(1, tone * envelope * 0.16)) * Double(Int16.max))
        }
        return SurvivorFeedback.wav(samples: pcm, sampleRate: sampleRate)
    }()

    private static func wav(samples: [Int16], sampleRate: Int) -> Data {
        var data = Data()
        func append<T>(_ value: T) {
            var value = value
            withUnsafeBytes(of: &value) { data.append(contentsOf: $0) }
        }
        data.append("RIFF".data(using: .ascii)!)
        append(UInt32(36 + samples.count * 2).littleEndian)
        data.append("WAVEfmt ".data(using: .ascii)!)
        append(UInt32(16).littleEndian)
        append(UInt16(1).littleEndian)
        append(UInt16(1).littleEndian)
        append(UInt32(sampleRate).littleEndian)
        append(UInt32(sampleRate * 2).littleEndian)
        append(UInt16(2).littleEndian)
        append(UInt16(16).littleEndian)
        data.append("data".data(using: .ascii)!)
        append(UInt32(samples.count * 2).littleEndian)
        for sample in samples { append(sample.littleEndian) }
        return data
    }
}

private extension SurvivorDetectionState {
    var count: Int {
        if case .ready(let count, _) = self { return count }
        return 0
    }
}
