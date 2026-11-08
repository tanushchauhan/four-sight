import SwiftUI

/// One feed the lens layer can show. Replace `VisorBriefing.sample` when the live source exists.
enum FeedState: Equatable {
    case loading
    case empty(value: String, detail: String)
    case failed(value: String, detail: String)
    case ready(value: String, detail: String)
}

struct VisorBriefing: Equatable {
    var coordinator: FeedState
    var survivors: FeedState
    var slam: FeedState
    var camera: FeedState

    /// Placeholder until Nemotron, detection and SLAM are wired.
    static let sample = VisorBriefing(
        coordinator: .ready(value: "Nemotron", detail: "Holding the east search"),
        survivors: .ready(value: "2", detail: "Survivors detected"),
        slam: .ready(value: "Loaded", detail: "Map is current"),
        camera: .loading
    )
}

/// The layer is drawn in view angles, not screen points: the square image spans
/// ±`halfAngleDegrees` around each lens's optical axis, and the eye pass places it through the
/// same Cardboard frustum as the camera, so both eyes see it at the same place and the lens warp bends it.
enum LensLayer {
    static let halfAngleDegrees: Float = 34
    static let side: CGFloat = 1000

    @MainActor
    static func render(_ briefing: VisorBriefing) -> CGImage? {
        let renderer = ImageRenderer(content: LensLayerView(briefing: briefing).frame(width: side, height: side))
        renderer.scale = 1.2
        renderer.isOpaque = false
        return renderer.cgImage
    }
}

struct LensLayerView: View {
    let briefing: VisorBriefing

    var body: some View {
        ZStack {
            Reticle()
                .stroke(VisorToken.rule2.color, lineWidth: VisorToken.hairline)
                .frame(width: 40, height: 40)

            VStack(spacing: 0) {
                HStack(alignment: .top) {
                    Readout(label: "Coordinator", state: briefing.coordinator)
                    Spacer(minLength: VisorToken.spaceLg)
                    Readout(label: "SLAM", state: briefing.slam, trailing: true)
                }
                Spacer(minLength: 0)
                HStack(alignment: .lastTextBaseline) {
                    CameraLink(state: briefing.camera)
                    Spacer(minLength: VisorToken.spaceLg)
                    SurvivorCount(state: briefing.survivors)
                }
            }
            // Keep all readouts inside the lens's comfortable center. The warped eye viewport is
            // intentionally smaller than the phone half below, so edge labels need extra inset.
            .padding(.horizontal, 270)
            .padding(.vertical, 260)
        }
        .frame(width: LensLayer.side, height: LensLayer.side)
    }
}

private struct Reticle: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let gap: CGFloat = 8
        path.move(to: CGPoint(x: rect.minX, y: rect.midY))
        path.addLine(to: CGPoint(x: rect.midX - gap, y: rect.midY))
        path.move(to: CGPoint(x: rect.midX + gap, y: rect.midY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
        path.move(to: CGPoint(x: rect.midX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.midX, y: rect.midY - gap))
        path.move(to: CGPoint(x: rect.midX, y: rect.midY + gap))
        path.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
        return path
    }
}

/// White type straight on the feed. A tight offset shadow in the sheet's deepest blue keeps it
/// readable over a bright frame without putting a box behind it.
private extension View {
    func overFeed() -> some View {
        shadow(color: VisorToken.paper3.opacity(0.95).color, radius: 1.5, x: 0, y: 1)
            .shadow(color: VisorToken.paper3.opacity(0.5).color, radius: 5, x: 0, y: 2)
    }
}

private struct Readout: View {
    let label: String
    let state: FeedState
    var trailing = false

    var body: some View {
        VStack(alignment: trailing ? .trailing : .leading, spacing: VisorToken.space2xs) {
            Text(label)
                .font(VisorToken.label(20))
                .foregroundStyle(VisorToken.muted.color)
            switch state {
            case .loading:
                Text("Waiting")
                    .font(VisorToken.display(32))
                    .foregroundStyle(VisorToken.muted.color)
            case .empty(let value, let detail), .ready(let value, let detail):
                title(value)
                detailText(detail)
            case .failed(let value, let detail):
                title(value).underline(true, color: VisorToken.ink.color)
                detailText(detail)
            }
        }
        .multilineTextAlignment(trailing ? .trailing : .leading)
        .frame(maxWidth: 300, alignment: trailing ? .trailing : .leading)
        .overFeed()
    }

    private func title(_ value: String) -> some View {
        Text(value.uppercased())
            .font(VisorToken.display(32))
            .foregroundStyle(VisorToken.ink.color)
            .lineLimit(1)
            .minimumScaleFactor(0.6)
    }

    private func detailText(_ detail: String) -> some View {
        Text(detail)
            .font(VisorToken.body(20))
            .foregroundStyle(VisorToken.ink2.color)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// Survivors as a label at the bottom right: the count large, the words beside it.
private struct SurvivorCount: View {
    let state: FeedState

    var body: some View {
        HStack(alignment: .lastTextBaseline, spacing: VisorToken.spaceXs) {
            switch state {
            case .loading:
                Text("Survivors, waiting")
                    .font(VisorToken.label(22))
                    .foregroundStyle(VisorToken.muted.color)
            case .empty(let value, let detail), .ready(let value, let detail), .failed(let value, let detail):
                HStack(alignment: .lastTextBaseline, spacing: VisorToken.spaceXs) {
                    Text(value)
                        .font(VisorToken.display(56))
                        .foregroundStyle(VisorToken.ink.color)
                    Text(detail.uppercased())
                        .font(VisorToken.label(18))
                        .foregroundStyle(VisorToken.ink2.color)
                        .fixedSize()
                }
                .padding(.horizontal, detail.localizedCaseInsensitiveContains("alert") ? VisorToken.spaceSm : 0)
                .padding(.vertical, detail.localizedCaseInsensitiveContains("alert") ? VisorToken.spaceXs : 0)
                .background(detail.localizedCaseInsensitiveContains("alert") ? VisorToken.scrim.color : Color.clear)
                .overlay(
                    Rectangle()
                        .strokeBorder(VisorToken.rule.color, lineWidth: VisorToken.hairline)
                        .opacity(detail.localizedCaseInsensitiveContains("alert") ? 1 : 0)
                )
            }
        }
        .overFeed()
    }
}

private struct CameraLink: View {
    let state: FeedState

    var body: some View {
        Text(text)
            .font(VisorToken.measured(18))
            .foregroundStyle(VisorToken.muted.color)
            .overFeed()
    }

    private var text: String {
        switch state {
        case .loading: return "Camera connecting"
        case .empty(let value, _), .ready(let value, _), .failed(let value, _): return "Camera: " + value.lowercased()
        }
    }
}
