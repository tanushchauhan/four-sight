import ARKit

enum CameraSource: String {
    case robot
    case phone
}

/// The iPhone's rear camera through ARKit, for the Cardboard passthrough path.
final class PhoneCamera {
    let session = ARSession()
    private(set) var running = false

    func start() {
        guard !running, ARWorldTrackingConfiguration.isSupported else { return }
        let configuration = ARWorldTrackingConfiguration()
        configuration.worldAlignment = .gravity
        session.run(configuration, options: [.resetTracking])
        running = true
    }

    func pause() {
        guard running else { return }
        session.pause()
        running = false
    }

    func currentFrame() -> ARFrame? {
        running ? session.currentFrame : nil
    }
}
