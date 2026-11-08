import Foundation
import simd
import UIKit

// Cardboard v1 viewer and lens distortion math, ported directly from the Google Cardboard SDK
// (polynomial_radial_distortion.cc, lens_distortion.cc, distortion_mesh.cc, Apache 2.0)
// and headset/Assets/_Project/Scripts/Stereo (LensDistortion.cs, PolynomialRadialDistortion.cs,
// ViewerParams.cs, AlignmentMarker.cs, ScreenMetrics.cs).

enum Eye: Int {
    case left = 0
    case right = 1
}

struct ViewerParams {
    enum VerticalAlignment: Int {
        case bottom = 0 // phone rests against a fixed bottom tray
        case center = 1 // screen is centered on the lenses
        case top = 2    // phone rests against a fixed top tray
    }

    var vendor: String
    var model: String
    var screenToLensDistance: Float
    var interLensDistance: Float
    // Left eye's view extent (left, right, bottom, top) from the optical axis in degrees.
    // The right eye mirrors left and right.
    var leftEyeFieldOfViewAngles: [Float]
    var verticalAlignment: VerticalAlignment
    var trayToLensDistance: Float
    // K1, K2, ... of p' = p (1 + K1 r^2 + K2 r^4 + ...), r in tan-angle units.
    var distortionCoefficients: [Float]

    // Original 2014 Cardboard (sdk/qrcode/cardboard_v1/cardboard_v1.cc).
    static let cardboardV1 = ViewerParams(
        vendor: "Google, Inc.",
        model: "Cardboard v1",
        screenToLensDistance: 0.042,
        interLensDistance: 0.06,
        leftEyeFieldOfViewAngles: [40, 40, 40, 40],
        verticalAlignment: .bottom,
        trayToLensDistance: 0.035,
        distortionCoefficients: [0.441, 0.156]
    )

    // Pop-Tech asymmetric biconvex, sold as a Cardboard V2 lens: 34 mm diameter, 45 mm focal length.
    // The screen sits on the focal plane so a relaxed eye sees the virtual image at infinity.
    // Distortion coefficients are the published Cardboard V2 pair for this 34 mm lens family.
    // Half-angles are the lens aperture from the 18 mm Cardboard eye relief, atan((34/2) / 18).
    static let diameterMeters: Float = 0.034
    static let focalLengthMeters: Float = 0.045
    static let eyeReliefMeters: Float = 0.018

    static let popTech34mm = ViewerParams(
        vendor: "Pop-Tech",
        model: "34 mm biconvex, 45 mm",
        screenToLensDistance: focalLengthMeters,
        interLensDistance: 0.06,
        leftEyeFieldOfViewAngles: [
            apertureHalfAngleDegrees, apertureHalfAngleDegrees,
            apertureHalfAngleDegrees, apertureHalfAngleDegrees
        ],
        verticalAlignment: .bottom,
        trayToLensDistance: 0.035,
        distortionCoefficients: [0.33582564, 0.55348791]
    )

    static var apertureHalfAngleDegrees: Float {
        atan((diameterMeters / 2) / eyeReliefMeters) * 180 / Float.pi
    }
}

struct PolynomialRadialDistortion {
    private static let maxInverseIterations = 100
    private let coefficients: [Float]

    init(coefficients: [Float]) {
        self.coefficients = coefficients
    }

    func distortionFactor(radiusSquared: Float) -> Float {
        var radiusFactor: Float = 1
        var distortionFactor: Float = 1
        for coefficient in coefficients {
            radiusFactor *= radiusSquared
            distortionFactor += coefficient * radiusFactor
        }
        return distortionFactor
    }

    func distortRadius(_ radius: Float) -> Float {
        radius * distortionFactor(radiusSquared: radius * radius)
    }

    func distort(_ point: SIMD2<Float>) -> SIMD2<Float> {
        point * distortionFactor(radiusSquared: simd_length_squared(point))
    }

    // Secant solve of distort(result) = point, matching the Cardboard SDK.
    func distortInverse(_ point: SIMD2<Float>) -> SIMD2<Float> {
        let radius = simd_length(point)
        if radius == 0 {
            return .zero
        }

        var r0 = radius / 2
        var r1 = radius / 3
        var dr0 = radius - distortRadius(r0)
        var iteration = 0
        while iteration < Self.maxInverseIterations && abs(r1 - r0) > 0.0001 {
            let dr1 = radius - distortRadius(r1)
            let r2 = r1 - dr1 * ((r1 - r0) / (dr1 - dr0))
            r0 = r1
            r1 = r2
            dr0 = dr1
            iteration += 1
        }
        return point * (r1 / radius)
    }
}

// Asymmetric frustum projection for an eye at unit distance in front (looking down -Z).
// A point (x, y, -1) projects to NDC = (m00 * x - m02, m11 * y - m12), so inverse is:
// x = (ndc.x + m02) / m00, y = (ndc.y + m12) / m11.
// Straight ahead (x = 0, y = 0) is the optical axis of the lens.
struct EyeProjection {
    var m00: Float
    var m11: Float
    var m02: Float
    var m12: Float
}

struct DistortionMesh {
    static let resolution = 40

    var positions: [SIMD2<Float>]
    var uvs: [SIMD2<Float>]
    var indices: [UInt16]
}

struct LensDistortion {
    private struct Viewport {
        var width: Float
        var height: Float
        var xEyeOffset: Float
        var yEyeOffset: Float
    }

    // Assumed bezel between the tray and the screen's lit area (LensDistortion.cs).
    private static let defaultBorderSizeMeters: Float = 0.003

    let viewer: ViewerParams
    let distortion: PolynomialRadialDistortion
    let screenWidthMeters: Float
    let screenHeightMeters: Float
    // Radians as (left, right, bottom, top), indexed by Eye.
    private let fieldOfView: [SIMD4<Float>]

    init(viewer: ViewerParams, screenWidthMeters: Float, screenHeightMeters: Float) {
        self.viewer = viewer
        let poly = PolynomialRadialDistortion(coefficients: viewer.distortionCoefficients)
        self.distortion = poly
        self.screenWidthMeters = screenWidthMeters
        self.screenHeightMeters = screenHeightMeters
        let left = Self.calculateFieldOfView(
            viewer: viewer,
            distortion: poly,
            screenWidthMeters: screenWidthMeters,
            screenHeightMeters: screenHeightMeters
        )
        self.fieldOfView = [left, SIMD4(left.y, left.x, left.z, left.w)]
    }

    func fieldOfViewRadians(_ eye: Eye) -> SIMD4<Float> {
        fieldOfView[eye.rawValue]
    }

    func eyeOffset(_ eye: Eye) -> SIMD3<Float> {
        SIMD3((eye == .left ? -0.5 : 0.5) * viewer.interLensDistance, 0, 0)
    }

    func eyeProjection(_ eye: Eye) -> EyeProjection {
        let fov = fieldOfView[eye.rawValue]
        let left = -tan(fov.x)
        let right = tan(fov.y)
        let bottom = -tan(fov.z)
        let top = tan(fov.w)
        return EyeProjection(
            m00: 2 / (right - left),
            m11: 2 / (top - bottom),
            m02: (right + left) / (right - left),
            m12: (top + bottom) / (top - bottom)
        )
    }

    func screenUvToTextureUv(_ screenUv: SIMD2<Float>, eye: Eye) -> SIMD2<Float> {
        let (screen, texture) = viewports(eye)
        let tanAngle = SIMD2(
            screenUv.x * screen.width - screen.xEyeOffset,
            screenUv.y * screen.height - screen.yEyeOffset
        )
        let distorted = distortion.distort(tanAngle)
        return SIMD2(
            (distorted.x + texture.xEyeOffset) / texture.width,
            (distorted.y + texture.yEyeOffset) / texture.height
        )
    }

    func textureUvToScreenUv(_ textureUv: SIMD2<Float>, eye: Eye) -> SIMD2<Float> {
        let (screen, texture) = viewports(eye)
        let tanAngle = SIMD2(
            textureUv.x * texture.width - texture.xEyeOffset,
            textureUv.y * texture.height - texture.yEyeOffset
        )
        let undistorted = distortion.distortInverse(tanAngle)
        return SIMD2(
            (undistorted.x + screen.xEyeOffset) / screen.width,
            (undistorted.y + screen.yEyeOffset) / screen.height
        )
    }

    // 40x40 grid mesh that warps one eye's texture onto its half of the screen.
    // Positions are in normalized device coordinates (-1...1, y up) for the whole screen.
    // UVs index the eye texture with (0, 0) at bottom-left.
    func makeDistortionMesh(_ eye: Eye) -> DistortionMesh {
        let n = DistortionMesh.resolution
        var positions = [SIMD2<Float>]()
        var uvs = [SIMD2<Float>]()
        positions.reserveCapacity(n * n)
        uvs.reserveCapacity(n * n)
        for row in 0..<n {
            for col in 0..<n {
                let textureUv = SIMD2(Float(col) / Float(n - 1), Float(row) / Float(n - 1))
                let screenUv = textureUvToScreenUv(textureUv, eye: eye)
                positions.append(SIMD2(2 * screenUv.x - 1, 2 * screenUv.y - 1))
                uvs.append(textureUv)
            }
        }

        var indices = [UInt16]()
        indices.reserveCapacity((n - 1) * (n - 1) * 6)
        for row in 0..<(n - 1) {
            for col in 0..<(n - 1) {
                let index = UInt16(row * n + col)
                let rowStride = UInt16(n)
                indices.append(index)
                indices.append(index + rowStride)
                indices.append(index + 1)
                indices.append(index + 1)
                indices.append(index + rowStride)
                indices.append(index + rowStride + 1)
            }
        }
        return DistortionMesh(positions: positions, uvs: uvs, indices: indices)
    }

    private static func calculateFieldOfView(
        viewer: ViewerParams,
        distortion: PolynomialRadialDistortion,
        screenWidthMeters: Float,
        screenHeightMeters: Float
    ) -> SIMD4<Float> {
        let deviceFov = viewer.leftEyeFieldOfViewAngles
        let toLens = viewer.screenToLensDistance
        let outerDistance = (screenWidthMeters - viewer.interLensDistance) / 2
        let innerDistance = viewer.interLensDistance / 2
        let bottomDistance = yEyeOffsetMeters(viewer: viewer, screenHeightMeters: screenHeightMeters)
        let topDistance = screenHeightMeters - bottomDistance

        let outerAngle = atan(distortion.distort(SIMD2(outerDistance / toLens, 0)).x)
        let innerAngle = atan(distortion.distort(SIMD2(innerDistance / toLens, 0)).x)
        let bottomAngle = atan(distortion.distort(SIMD2(0, bottomDistance / toLens)).y)
        let topAngle = atan(distortion.distort(SIMD2(0, topDistance / toLens)).y)
        let deg2Rad = Float.pi / 180

        return SIMD4(
            min(outerAngle, deviceFov[0] * deg2Rad),
            min(innerAngle, deviceFov[1] * deg2Rad),
            min(bottomAngle, deviceFov[2] * deg2Rad),
            min(topAngle, deviceFov[3] * deg2Rad)
        )
    }

    private static func yEyeOffsetMeters(viewer: ViewerParams, screenHeightMeters: Float) -> Float {
        switch viewer.verticalAlignment {
        case .bottom:
            return viewer.trayToLensDistance - defaultBorderSizeMeters
        case .top:
            return screenHeightMeters - viewer.trayToLensDistance - defaultBorderSizeMeters
        case .center:
            return screenHeightMeters / 2
        }
    }

    private func viewports(_ eye: Eye) -> (Viewport, Viewport) {
        let toLens = viewer.screenToLensDistance
        let eyeCenterMeters = eye == .left
            ? (screenWidthMeters - viewer.interLensDistance) / 2
            : (screenWidthMeters + viewer.interLensDistance) / 2
        let screen = Viewport(
            width: screenWidthMeters / toLens,
            height: screenHeightMeters / toLens,
            xEyeOffset: eyeCenterMeters / toLens,
            yEyeOffset: Self.yEyeOffsetMeters(viewer: viewer, screenHeightMeters: screenHeightMeters) / toLens
        )
        let fov = fieldOfView[eye.rawValue]
        let texture = Viewport(
            width: tan(fov.x) + tan(fov.y),
            height: tan(fov.z) + tan(fov.w),
            xEyeOffset: tan(fov.x),
            yEyeOffset: tan(fov.z)
        )
        return (screen, texture)
    }
}

// Cardboard's alignment marker: a thin line up from the bottom center of the screen between the two
// eye images (AlignmentMarker.cs, 2 dp wide and 24 mm tall).
enum AlignmentMarker {
    static let defaultHeightMeters: Float = 0.024
    static let defaultWidthDp: Float = 2.0
    private static let metersPerInch: Float = 0.0254
    private static let dpPerInch: Float = 160.0

    static func makeMesh(
        screenPixels: SIMD2<Int>,
        screenMeters: SIMD2<Float>,
        heightMeters: Float = defaultHeightMeters,
        widthDp: Float = defaultWidthDp
    ) -> DistortionMesh {
        let dpiX = Float(screenPixels.x) / (screenMeters.x / metersPerInch)
        let pixelsPerMeterY = Float(screenPixels.y) / screenMeters.y
        let widthPixels = max(1, Int(widthDp * dpiX / dpPerInch))
        let heightPixels = min(max(1, Int(heightMeters * pixelsPerMeterY)), screenPixels.y)

        let left = (screenPixels.x - widthPixels) / 2
        let leftNdc = Float(left) * 2 / Float(screenPixels.x) - 1
        let rightNdc = Float(left + widthPixels) * 2 / Float(screenPixels.x) - 1
        let topNdc = Float(heightPixels) * 2 / Float(screenPixels.y) - 1

        return DistortionMesh(
            positions: [
                SIMD2(leftNdc, -1),
                SIMD2(rightNdc, -1),
                SIMD2(leftNdc, topNdc),
                SIMD2(rightNdc, topNdc),
            ],
            uvs: [
                SIMD2(0, 0),
                SIMD2(1, 0),
                SIMD2(0, 1),
                SIMD2(1, 1),
            ],
            indices: [0, 2, 1, 1, 2, 3]
        )
    }
}

// Physical landscape screen size in meters, matching ScreenMetrics.cs and Unity's DeviceInfo.cpp.
enum ScreenMetrics {
    private static let metersPerInch: Float = 0.0254

    static func landscapePixels() -> SIMD2<Int> {
        let native = UIScreen.main.nativeBounds.size
        let w = Int(max(native.width, native.height))
        let h = Int(min(native.width, native.height))
        return SIMD2(max(w, 1), max(h, 1))
    }

    static func screenSizeMeters() -> SIMD2<Float> {
        let pixels = landscapePixels()
        let dpi = deviceDpi(landscapePixels: pixels)
        return SIMD2(
            Float(pixels.x) / dpi * metersPerInch,
            Float(pixels.y) / dpi * metersPerInch
        )
    }

    // Matches Unity's UnityDeviceDPI() in Classes/Unity/DeviceInfo.cpp for iPhones.
    private static func deviceDpi(landscapePixels: SIMD2<Int>) -> Float {
        var sysinfo = utsname()
        uname(&sysinfo)
        let identifier = withUnsafePointer(to: &sysinfo.machine) {
            $0.withMemoryRebound(to: CChar.self, capacity: 1) {
                String(validatingUTF8: $0) ?? ""
            }
        }
        // Mini OLED models are 476 ppi; Plus/Max OLED models up to 13 Pro Max / 14 Plus are 458 ppi;
        // iPhone 12/13/14/15/16/17 series (including 15/16 Pro & Pro Max) are 460 ppi.
        if identifier == "iPhone13,1" || identifier == "iPhone14,4" {
            return 476.0
        }
        switch identifier {
        case "iPhone10,3", "iPhone10,6", "iPhone11,2", "iPhone11,4", "iPhone11,6",
             "iPhone12,3", "iPhone12,5", "iPhone13,4", "iPhone14,3", "iPhone14,8":
            return 458.0
        case "iPhone10,4", "iPhone11,8", "iPhone12,1", "iPhone12,8", "iPhone14,6":
            return 326.0
        default:
            break
        }
        if UIScreen.main.nativeScale >= 3.0 {
            return 460.0
        }
        return Float(163.0 * UIScreen.main.nativeScale)
    }
}
