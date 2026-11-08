import Combine
import CoreImage
import Foundation
import UltralyticsYOLO

enum SurvivorDetectionState: Equatable {
    case loading
    case ready(count: Int, confidence: Float)
    case failed(String)
}

enum TriageMotion: String, Equatable {
    case moving = "Moving"
    case stationary = "Stationary"
    case uncertain = "Uncertain"
}

struct SurvivorSighting: Equatable, Identifiable {
    let id: UUID
    var distance: Double
    var bearing: Double
    var seen: Date
}

struct SurvivorObservation: Codable, Equatable {
    let confidence: Float
    let box: [Double]
    let maskCoverage: Double
}

struct SurvivorInsight: Equatable {
    var observations: [SurvivorObservation] = []
    var sightings: [SurvivorSighting] = []
    var distanceMeters: Double?
    var bearingDegrees: Double?
    var motion: TriageMotion = .uncertain
    var lastConfirmedDistance: Double?
    var lastConfirmedBearing: Double?

    static let empty = SurvivorInsight()
}

struct SurvivorOverlay {
    let revision: UInt64
    let image: CGImage?
}

/// Runs YOLO segmentation away from the render loop and keeps only the newest result.
/// COCO's `person` class is the baseline survivor signal; a rescue-specific model can replace
/// the bundled model later without changing the camera or overlay path.
final class SurvivorDetector: ObservableObject {
    @Published private(set) var state: SurvivorDetectionState = .loading
    @Published private(set) var insight = SurvivorInsight.empty

    private let inferenceQueue = DispatchQueue(label: "com.hackutd26.visor.survivor-inference", qos: .userInitiated)
    private let lock = NSLock()
    private var model: YOLO?
    private var busy = false
    private var generation: UInt64 = 0
    private var lastSubmission = CFAbsoluteTimeGetCurrent() - 1
    private var overlay = SurvivorOverlay(revision: 0, image: nil)
    private var previousCentroid: CGPoint?
    private var previousCentroidTime: CFTimeInterval?
    private var stableFrames = 0
    private var lastConfirmedDistance: Double?
    private var lastConfirmedBearing: Double?
    private var sightings: [SurvivorSighting] = []

    /// Inference is deliberately capped below display refresh. Metal remains at 60 FPS and always
    /// uses the latest completed mask instead of waiting for the Neural Engine.
    private let minimumInterval: CFTimeInterval = 1.0 / 15.0
    private let confidenceThreshold: Float = 0.42

    init() {
        model = YOLO("yolo26n-seg", task: .segment, useGpu: true, numItemsThreshold: 12) { [weak self] result in
            switch result {
            case .success(let model):
                model.setConfidenceThreshold(0.30)
                model.setIouThreshold(0.65)
                DispatchQueue.main.async {
                    self?.state = .ready(count: 0, confidence: 0)
                }
            case .failure(let error):
                DispatchQueue.main.async {
                    self?.state = .failed(error.localizedDescription)
                }
            }
        }
    }

    func submit(_ image: CIImage) {
        let now = CFAbsoluteTimeGetCurrent()
        lock.lock()
        guard !busy, now - lastSubmission >= minimumInterval, model?.isLoaded == true else {
            lock.unlock()
            return
        }
        busy = true
        lastSubmission = now
        let model = model
        let generation = generation
        lock.unlock()

        inferenceQueue.async { [weak self] in
            guard let self, let model else { return }
            let result = model(image)
            let survivors = result.boxes.enumerated().filter {
                $0.element.cls.lowercased() == "person" && $0.element.conf >= self.confidenceThreshold
            }
            let mask = self.makeSurvivorMask(result: result, survivorIndices: survivors.map(\.offset))
            let confidence = survivors.map(\.element.conf).max() ?? 0
            var insight = self.makeInsight(survivors: survivors.map(\.element), at: now)
            insight.observations = survivors.map { item in
                let box = item.element.xywhn
                let mask = result.masks?.masks
                let pixels = (mask != nil && item.offset < mask!.count) ? mask![item.offset].flatMap { $0 } : []
                return SurvivorObservation(confidence: item.element.conf,
                    box: [Double(box.minX), Double(box.minY), Double(box.width), Double(box.height)],
                    maskCoverage: pixels.isEmpty ? 0 : Double(pixels.filter { $0 > 0.5 }.count) / Double(pixels.count))
            }

            self.lock.lock()
            self.busy = false
            guard self.generation == generation else { self.lock.unlock(); return }
            self.overlay = SurvivorOverlay(revision: self.overlay.revision &+ 1, image: mask)
            self.lock.unlock()

            DispatchQueue.main.async {
                self.lock.lock()
                let current = self.generation == generation
                self.lock.unlock()
                guard current else { return }
                self.insight = insight
                self.state = .ready(count: survivors.count, confidence: confidence)
            }
        }
    }

    func reset() {
        lock.lock()
        generation &+= 1
        overlay = SurvivorOverlay(revision: overlay.revision &+ 1, image: nil)
        lock.unlock()
        DispatchQueue.main.async { self.state = .loading }
    }

    func latestOverlay() -> SurvivorOverlay {
        lock.lock(); defer { lock.unlock() }
        return overlay
    }

    private func makeInsight(survivors: [Box], at time: CFTimeInterval) -> SurvivorInsight {
        let now = Date()
        sightings.removeAll { now.timeIntervalSince($0.seen) > 45 }
        for person in survivors {
            let distance = Double(min(15, max(1, 1.7 / max(person.xywhn.height, 0.12))))
            let bearing = Double(min(34, max(-34, (person.xywhn.midX - 0.5) * 68)))
            if let index = sightings.indices.min(by: {
                abs(sightings[$0].bearing - bearing) < abs(sightings[$1].bearing - bearing)
            }), abs(sightings[index].bearing - bearing) < 6,
               abs(sightings[index].distance - distance) < 2 {
                sightings[index].seen = now
            } else {
                sightings.append(SurvivorSighting(id: UUID(), distance: distance, bearing: bearing, seen: now))
            }
        }
        sightings = Array(sightings.suffix(24))
        guard let primary = survivors.max(by: { $0.conf < $1.conf }) else {
            previousCentroid = nil
            previousCentroidTime = nil
            stableFrames = 0
            return SurvivorInsight(
                sightings: sightings,
                distanceMeters: nil,
                bearingDegrees: nil,
                motion: .uncertain,
                lastConfirmedDistance: lastConfirmedDistance,
                lastConfirmedBearing: lastConfirmedBearing
            )
        }

        let box = primary.xywhn
        let centroid = CGPoint(x: box.midX, y: box.midY)
        // Monocular scale estimate: assume a standing/crouched human occupies about 1.7 m vertically.
        // This is intentionally presented as an estimate until depth or calibrated camera geometry exists.
        let distance = min(15, max(1, 1.7 / max(box.height, 0.12)))
        let bearing = min(34, max(-34, (box.midX - 0.5) * 68))
        var motion: TriageMotion = .uncertain

        if let previousCentroid, let previousCentroidTime {
            let elapsed = max(0.05, time - previousCentroidTime)
            let displacement = hypot(centroid.x - previousCentroid.x, centroid.y - previousCentroid.y)
            let normalizedSpeed = displacement / elapsed
            if normalizedSpeed > 0.18 {
                stableFrames = 0
                motion = .moving
            } else if normalizedSpeed < 0.05 {
                stableFrames += 1
                motion = stableFrames >= 3 ? .stationary : .uncertain
            } else {
                stableFrames = 0
            }
        }

        previousCentroid = centroid
        previousCentroidTime = time
        lastConfirmedDistance = distance
        lastConfirmedBearing = bearing
        return SurvivorInsight(
            sightings: sightings,
            distanceMeters: distance,
            bearingDegrees: bearing,
            motion: motion,
            lastConfirmedDistance: distance,
            lastConfirmedBearing: bearing
        )
    }

    private func makeSurvivorMask(result: YOLOResult, survivorIndices: [Int]) -> CGImage? {
        guard !survivorIndices.isEmpty,
              let masks = result.masks?.masks,
              let first = masks.first,
              !first.isEmpty,
              let firstRow = first.first,
              !firstRow.isEmpty else { return nil }

        let width = firstRow.count
        let height = first.count
        // Premultiplied RGBA for a 41% cyan overlay.
        let cyan = (r: UInt8(28), g: UInt8(94), b: UInt8(105), a: UInt8(105))
        var pixels = [UInt8](repeating: 0, count: width * height * 4)

        for index in survivorIndices where index < masks.count {
            let instance = masks[index]
            guard instance.count == height else { continue }
            for y in 0..<height where instance[y].count == width {
                for x in 0..<width where instance[y][x] > 0.5 {
                    let pixel = (y * width + x) * 4
                    pixels[pixel] = cyan.r
                    pixels[pixel + 1] = cyan.g
                    pixels[pixel + 2] = cyan.b
                    pixels[pixel + 3] = cyan.a
                }
            }
        }

        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: colorSpace,
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: true,
            intent: .defaultIntent
        )
    }
}
