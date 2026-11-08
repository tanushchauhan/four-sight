import ARKit
import Foundation
import os
import simd

/// Runs the ARKit world-tracking camera session for the Cardboard passthrough visor.
final class ARSessionController: NSObject, ARSessionDelegate {
    let session = ARSession()
    private let log = Logger(subsystem: "com.hackutd26.visor", category: "arkit")
    private let lock = NSLock()
    private var latestFrame: ARFrame?
    private var lastTrackingState = ""

    func start() {
        let configuration = ARWorldTrackingConfiguration()
        configuration.worldAlignment = .gravity
        if ARWorldTrackingConfiguration.supportsFrameSemantics(.sceneDepth) {
            configuration.frameSemantics.insert(.sceneDepth)
        }
        session.delegate = self
        session.run(configuration, options: [.resetTracking, .removeExistingAnchors])
        log.info("ARSession started")
    }

    func pause() {
        session.pause()
    }

    func currentFrame() -> ARFrame? {
        lock.lock()
        defer { lock.unlock() }
        return latestFrame ?? session.currentFrame
    }

    func session(_ session: ARSession, didUpdate frame: ARFrame) {
        let stateDescription = Self.describe(frame.camera.trackingState)
        lock.lock()
        latestFrame = frame
        let changed = stateDescription != lastTrackingState
        if changed {
            lastTrackingState = stateDescription
        }
        lock.unlock()

        if changed {
            log.info("Tracking state: \(stateDescription, privacy: .public)")
        }
    }

    private static func describe(_ state: ARCamera.TrackingState) -> String {
        switch state {
        case .notAvailable:
            return "notAvailable"
        case .normal:
            return "normal"
        case .limited(let reason):
            return "limited(\(String(describing: reason)))"
        @unknown default:
            return "unknown"
        }
    }
}
