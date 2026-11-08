import Foundation
import Combine

// Latest-frame MJPEG receiver. Network callbacks run serially; render access is locked.
final class ARSessionController: NSObject, ObservableObject, URLSessionDataDelegate {
    @Published var status = "Disconnected"
    private(set) var lastFrameTime = Date.distantPast
    private let lock = NSLock()
    private var latest: Data?
    private var buffer = Data()
    private var connection: URLSession?
    private var task: URLSessionDataTask?
    private var endpoint: URL?
    private var stopped = true
    private var retry: DispatchWorkItem?

    func takeFrame() -> Data? {
        lock.lock(); defer { lock.unlock() }
        let result = latest; latest = nil; return result
    }
    func start(_ address: String = "http://prada.local:8080/stream") {
        pause()
        guard let url = URL(string: address), ["http", "https"].contains(url.scheme ?? ""), url.host != nil else {
            status = "Enter a valid camera URL"; return
        }
        endpoint = url; stopped = false; connect()
    }
    private func connect() {
        guard !stopped, let endpoint else { return }
        status = "Connecting…"
        buffer.removeAll()
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 10
        config.timeoutIntervalForResource = 86400
        connection = URLSession(configuration: config, delegate: self, delegateQueue: .main)
        task = connection?.dataTask(with: endpoint); task?.resume()
    }
    func pause() {
        stopped = true; retry?.cancel(); retry = nil
        task?.cancel(); task = nil
        connection?.invalidateAndCancel(); connection = nil
        lock.lock(); latest = nil; lock.unlock()
        lastFrameTime = .distantPast
        status = "Disconnected"
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard session === connection else { completionHandler(.cancel); return }
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            status = "Camera returned an HTTP error"; completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard session === connection else { return }
        buffer.append(data)
        while let start = buffer.range(of: Data([0xff, 0xd8])),
              let end = buffer.range(of: Data([0xff, 0xd9]), in: start.upperBound..<buffer.endIndex) {
            let jpeg = buffer.subdata(in: start.lowerBound..<end.upperBound)
            buffer.removeSubrange(buffer.startIndex..<end.upperBound)
            lock.lock(); latest = jpeg; lock.unlock()
            lastFrameTime = Date()
            status = "Live"
        }
        if buffer.count > 4 * 1024 * 1024 { buffer.removeAll() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard session === connection, !stopped else { return }
        status = "Feed disconnected; retrying…"
        session.finishTasksAndInvalidate()
        let work = DispatchWorkItem { [weak self] in self?.connect() }
        retry = work; DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }
}
