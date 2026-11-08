import Combine
import CoreImage
import Foundation
import UltralyticsYOLO

enum SurvivorDetectionState: Equatable {
    case loading
    case ready(count: Int, confidence: Float)
    case failed(String)
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

    private let inferenceQueue = DispatchQueue(label: "com.hackutd26.visor.survivor-inference", qos: .userInitiated)
    private let lock = NSLock()
    private var model: YOLO?
    private var busy = false
    private var generation: UInt64 = 0
    private var lastSubmission = CFAbsoluteTimeGetCurrent() - 1
    private var overlay = SurvivorOverlay(revision: 0, image: nil)

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
