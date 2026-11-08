import Combine
import Foundation
import SwiftUI

/// Decides when a run of detector results is a person worth reporting. Pure value logic, no I/O:
/// a result is a hit when it has a person at `minimumConfidence` or better; the trigger fires when
/// `required` of the last `window` results are hits, then stays quiet for `cooldown` seconds.
/// A result with no person clears the window, so a fresh streak is needed after the person leaves.
struct DetectionDebouncer {
    let window: Int
    let required: Int
    let minimumConfidence: Float
    let cooldown: TimeInterval
    private var hits: [Bool] = []
    private var lastFired: TimeInterval?

    init(window: Int = 5, required: Int = 3, minimumConfidence: Float = 0.5, cooldown: TimeInterval = 30) {
        self.window = window
        self.required = required
        self.minimumConfidence = minimumConfidence
        self.cooldown = cooldown
    }

    /// Feed one detector result. `time` is monotonic seconds (for example `systemUptime`).
    /// Returns true exactly when the agent should be notified for this result.
    mutating func observe(count: Int, confidence: Float, at time: TimeInterval) -> Bool {
        guard count > 0 else {
            hits.removeAll()
            return false
        }
        hits.append(confidence >= minimumConfidence)
        if hits.count > window { hits.removeFirst(hits.count - window) }
        guard hits.filter({ $0 }).count >= required else { return false }
        if let lastFired, time - lastFired < cooldown { return false }
        lastFired = time
        hits.removeAll()
        return true
    }

    /// Clears the streak but keeps the cooldown, so toggling the link cannot cause a repeat post.
    mutating func reset() {
        hits.removeAll()
    }
}

enum AgentLinkStatus: Equatable {
    case off
    case idle
    case notifying
    case notified(caseID: Int?, newCase: Bool)
    case ignored(reason: String, caseID: Int?)
    case unreachable
    case badToken
    case invalidAddress
    case error(String)

    var text: String {
        switch self {
        case .off: return "Agent: off"
        case .idle: return "Agent: idle"
        case .notifying: return "Agent: notifying…"
        case .notified(let caseID, _): return caseID.map { "Agent: notified case \($0)" } ?? "Agent: notified"
        case .ignored(let reason, let caseID):
            return "Agent: ignored (\(reason))" + (caseID.map { ", case \($0)" } ?? "")
        case .unreachable: return "Agent: unreachable"
        case .badToken: return "Agent: bad token"
        case .invalidAddress: return "Agent: enter a valid agent URL"
        case .error(let detail): return "Agent: error (\(detail))"
        }
    }

    /// The lens HUD's Coordinator readout.
    var feed: FeedState {
        switch self {
        case .off: return .empty(value: "Offline", detail: "Agent link off")
        case .idle: return .empty(value: "Standing by", detail: "Agent not notified yet")
        case .notifying: return .ready(value: "Notifying", detail: "Person seen")
        case .notified(let caseID, let newCase):
            return .ready(value: caseID.map { "Case \($0)" } ?? "Notified",
                          detail: newCase ? "Triage started" : "Agent notified")
        case .ignored(let reason, let caseID):
            return .ready(value: caseID.map { "Case \($0)" } ?? "Notified",
                          detail: reason == "active_case" ? "Case already active" : "Agent ignored: \(reason)")
        case .unreachable: return .failed(value: "Unreachable", detail: "Check agent URL")
        case .badToken: return .failed(value: "Bad token", detail: "Re-enter agent token")
        case .invalidAddress: return .failed(value: "No address", detail: "Check agent URL")
        case .error(let detail): return .failed(value: "Agent error", detail: detail)
        }
    }
}

/// Tells the Nemotron agent (`POST /api/detections`) when the on-phone detector stably sees a person
/// on the robot feed. Networking never touches the detector or the renderer; failures only change `status`.
@MainActor
final class AgentLink: ObservableObject {
    /// Held in memory only, never persisted, like other control secrets.
    @Published var token = "" {
        didSet {
            let had = !oldValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let has = !trimmedToken.isEmpty
            if has, !had { enabled = true }
            if !has { enabled = false }
        }
    }
    @Published var enabled = false {
        didSet {
            guard enabled != oldValue else { return }
            debouncer.reset()
            if task == nil { status = enabled && !trimmedToken.isEmpty ? .idle : .off }
        }
    }
    @Published private(set) var status = AgentLinkStatus.off

    static let port = 8788
    private var debouncer = DetectionDebouncer()
    private var task: Task<Void, Never>?
    private let session: URLSession

    init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3
        config.timeoutIntervalForResource = 3
        config.waitsForConnectivity = false
        session = URLSession(configuration: config)
    }

    var coordinatorFeed: FeedState { status.feed }

    private var trimmedToken: String { token.trimmingCharacters(in: .whitespacesAndNewlines) }

    /// `http://<camera host>:8788`, the agent address used while the agent URL field is blank.
    static func defaultAddress(camera: String) -> String {
        guard var url = URLComponents(string: camera), let host = url.host, !host.isEmpty else {
            return "http://prada.local:\(port)"
        }
        url.scheme = "http"; url.port = port; url.path = ""; url.query = nil; url.fragment = nil
        url.user = nil; url.password = nil
        return url.string ?? "http://prada.local:\(port)"
    }

    /// Feed every detector result. `robotLive` is true only while the robot feed is the active,
    /// live source; the phone camera never notifies the agent.
    func observe(_ state: SurvivorDetectionState, address: String, camera: String, robotLive: Bool) {
        guard enabled, !trimmedToken.isEmpty, robotLive else {
            debouncer.reset()
            return
        }
        var count = 0
        var confidence: Float = 0
        if case .ready(let c, let conf) = state {
            count = c
            confidence = conf
        }
        guard debouncer.observe(count: count, confidence: confidence, at: ProcessInfo.processInfo.systemUptime) else {
            return
        }
        let base = address.trimmingCharacters(in: .whitespacesAndNewlines)
        notify(base: base.isEmpty ? Self.defaultAddress(camera: camera) : base,
               count: count, confidence: confidence)
    }

    private func notify(base: String, count: Int, confidence: Float) {
        guard task == nil else { return }
        guard let root = URL(string: base), ["http", "https"].contains(root.scheme ?? ""), root.host != nil else {
            status = .invalidAddress
            return
        }
        let body: [String: Any] = [
            "source": "visor",
            "confidence": Double(min(max(confidence, 0), 1)),
            "count": count,
            // The detector does not publish the box yet; the agent accepts null.
            "box": NSNull(),
            "frame_time": Int64(Date().timeIntervalSince1970 * 1000),
        ]
        guard let payload = try? JSONSerialization.data(withJSONObject: body) else { return }
        var request = URLRequest(url: root.appendingPathComponent("api/detections"))
        request.httpMethod = "POST"
        request.timeoutInterval = 3
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(trimmedToken)", forHTTPHeaderField: "Authorization")
        request.httpBody = payload

        status = .notifying
        task = Task { [weak self] in
            guard let self else { return }
            let result = await self.post(request)
            self.task = nil
            self.status = result
        }
    }

    /// One attempt plus at most two retries, 1 s apart, for transport errors and 5xx only.
    private func post(_ request: URLRequest) async -> AgentLinkStatus {
        var attempt = 0
        while true {
            let outcome: AgentLinkStatus
            var retryable = false
            do {
                let (data, response) = try await session.data(for: request)
                let code = (response as? HTTPURLResponse)?.statusCode ?? 0
                let reply = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                switch code {
                case 200, 202:
                    if let reason = reply?["ignored"] as? String {
                        outcome = .ignored(reason: reason, caseID: reply?["case_id"] as? Int)
                    } else {
                        outcome = .notified(caseID: reply?["case_id"] as? Int,
                                            newCase: reply?["new_case"] as? Bool ?? (code == 202))
                    }
                case 401, 403:
                    outcome = .badToken
                case 500...599:
                    outcome = .error(reply?["error"] as? String ?? "HTTP \(code)")
                    retryable = true
                default:
                    outcome = .error(reply?["error"] as? String ?? "HTTP \(code)")
                }
            } catch {
                outcome = .unreachable
                retryable = !(error is CancellationError)
            }
            guard retryable, attempt < 2, !Task.isCancelled else { return outcome }
            attempt += 1
            try? await Task.sleep(nanoseconds: 1_000_000_000)
        }
    }
}

enum AgentField: Hashable {
    case address
    case token
}

/// Settings rows for the agent link, styled like the camera URL field.
struct AgentSettings: View {
    @ObservedObject var link: AgentLink
    @Binding var address: String
    let cameraAddress: String
    let focus: FocusState<AgentField?>.Binding

    var body: some View {
        VStack(alignment: .leading, spacing: VisorToken.spaceXs) {
            Text("Agent URL")
                .font(VisorToken.label(14))
                .foregroundStyle(VisorToken.muted.color)
            TextField(AgentLink.defaultAddress(camera: cameraAddress), text: $address)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
                .textContentType(.URL)
                .submitLabel(.done)
                .modifier(AgentFieldStyle(focused: focus.wrappedValue == .address))
                .focused(focus, equals: .address)
            Text("Agent token")
                .font(VisorToken.label(14))
                .foregroundStyle(VisorToken.muted.color)
            SecureField("Not saved", text: $link.token)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.done)
                .modifier(AgentFieldStyle(focused: focus.wrappedValue == .token))
                .focused(focus, equals: .token)
            Toggle("Notify agent", isOn: $link.enabled)
                .font(VisorToken.label(16))
                .foregroundStyle(VisorToken.ink.color)
                .tint(VisorToken.ink.color)
                .disabled(link.token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            Text(link.status.text)
                .font(VisorToken.body(16))
                .foregroundStyle(VisorToken.ink2.color)
                .frame(minHeight: 22, alignment: .leading)
        }
    }
}

private struct AgentFieldStyle: ViewModifier {
    let focused: Bool

    func body(content: Content) -> some View {
        content
            .font(VisorToken.measured(16))
            .foregroundStyle(VisorToken.ink.color)
            .tint(VisorToken.ink.color)
            .padding(.horizontal, VisorToken.spaceSm)
            .frame(height: VisorToken.controlHeight)
            .background(VisorToken.paper2.color)
            .overlay(Rectangle().strokeBorder(VisorToken.rule.color, lineWidth: VisorToken.hairline))
            .overlay(
                Rectangle()
                    .strokeBorder(VisorToken.focus.color, lineWidth: 2)
                    .padding(-4)
                    .opacity(focused ? 1 : 0)
                    .animation(nil, value: focused)
            )
    }
}
