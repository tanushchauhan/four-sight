import Combine
import Foundation
import SwiftUI
import AVFoundation
import Speech

@MainActor
final class CoordinatorAudio: ObservableObject {
    @Published private(set) var listening = false
    @Published private(set) var handsFree = false
    @Published private(set) var transcript = ""
    @Published private(set) var status = "Coordinator voice off"
    private let speaker = AVSpeechSynthesizer()
    private let engine = AVAudioEngine()
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    private var recognition: SFSpeechRecognitionTask?
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var poll: Task<Void, Never>?
    private var timeout: Task<Void, Never>?
    private var silence: Task<Void, Never>?
    private var sending: Task<Void, Never>?
    private var lastID = 0
    private var lastLineTime: Double = 0
    private var base = ""
    private var tapped = false
    private var authorizing = false
    private var active = false
    private var waitingReply = false
    private var replyDeadline = Date.distantPast
    private var nextListen = Date.distantPast
    private var captureID = UUID()

    struct Line: Decodable {
        let id: Int
        let text: String
        let interrupt: Bool
        let time: Double
        let source: String?
    }

    func start(camera: String) {
        guard poll == nil else { return }
        active = true
        let endpoint = AgentLink.defaultAddress(camera: camera)
        if base != endpoint { lastID = 0 }
        base = endpoint
        poll = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                do {
                    guard let url = URL(string: self.base + "/api/operator/recent?n=20") else { throw URLError(.badURL) }
                    var query = URLRequest(url: url)
                    query.timeoutInterval = 3
                    let (data, response) = try await URLSession.shared.data(for: query)
                    guard !Task.isCancelled else { return }
                    guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
                    let lines = try JSONDecoder().decode([Line].self, from: data)
                    // Agent restarts reset feed IDs; don't wait for them to exceed an old session's IDs.
                    if lines.contains(where: { $0.id <= self.lastID && $0.time > self.lastLineTime }) { self.lastID = 0 }
                    if self.listening && lines.contains(where: { $0.id > self.lastID && $0.source == "handoff" && Date().timeIntervalSince1970 * 1000 - $0.time < 20_000 }) {
                        self.endCapture()
                        self.nextListen = Date().addingTimeInterval(0.5)
                    }
                    if !self.listening {
                        for line in lines.sorted(by: { $0.id < $1.id }) where line.id > self.lastID {
                            self.lastID = line.id
                            self.lastLineTime = max(self.lastLineTime, line.time)
                            guard Date().timeIntervalSince1970 * 1000 - line.time < 20_000 else { continue }
                            if line.source == "conversation" {
                                guard self.waitingReply else { continue }
                                self.waitingReply = false
                            }
                            if line.interrupt { self.speaker.stopSpeaking(at: .immediate) }
                            try self.configureAudio()
                            let utterance = AVSpeechUtterance(string: line.text)
                            utterance.voice = AVSpeechSynthesisVoice(language: "en-US")
                            utterance.rate = AVSpeechUtteranceDefaultSpeechRate
                            utterance.volume = 0.75
                            self.speaker.speak(utterance)
                            self.nextListen = Date().addingTimeInterval(0.5)
                            self.status = "Nemotron speaking"
                        }
                    }
                    if self.waitingReply && Date() > self.replyDeadline {
                        self.waitingReply = false
                        self.status = "No reply received — try again"
                        self.nextListen = Date().addingTimeInterval(1)
                    }
                    if self.handsFree && !self.listening && !self.waitingReply && !self.authorizing && !self.speaker.isSpeaking && Date() > self.nextListen {
                        self.beginListening()
                    } else if !self.handsFree && !self.listening && !self.waitingReply && !self.speaker.isSpeaking {
                        self.status = "Coordinator connected · alerts only"
                    }
                } catch {
                    if !Task.isCancelled && !self.listening { self.status = "Coordinator reconnecting…" }
                }
                try? await Task.sleep(nanoseconds: 400_000_000)
            }
        }
    }

    private func configureAudio() throws {
        let audio = AVAudioSession.sharedInstance()
        try audio.setCategory(.playAndRecord, mode: .voiceChat, options: [.defaultToSpeaker, .allowBluetooth])
        try audio.setActive(true)
    }

    func toggleConversation() {
        if handsFree {
            handsFree = false
            waitingReply = false
            sending?.cancel(); sending = nil
            endCapture()
            speaker.stopSpeaking(at: .immediate)
            status = "Conversation ended · alerts remain on"
        } else {
            handsFree = true
            waitingReply = false
            beginListening()
        }
    }

    func beginListening() {
        guard active, !listening, !authorizing, !waitingReply else { return }
        authorizing = true
        captureID = UUID()
        let id = captureID
        SFSpeechRecognizer.requestAuthorization { [weak self] authorization in
            AVAudioSession.sharedInstance().requestRecordPermission { granted in
                Task { @MainActor in
                    guard let self else { return }
                    self.authorizing = false
                    guard self.active, self.captureID == id else { return }
                    guard authorization == .authorized, granted else {
                        self.handsFree = false
                        self.status = "Allow microphone and speech access in Settings"; return
                    }
                    self.capture()
                }
            }
        }
    }

    private func capture() {
        guard let recognizer, recognizer.isAvailable else {
            handsFree = false; status = "Speech recognition unavailable"; return
        }
        speaker.stopSpeaking(at: .immediate)
        transcript = ""
        let id = captureID
        do {
            try configureAudio()
            let request = SFSpeechAudioBufferRecognitionRequest()
            request.shouldReportPartialResults = true
            self.request = request
            let input = engine.inputNode
            let format = input.outputFormat(forBus: 0)
            guard format.sampleRate > 0 else { throw URLError(.cannotLoadFromNetwork) }
            input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in request.append(buffer) }
            tapped = true
            listening = true
            recognition = recognizer.recognitionTask(with: request) { [weak self] result, error in
                Task { @MainActor in
                    guard let self, self.listening, self.captureID == id else { return }
                    if let result {
                        let text = result.bestTranscription.formattedString
                        if text != self.transcript {
                            self.transcript = text
                            self.silence?.cancel()
                            self.silence = Task { [weak self] in
                                try? await Task.sleep(nanoseconds: 1_200_000_000)
                                guard !Task.isCancelled, let self, self.captureID == id else { return }
                                self.sendCommand()
                            }
                        }
                    }
                    if result?.isFinal == true { self.sendCommand() }
                    else if error != nil {
                        self.endCapture(); self.status = "Speech interrupted — listening again shortly"
                        self.nextListen = Date().addingTimeInterval(1)
                    }
                }
            }
            engine.prepare()
            try engine.start()
            status = handsFree ? "Listening · pause to send" : "Listening · pause or tap Send"
            timeout = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 20_000_000_000)
                guard !Task.isCancelled, let self, self.captureID == id else { return }
                self.sendCommand()
            }
        } catch { endCapture(); handsFree = false; status = "Microphone unavailable" }
    }

    private func endCapture() {
        listening = false
        captureID = UUID()
        timeout?.cancel(); timeout = nil
        silence?.cancel(); silence = nil
        engine.stop()
        if tapped { engine.inputNode.removeTap(onBus: 0); tapped = false }
        request?.endAudio(); request = nil
        recognition?.cancel(); recognition = nil
    }

    func sendCommand() {
        guard listening else { return }
        let text = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        endCapture()
        nextListen = Date().addingTimeInterval(0.7)
        guard !text.isEmpty else { status = "No speech heard"; return }
        waitingReply = true
        replyDeadline = Date().addingTimeInterval(35)
        status = "Waiting for Nemotron…"
        sending = Task {
            do {
                guard let url = URL(string: base + "/api/operator/command") else { throw URLError(.badURL) }
                var command = URLRequest(url: url)
                command.httpMethod = "POST"
                command.timeoutInterval = 5
                command.setValue("application/json", forHTTPHeaderField: "Content-Type")
                command.httpBody = try JSONSerialization.data(withJSONObject: ["text": text])
                let (_, response) = try await URLSession.shared.data(for: command)
                guard !Task.isCancelled else { return }
                guard (response as? HTTPURLResponse)?.statusCode == 202 else { throw URLError(.badServerResponse) }
            } catch {
                guard !Task.isCancelled else { return }
                waitingReply = false
                status = "Could not reach coordinator — try again"
            }
        }
    }

    func stop() {
        active = false; handsFree = false; waitingReply = false
        sending?.cancel(); sending = nil
        poll?.cancel(); poll = nil
        endCapture()
        speaker.stopSpeaking(at: .immediate)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}

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
    @Published var enabled = false {
        didSet {
            guard enabled != oldValue else { return }
            debouncer.reset()
            if task == nil { status = enabled ? .idle : .off }
        }
    }
    @Published private(set) var status = AgentLinkStatus.off

    static let port = 8788
    private var debouncer = DetectionDebouncer(cooldown: 5)
    private var task: Task<Void, Never>?
    private let session: URLSession

    init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3
        config.timeoutIntervalForResource = 3
        config.waitsForConnectivity = false
        session = URLSession(configuration: config)
        enabled = true
        status = .idle
    }

    static var configuration: [String: String] {
        var values: [String: String] = [:]
        if let url = Bundle.main.url(forResource: "AgentConfiguration", withExtension: "plist"),
           let data = try? Data(contentsOf: url),
           let decoded = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: String] {
            values = decoded
        }
        for key in ["AGENT_URL"] {
            if let value = ProcessInfo.processInfo.environment[key] { values[key] = value }
        }
        return values
    }

    var coordinatorFeed: FeedState { status.feed }


    /// `http://<camera host>:8788`, the agent address used while the agent URL field is blank.
    static func defaultAddress(camera: String) -> String {
        if let configured = configuration["AGENT_URL"], !configured.isEmpty { return configured }
        guard var url = URLComponents(string: camera), let host = url.host, !host.isEmpty else {
            return "http://prada.local:\(port)"
        }
        url.scheme = "http"; url.port = port; url.path = ""; url.query = nil; url.fragment = nil
        url.user = nil; url.password = nil
        return url.string ?? "http://prada.local:\(port)"
    }

    /// Feed every detector result. `robotLive` is true only while the robot feed is the active,
    /// live source; the phone camera never notifies the agent.
    func observe(_ state: SurvivorDetectionState, address: String, camera: String, robotLive: Bool,
                 insight: SurvivorInsight = .empty) {
        guard enabled, robotLive else {
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
        let base = Self.configuration["AGENT_URL"] ?? ""
        notify(base: base.isEmpty ? Self.defaultAddress(camera: camera) : base,
               count: count, confidence: confidence, insight: insight)
    }

    private func notify(base: String, count: Int, confidence: Float, insight: SurvivorInsight) {
        guard task == nil else { return }
        guard let root = URL(string: base), ["http", "https"].contains(root.scheme ?? ""), root.host != nil else {
            status = .invalidAddress
            return
        }
        var body: [String: Any] = [
            "source": "visor",
            "confidence": Double(min(max(confidence, 0), 1)),
            "count": count,
            // The detector does not publish the box yet; the agent accepts null.
            "box": NSNull(),
            "frame_time": Int64(Date().timeIntervalSince1970 * 1000),
        ]
        if let encoded = try? JSONEncoder().encode(insight.observations),
           let observations = try? JSONSerialization.jsonObject(with: encoded) {
            body["survivors"] = observations
        }
        body["motion"] = insight.motion.rawValue.lowercased()
        body["distance_estimate_m"] = insight.distanceMeters
        body["bearing_estimate_deg"] = insight.bearingDegrees
        body["model"] = "yolo26n-seg"
        guard let payload = try? JSONSerialization.data(withJSONObject: body) else { return }
        var request = URLRequest(url: root.appendingPathComponent("api/detections"))
        request.httpMethod = "POST"
        request.timeoutInterval = 3
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
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
            Toggle("Notify agent", isOn: $link.enabled)
                .font(VisorToken.label(16))
                .foregroundStyle(VisorToken.ink.color)
                .tint(VisorToken.ink.color)
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
