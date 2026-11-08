import MetalKit
import SwiftUI
import UIKit

/// Everything worn is drawn inside the lenses by `StereoRenderer`. The camera address is the only
/// flat control: it shows while the camera is not live, folds away once it is, and a tap brings it back.
struct VisorView: View {
    @StateObject private var controller = ARSessionController()
    @AppStorage("cameraURL") private var address = "http://prada.local:8080/stream"
    @AppStorage("cameraSource") private var sourceName = CameraSource.robot.rawValue
    @State private var phone = PhoneCamera()
    @State private var settings = true
    @State private var layerStatus = "Lens layer: drawing"
    @FocusState private var focused: Control?

    private enum Control: Hashable {
        case address
        case connect
        case wear
        case robot
        case phone
    }

    private var source: CameraSource { CameraSource(rawValue: sourceName) ?? .robot }

    private var briefing: VisorBriefing {
        var briefing = VisorBriefing.sample
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
            MetalVisor(session: controller, phone: phone, source: source, briefing: briefing, layerStatus: $layerStatus)
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
            controller.pause()
            phone.pause()
            UIApplication.shared.isIdleTimerDisabled = false
        }
        .onChange(of: controller.status) { _, status in
            if source == .robot, status == "Live", focused == nil { settings = false }
        }
    }

    private func apply(_ source: CameraSource) {
        sourceName = source.rawValue
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
                    .onSubmit { controller.start(address) }
                Text(controller.status)
                    .font(VisorToken.body(16))
                    .foregroundStyle(VisorToken.ink2.color)
                    .frame(minHeight: 22, alignment: .leading)
            }
            }
            rule
            HStack(spacing: VisorToken.spaceSm) {
                if source == .robot {
                    Button(controller.status == "Connecting…" ? "Connecting" : "Connect") {
                        focused = nil
                        controller.start(address)
                    }
                    .buttonStyle(SheetButtonStyle(primary: true, focused: focused == .connect))
                    .focused($focused, equals: .connect)
                    .disabled(controller.status == "Connecting…")
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
    let source: CameraSource
    let briefing: VisorBriefing
    @Binding var layerStatus: String

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> MTKView {
        let view = MTKView(frame: .zero, device: MTLCreateSystemDefaultDevice())
        view.isOpaque = true
        view.backgroundColor = VisorToken.paper3.uiColor
        if let renderer = StereoRenderer(view: view, session: session, phone: phone) {
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
