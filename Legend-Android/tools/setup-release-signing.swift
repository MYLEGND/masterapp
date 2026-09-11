#!/usr/bin/swift
import Foundation
import AppKit
import Security

let service = "com.mylegnd.legend.android.release"
let alias = "legend-upload-2026"
let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8)); exit(1)
}
func password(_ variable: String, _ title: String) -> String {
    if let value = ProcessInfo.processInfo.environment[variable], !value.isEmpty { return value }
    NSApplication.shared.setActivationPolicy(.accessory)
    NSApplication.shared.activate(ignoringOtherApps: true)
    let alert = NSAlert()
    alert.messageText = title
    alert.informativeText = "One-time setup for LEGEND Android release signing. This password will be saved in your macOS login Keychain, never in project files."
    let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 360, height: 24))
    alert.accessoryView = field
    alert.addButton(withTitle: "Save securely")
    alert.addButton(withTitle: "Cancel")
    alert.window.initialFirstResponder = field
    guard alert.runModal() == .alertFirstButtonReturn, !field.stringValue.isEmpty else { fail("Signing setup cancelled; no build was started.") }
    return field.stringValue
}
let store = password("LEGEND_STORE_PASSWORD", "LEGEND upload keystore password")
let key = password("LEGEND_KEY_PASSWORD", "LEGEND upload key password")
// Validate both passwords with a disposable signed JAR before saving anything.
let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
defer { try? FileManager.default.removeItem(at: temp) }
try Data("LEGEND signing check\n".utf8).write(to: temp.appendingPathComponent("check.txt"))
func run(_ executable: String, _ arguments: [String]) -> Bool {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = arguments
    var environment = ProcessInfo.processInfo.environment
    environment["LEGEND_STORE_PASSWORD"] = store
    environment["LEGEND_KEY_PASSWORD"] = key
    process.environment = environment
    process.standardOutput = FileHandle.nullDevice
    process.standardError = FileHandle.nullDevice
    do { try process.run(); process.waitUntilExit(); return process.terminationStatus == 0 } catch { return false }
}
guard run("/usr/bin/jar", ["--create", "--file", temp.appendingPathComponent("check.jar").path, "-C", temp.path, "check.txt"]),
      run("/usr/bin/jarsigner", ["-keystore", root.appendingPathComponent("Legend.jks").path, "-storepass:env", "LEGEND_STORE_PASSWORD", "-keypass:env", "LEGEND_KEY_PASSWORD", temp.appendingPathComponent("check.jar").path, alias]) else {
    fail("Signing verification failed. Check the passwords, Legend.jks, and the installed JDK. Keychain was not changed.")
}
for (account, value) in [("store-password", store), ("key-password", key)] {
    let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: service, kSecAttrAccount as String: account]
    let attributes: [String: Any] = [kSecValueData as String: Data(value.utf8)]
    var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
    if status == errSecItemNotFound {
        status = SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil)
    }
    guard status == errSecSuccess else { fail("Keychain could not save a signing credential (status \(status)).") }
}
print("Verified upload key and saved signing passwords in macOS Keychain. Future local release builds retrieve them automatically.")
