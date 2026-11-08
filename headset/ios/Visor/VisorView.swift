import MetalKit
import SwiftUI
import UIKit

struct VisorView: View {
    @StateObject private var controller = ARSessionController()
    @AppStorage("cameraURL") private var address = "http://prada.local:8080/stream"
    @State private var settings = true

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
                        Button("Connect") { controller.start(address) }
                    }
                    if settings || controller.status != "Live" { Text(controller.status).foregroundStyle(.white) }
                }.padding().background(.black.opacity(0.7))
            }
            .statusBarHidden(true)
            .persistentSystemOverlays(.hidden)
            .onAppear {
                UIApplication.shared.isIdleTimerDisabled = true
                controller.start(address)
            }
            .onDisappear {
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
