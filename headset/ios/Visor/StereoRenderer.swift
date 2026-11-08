import ARKit
import Metal
import MetalKit
import os
import simd
import UIKit

struct EyeUniforms {
    var lensM00: Float
    var lensM11: Float
    var lensM02: Float
    var lensM12: Float
    var frustumCenterX: Float
    var frustumCenterY: Float
    var frustumSizeX: Float
    var frustumSizeY: Float
    var wideM00: Float
    var wideM01: Float
    var wideM10: Float
    var wideM11: Float
    var wideM20: Float
    var wideM21: Float
}

struct WarpVertex {
    var ndc: SIMD2<Float>
    var uv: SIMD2<Float>
}

/// Renders the AR camera stream into left and right eye textures using Cardboard's asymmetric eye
/// frustums and PassthroughController's uncropped camera quad math, then warps both eye textures
/// through the Cardboard v1 lens distortion mesh onto the screen.
final class StereoRenderer: NSObject, MTKViewDelegate {
    // Matches StereoRig's m_RenderScale = 0.75 (visor default).
    private static let renderScale: Float = 0.75

    private let session: ARSessionController
    private let device: MTLDevice
    private let queue: MTLCommandQueue
    private let eyePipeline: MTLRenderPipelineState
    private var robotTexture: MTLTexture?
    private var lastFrameTime = Date.distantPast
    private let warpPipeline: MTLRenderPipelineState
    private let log = Logger(subsystem: "com.hackutd26.visor", category: "stereo")

    private var textureCache: CVMetalTextureCache?
    private var eyeTextures: [MTLTexture] = []
    private var eyeSize = MTLSize(width: 0, height: 0, depth: 1)
    private var meshes: [(buffer: MTLBuffer, indexOffset: Int, indexCount: Int)] = []
    private var markerMesh: (buffer: MTLBuffer, indexOffset: Int, indexCount: Int)?
    private var markerTexture: MTLTexture?
    private var meshScreenMeters = SIMD2<Float>(repeating: -1)
    private var eyeProjections: [EyeProjection] = []
    private var didLogPassthrough = false

    init?(view: MTKView, session: ARSessionController) {
        guard let device = view.device ?? MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue() else { return nil }
        self.session = session
        self.device = device
        self.queue = queue

        view.device = device
        view.colorPixelFormat = .bgra8Unorm
        view.depthStencilPixelFormat = .invalid
        view.clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        view.isPaused = false
        view.enableSetNeedsDisplay = false
        view.preferredFramesPerSecond = 60

        guard let shaderURL = Bundle.main.url(forResource: "Shaders", withExtension: "metal.txt"),
              let source = try? String(contentsOf: shaderURL, encoding: .utf8),
              let library = try? device.makeLibrary(source: source, options: nil) else { return nil }
        eyePipeline = Self.makePipeline(
            device: device,
            library: library,
            vertex: "eyeVertex",
            fragment: "robotFragment",
            pixelFormat: .rgba8Unorm,
            vertexDescriptor: nil
        )

        let vertices = MTLVertexDescriptor()
        vertices.attributes[0].format = .float2
        vertices.attributes[0].offset = 0
        vertices.attributes[0].bufferIndex = 0
        vertices.attributes[1].format = .float2
        vertices.attributes[1].offset = MemoryLayout<SIMD2<Float>>.stride
        vertices.attributes[1].bufferIndex = 0
        vertices.layouts[0].stride = MemoryLayout<WarpVertex>.stride

        warpPipeline = Self.makePipeline(
            device: device,
            library: library,
            vertex: "warpVertex",
            fragment: "warpFragment",
            pixelFormat: view.colorPixelFormat,
            vertexDescriptor: vertices
        )

        super.init()

        var cache: CVMetalTextureCache?
        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, device, nil, &cache)
        textureCache = cache
        markerTexture = Self.makeMarkerTexture(device: device)
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        rebuildEyes(drawableSize: size)
        rebuildMeshes()
    }

    func draw(in view: MTKView) {
        guard let drawable = view.currentDrawable,
              let pass = view.currentRenderPassDescriptor,
              let commandBuffer = queue.makeCommandBuffer() else { return }

        if eyeTextures.count != 2 || eyeSize.width == 0 {
            rebuildEyes(drawableSize: view.drawableSize)
        }
        rebuildMeshes()

        if let jpeg = session.takeFrame() {
            lastFrameTime = Date()
            robotTexture = try? MTKTextureLoader(device: device).newTexture(data: jpeg, options: [.SRGB: false])
        }
        if Date().timeIntervalSince(lastFrameTime) > 2 { robotTexture = nil }
        encodeRobot(into: commandBuffer)

        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: pass) else { return }
        encoder.setRenderPipelineState(warpPipeline)

        for (index, mesh) in meshes.enumerated() where index < eyeTextures.count {
            encoder.setVertexBuffer(mesh.buffer, offset: 0, index: 0)
            encoder.setFragmentTexture(eyeTextures[index], index: 0)
            encoder.drawIndexedPrimitives(
                type: .triangle,
                indexCount: mesh.indexCount,
                indexType: .uint16,
                indexBuffer: mesh.buffer,
                indexBufferOffset: mesh.indexOffset
            )
        }

        if let markerMesh, let markerTexture {
            encoder.setVertexBuffer(markerMesh.buffer, offset: 0, index: 0)
            encoder.setFragmentTexture(markerTexture, index: 0)
            encoder.drawIndexedPrimitives(
                type: .triangle,
                indexCount: markerMesh.indexCount,
                indexType: .uint16,
                indexBuffer: markerMesh.buffer,
                indexBufferOffset: markerMesh.indexOffset
            )
        }

        encoder.endEncoding()
        commandBuffer.present(drawable)
        commandBuffer.commit()
    }

    private func encodeRobot(into commandBuffer: MTLCommandBuffer) {
        for (texture, projection) in zip(eyeTextures, eyeProjections) {
            let pass = MTLRenderPassDescriptor()
            pass.colorAttachments[0].texture = texture
            pass.colorAttachments[0].loadAction = .clear
            pass.colorAttachments[0].storeAction = .store
            pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
            guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: pass) else { continue }
            if let image = robotTexture {
                // Center the full image on the optical axis, preserving camera aspect ratio.
                let aspect = Float(image.width) / Float(image.height)
                let width: Float = 2 * tan(35 * Float.pi / 180)
                var u = EyeUniforms(lensM00: projection.m00, lensM11: projection.m11,
                    lensM02: projection.m02, lensM12: projection.m12,
                    frustumCenterX: 0, frustumCenterY: 0, frustumSizeX: width, frustumSizeY: width / aspect,
                    wideM00: 1, wideM01: 0, wideM10: 0, wideM11: 1, wideM20: 0, wideM21: 0)
                encoder.setRenderPipelineState(eyePipeline)
                encoder.setFragmentBytes(&u, length: MemoryLayout<EyeUniforms>.stride, index: 0)
                encoder.setFragmentTexture(image, index: 0)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            }
            encoder.endEncoding()
        }
    }

    private struct PassthroughParams {
        var frustumCenter: SIMD2<Float>
        var frustumSize: SIMD2<Float>
        var wideM00: Float
        var wideM01: Float
        var wideM10: Float
        var wideM11: Float
        var wideM20: Float
        var wideM21: Float
    }

    /// Exact port of PassthroughController.OnCameraFrame and PassthroughController.TryWidenDisplayTransform.
    ///
    /// Unity's ARKit CameraSubsystem converts ARFrame.displayTransform (which uses UIKit top-left view UVs)
    /// to Unity's bottom-left screen UV convention: u_u = u_ios, v_u = 1 - v_ios.
    /// Then TryWidenDisplayTransform finds the uncropped screen-UV rectangle [min, max] covering the whole
    /// camera sensor (0..1 x 0..1), and computes the camera frustum size and center from ARKit's projectionMatrix.
    private static func computePassthroughParams(
        frame: ARFrame,
        orientation: UIInterfaceOrientation,
        viewport: CGSize
    ) -> PassthroughParams? {
        let t = frame.displayTransform(for: orientation, viewportSize: viewport)
        let a = Float(t.a)
        let b = Float(t.b)
        let c = Float(t.c)
        let d = Float(t.d)
        let tx = Float(t.tx)
        let ty = Float(t.ty)

        // Convert UIKit top-left displayTransform to Unity bottom-left screen UV (v_ios = 1 - v_u):
        // tex.x = u_u * a + (1 - v_u) * c + tx = u_u * a + v_u * (-c) + (c + tx)
        // tex.y = u_u * b + (1 - v_u) * d + ty = u_u * b + v_u * (-d) + (d + ty)
        let c0 = SIMD2<Float>(a, b)
        let c1 = SIMD2<Float>(-c, -d)
        let c2 = SIMD2<Float>(c + tx, d + ty)

        let determinant = c0.x * c1.y - c1.x * c0.y
        guard abs(determinant) > 1e-6 else { return nil }

        func screenUv(_ texture: SIMD2<Float>) -> SIMD2<Float> {
            let delta = texture - c2
            return SIMD2<Float>(
                (c1.y * delta.x - c1.x * delta.y) / determinant,
                (c0.x * delta.y - c0.y * delta.x) / determinant
            )
        }

        let p00 = screenUv(SIMD2(0, 0))
        let p10 = screenUv(SIMD2(1, 0))
        let p01 = screenUv(SIMD2(0, 1))
        let p11 = screenUv(SIMD2(1, 1))
        let minUv = simd_min(simd_min(p00, p10), simd_min(p01, p11))
        let maxUv = simd_max(simd_max(p00, p10), simd_max(p01, p11))
        let size = maxUv - minUv

        let wideM00 = c0.x * size.x
        let wideM01 = c0.y * size.x
        let wideM10 = c1.x * size.y
        let wideM11 = c1.y * size.y
        let offset = minUv.x * c0 + minUv.y * c1 + c2

        // ARKit's projectionMatrix for the same orientation and viewport.
        let projection = frame.camera.projectionMatrix(
            for: orientation,
            viewportSize: viewport,
            zNear: 0.1,
            zFar: 100.0
        )
        let projM00 = projection.columns.0.x
        let projM11 = projection.columns.1.y
        let projM02 = projection.columns.2.x
        let projM12 = projection.columns.2.y

        guard abs(projM00) > 1e-6, abs(projM11) > 1e-6 else { return nil }

        // PassthroughController.cs lines 178-182:
        // Screen UV maps to NDC as 2uv - 1, which ARKit's projection maps to camera space at unit
        // distance as (ndc + p02) / p00 across and (ndc + p12) / p11 up.
        let frustumSize = SIMD2<Float>(2 * size.x / projM00, 2 * size.y / projM11)
        let frustumCenter = SIMD2<Float>(
            (minUv.x + maxUv.x - 1 + projM02) / projM00,
            (minUv.y + maxUv.y - 1 + projM12) / projM11
        )

        return PassthroughParams(
            frustumCenter: frustumCenter,
            frustumSize: frustumSize,
            wideM00: wideM00,
            wideM01: wideM01,
            wideM10: wideM10,
            wideM11: wideM11,
            wideM20: offset.x,
            wideM21: offset.y
        )
    }

    private static func currentInterfaceOrientation() -> UIInterfaceOrientation {
        for scene in UIApplication.shared.connectedScenes {
            if let windowScene = scene as? UIWindowScene {
                let orientation = windowScene.interfaceOrientation
                if orientation == .landscapeLeft || orientation == .landscapeRight {
                    return orientation
                }
            }
        }
        // Unity's ScreenOrientation.LandscapeLeft (top of phone on user's left) is UIInterfaceOrientation.landscapeRight.
        return .landscapeRight
    }

    private func rebuildEyes(drawableSize: CGSize) {
        let w = max(Int((Float(drawableSize.width) * 0.5 * Self.renderScale).rounded()), 16)
        let h = max(Int((Float(drawableSize.height) * Self.renderScale).rounded()), 16)
        guard w != eyeSize.width || h != eyeSize.height || eyeTextures.count != 2 else { return }

        let descriptor = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .rgba8Unorm,
            width: w,
            height: h,
            mipmapped: false
        )
        descriptor.usage = [.shaderRead, .renderTarget]
        descriptor.storageMode = .private
        eyeTextures = (0..<2).compactMap { _ in device.makeTexture(descriptor: descriptor) }
        eyeSize = MTLSize(width: w, height: h, depth: 1)
    }

    private func rebuildMeshes() {
        let screenMeters = ScreenMetrics.screenSizeMeters()
        guard screenMeters != meshScreenMeters else { return }

        let lens = LensDistortion(
            viewer: .cardboardV1,
            screenWidthMeters: screenMeters.x,
            screenHeightMeters: screenMeters.y
        )
        eyeProjections = [lens.eyeProjection(.left), lens.eyeProjection(.right)]
        meshes = [Eye.left, Eye.right].compactMap { eye in
            packMesh(lens.makeDistortionMesh(eye))
        }
        let pixels = ScreenMetrics.landscapePixels()
        markerMesh = packMesh(AlignmentMarker.makeMesh(screenPixels: pixels, screenMeters: screenMeters))
        meshScreenMeters = screenMeters

        let fov = lens.fieldOfViewRadians(.left) * (180 / Float.pi)
        log.info("[Stereo] \(lens.viewer.model, privacy: .public): screen \(screenMeters.x, format: .fixed(precision: 4)) x \(screenMeters.y, format: .fixed(precision: 4)) m (\(pixels.x)x\(pixels.y) px), left eye fov L\(fov.x, format: .fixed(precision: 1)) R\(fov.y, format: .fixed(precision: 1)) B\(fov.z, format: .fixed(precision: 1)) T\(fov.w, format: .fixed(precision: 1))")
    }

    private func packMesh(_ mesh: DistortionMesh) -> (buffer: MTLBuffer, indexOffset: Int, indexCount: Int)? {
        let packed = zip(mesh.positions, mesh.uvs).map { position, uv in
            WarpVertex(ndc: position, uv: uv)
        }
        let vertexBytes = packed.count * MemoryLayout<WarpVertex>.stride
        let indexBytes = mesh.indices.count * MemoryLayout<UInt16>.stride
        guard let buffer = device.makeBuffer(length: vertexBytes + indexBytes, options: .storageModeShared) else {
            return nil
        }
        packed.withUnsafeBytes { raw in
            if let base = raw.baseAddress {
                buffer.contents().copyMemory(from: base, byteCount: vertexBytes)
            }
        }
        mesh.indices.withUnsafeBytes { raw in
            if let base = raw.baseAddress {
                buffer.contents().advanced(by: vertexBytes).copyMemory(from: base, byteCount: indexBytes)
            }
        }
        return (buffer, vertexBytes, mesh.indices.count)
    }

    private static func makeMarkerTexture(device: MTLDevice) -> MTLTexture? {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .rgba8Unorm,
            width: 1,
            height: 1,
            mipmapped: false
        )
        descriptor.usage = [.shaderRead]
        guard let texture = device.makeTexture(descriptor: descriptor) else { return nil }
        // AlignmentMarker.DefaultColor = Color32(0xb4, 0xb4, 0xb4, 0xff).
        var pixel: [UInt8] = [0xb4, 0xb4, 0xb4, 0xff]
        texture.replace(
            region: MTLRegionMake2D(0, 0, 1, 1),
            mipmapLevel: 0,
            withBytes: &pixel,
            bytesPerRow: 4
        )
        return texture
    }

    private func metalTexture(
        from pixelBuffer: CVPixelBuffer,
        cache: CVMetalTextureCache,
        plane: Int,
        format: MTLPixelFormat
    ) -> (texture: MTLTexture, holder: CVMetalTexture)? {
        let width = CVPixelBufferGetWidthOfPlane(pixelBuffer, plane)
        let height = CVPixelBufferGetHeightOfPlane(pixelBuffer, plane)
        var holder: CVMetalTexture?
        let status = CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault,
            cache,
            pixelBuffer,
            nil,
            format,
            width,
            height,
            plane,
            &holder
        )
        guard status == kCVReturnSuccess, let holder, let texture = CVMetalTextureGetTexture(holder) else {
            return nil
        }
        return (texture, holder)
    }

    private static func makePipeline(
        device: MTLDevice,
        library: MTLLibrary?,
        vertex: String,
        fragment: String,
        pixelFormat: MTLPixelFormat,
        vertexDescriptor: MTLVertexDescriptor?
    ) -> MTLRenderPipelineState {
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = library?.makeFunction(name: vertex)
        descriptor.fragmentFunction = library?.makeFunction(name: fragment)
        descriptor.vertexDescriptor = vertexDescriptor
        descriptor.colorAttachments[0].pixelFormat = pixelFormat
        do {
            return try device.makeRenderPipelineState(descriptor: descriptor)
        } catch {
            fatalError("Metal pipeline \(vertex) failed: \(error)")
        }
    }
}
