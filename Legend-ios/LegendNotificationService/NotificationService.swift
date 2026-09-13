import UserNotifications
import Intents
import ImageIO
import UIKit

/// The server owns sender identity, text, and unread count. iOS owns the
/// communication-notification layout, including the LEGEND application badge.
final class NotificationService: UNNotificationServiceExtension, URLSessionTaskDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var handler: ((UNNotificationContent) -> Void)?
    private var bestContent: UNNotificationContent?
    private var session: URLSession?

    override func didReceive(_ request: UNNotificationRequest,
                             withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) {
        guard let content = request.content.mutableCopy() as? UNMutableNotificationContent else {
            contentHandler(request.content)
            return
        }
        if let count = content.userInfo["unreadCount"] as? NSNumber {
            content.badge = NSNumber(value: max(0, count.intValue))
        }
        lock.lock()
        handler = contentHandler
        bestContent = content
        lock.unlock()
        guard let sender = content.userInfo["sender"] as? [String: String],
              let id = sender["id"], !id.isEmpty,
              let name = sender["name"], !name.isEmpty else {
            finish(content)
            return
        }
        let present: @Sendable (Data?) -> Void = { [weak self] data in
            let person = INPerson(personHandle: INPersonHandle(value: id, type: .unknown),
                                  nameComponents: nil, displayName: name,
                                  image: data.map { INImage(imageData: $0) },
                                  contactIdentifier: nil, customIdentifier: id)
            let intent = INSendMessageIntent(recipients: nil, outgoingMessageType: .outgoingMessageText,
                                            content: content.body, speakableGroupName: nil,
                                            conversationIdentifier: content.userInfo["conversationId"] as? String,
                                            serviceName: "LEGEND", sender: person, attachments: nil)
            if let data { intent.setImage(INImage(imageData: data), forParameterNamed: \.sender) }
            let interaction = INInteraction(intent: intent, response: nil)
            interaction.direction = .incoming
            interaction.donate { _ in }
            self?.finish((try? content.updating(from: intent)) ?? content)
        }
        guard let path = sender["imagePath"],
              path.hasPrefix("/api/v1/mobile/notifications/"), !path.contains("\\"),
              let base = Bundle.main.object(forInfoDictionaryKey: "LegendAPIBaseURL") as? String,
              let origin = URL(string: base), origin.scheme == "https", origin.host != nil,
              let url = URL(string: path, relativeTo: origin)?.absoluteURL,
              url.host == origin.host, url.scheme == origin.scheme else {
            present(nil)
            return
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 4
        configuration.timeoutIntervalForResource = 5
        configuration.urlCache = nil
        configuration.httpShouldSetCookies = false
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        self.session = session
        session.dataTask(with: url) { data, response, _ in
            guard let response = response as? HTTPURLResponse, response.statusCode == 200,
                  let data, data.count <= 3 * 1024 * 1024,
                  let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceThumbnailMaxPixelSize: 160,
                    kCGImageSourceCreateThumbnailWithTransform: true
                  ] as CFDictionary) else { present(nil); return }
            present(UIImage(cgImage: image).pngData())
        }.resume()
    }

    // Never forward a scoped image capability to a redirected origin.
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }

    private func finish(_ content: UNNotificationContent? = nil) {
        lock.lock()
        let callback = handler
        let result = content ?? bestContent
        handler = nil
        lock.unlock()
        if let callback, let result { callback(result) }
        session?.invalidateAndCancel()
    }

    override func serviceExtensionTimeWillExpire() { finish() }
}
