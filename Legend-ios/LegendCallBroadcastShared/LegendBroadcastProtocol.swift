import Foundation
import Network

// Transient IPC only. No account credentials, signaling, or media are persisted here.
enum LegendBroadcastProtocol {
    static let group = "group.com.mylegnd.legend.registered.call-broadcast"
    static let extensionID = "com.mylegnd.legend.registered.broadcast"
    static let maximumFrameBytes = 2_000_000
    static let maximumDimension = 1920
    struct Invitation: Codable {
        let nonce: String
        let socketName: String
        let expiresUtc: Date
        let width, height, fps: Int
        func validated(root: URL, now: Date = Date()) -> URL? {
            guard nonce.count == 64, nonce.allSatisfy({ $0.isHexDigit }),
                  socketName == "call.sock", expiresUtc > now,
                  expiresUtc.timeIntervalSince(now) <= 125,
                  (2...LegendBroadcastProtocol.maximumDimension).contains(width),
                  (2...LegendBroadcastProtocol.maximumDimension).contains(height),
                  (1...15).contains(fps) else { return nil }
            let url = root.appendingPathComponent(socketName)
            // Darwin sockaddr_un.sun_path contains 104 bytes including the terminator.
            return url.path.utf8.count < 104 ? url : nil
        }
    }
    static func root() throws -> URL {
        guard let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group) else {
            throw NSError(domain: "LegendBroadcast", code: 1,
                userInfo: [NSLocalizedDescriptionKey: "Screen sharing is unavailable in this app installation."])
        }
        return url
    }
    static func invitationURL(_ root: URL) -> URL { root.appendingPathComponent("invitation.json") }
    static func readExactly(_ count: Int, from connection: NWConnection,
                            completion: @escaping (Data?) -> Void) {
        guard count > 0, count <= maximumFrameBytes else { completion(nil); return }
        connection.receive(minimumIncompleteLength: count, maximumLength: count) { data, _, complete, error in
            completion(error == nil && data?.count == count ? data : nil)
        }
    }
    static func encodeHeader(size: Int, timestamp: Int64) -> Data {
        var length = UInt32(size).bigEndian
        var time = timestamp.bigEndian
        var data = withUnsafeBytes(of: &length) { Data($0) }
        data.append(withUnsafeBytes(of: &time) { Data($0) })
        return data
    }
    static func decodeHeader(_ data: Data) -> (size: Int, timestamp: Int64)? {
        guard data.count == 12 else { return nil }
        let size = data.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        let time = data.suffix(8).reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
        guard size > 0, size <= maximumFrameBytes, time <= UInt64(Int64.max) else { return nil }
        return (Int(size), Int64(time))
    }
}
