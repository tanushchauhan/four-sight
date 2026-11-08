import MetalKit
import SwiftUI
import UIKit

struct VisorView: View {
    @StateObject private var session = SessionOwner()

    var body: some View {
        MetalVisor(session: session.controller)
            .ignoresSafeArea()
            .statusBarHidden(true)
            .persistentSystemOverlays(.hidden)
            .onAppear {
                UIApplication.shared.isIdleTimerDisabled = true
                session.controller.start()
            }
            .onDisappear {
                session.controller.pause()
                UIApplication.shared.isIdleTimerDisabled = false
            }
    }
}

private final class SessionOwner: ObservableObject {
    let controller = ARSessionController()
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
