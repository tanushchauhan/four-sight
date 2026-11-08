import SwiftUI
import UIKit

/// Blueprint, cyanotype sheet. Every colour, face, space and line weight in the visor comes from this block.
enum VisorToken {
    struct RGB {
        var r: Double
        var g: Double
        var b: Double
        var a: Double = 1
        var color: Color { Color(.sRGB, red: r, green: g, blue: b, opacity: a) }
        var uiColor: UIColor { UIColor(red: r, green: g, blue: b, alpha: a) }
        func opacity(_ value: Double) -> RGB { RGB(r: r, g: g, b: b, a: value) }
    }

    static let paper = oklch(0.35, 0.105, 256)
    static let paper2 = oklch(0.31, 0.10, 257)
    static let paper3 = oklch(0.265, 0.095, 258)
    static let ink = oklch(0.97, 0.012, 240)
    static let ink2 = oklch(0.90, 0.025, 242)
    static let muted = oklch(0.80, 0.045, 244)
    static let rule = oklch(0.93, 0.02, 240).opacity(0.85)
    static let rule2 = oklch(0.93, 0.02, 240).opacity(0.32)
    static let accentInk = oklch(0.33, 0.105, 256)
    static let focus = oklch(0.97, 0.012, 240)
    /// Behind type that sits over the camera, from the theme's paper-3 scrim.
    static let scrim = oklch(0.265, 0.095, 258).opacity(0.72)

    static let space2xs: CGFloat = 4
    static let spaceXs: CGFloat = 8
    static let spaceSm: CGFloat = 12
    static let spaceMd: CGFloat = 16
    static let spaceLg: CGFloat = 24
    static let spaceXl: CGFloat = 40
    static let hairline: CGFloat = 1
    static let heavy: CGFloat = 2
    static let keylineInset: CGFloat = 5
    static let controlHeight: CGFloat = 48

    static func display(_ size: CGFloat) -> Font {
        Font.custom("Chakra Petch", size: size).weight(.semibold)
    }

    static func body(_ size: CGFloat) -> Font {
        Font.custom("IBM Plex Sans", size: size).weight(.regular)
    }

    static func label(_ size: CGFloat) -> Font {
        Font.custom("IBM Plex Sans", size: size).weight(.medium)
    }

    static func measured(_ size: CGFloat) -> Font {
        Font.custom("IBM Plex Mono", size: size).weight(.medium)
    }

    private static func oklch(_ L: Double, _ C: Double, _ H: Double) -> RGB {
        let h = H * .pi / 180
        let a = C * cos(h)
        let b = C * sin(h)
        let l_ = L + 0.3963377774 * a + 0.2158037573 * b
        let m_ = L - 0.1055613458 * a - 0.0638541728 * b
        let s_ = L - 0.0894841775 * a - 1.2914855480 * b
        let l = l_ * l_ * l_
        let m = m_ * m_ * m_
        let s = s_ * s_ * s_
        let red = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
        let green = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
        let blue = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
        func encode(_ channel: Double) -> Double {
            let clamped = min(max(channel, 0), 1)
            if clamped <= 0.0031308 { return 12.92 * clamped }
            return 1.055 * pow(clamped, 1 / 2.4) - 0.055
        }
        return RGB(r: encode(red), g: encode(green), b: encode(blue))
    }
}
