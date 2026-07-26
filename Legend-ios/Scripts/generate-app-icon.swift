#!/usr/bin/swift

import AppKit
import CoreGraphics
import Foundation

private let iconSize = 1024
private let targetShieldCoverage: CGFloat = 0.735
private let alphaThreshold: UInt8 = 4

private let scriptDirectory = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
private let projectDirectory = scriptDirectory.deletingLastPathComponent()
private let sourceURL = projectDirectory
    .appendingPathComponent("../AgentPortal/wwwroot/images/company-icons/legend-protect-transparent.png")
    .standardizedFileURL
private let destinationURL = projectDirectory
    .appendingPathComponent("Legend/Resources/Assets.xcassets/AppIcon.appiconset/AppIcon-1024.png")

guard let sourceImage = NSImage(contentsOf: sourceURL),
      let sourceCGImage = sourceImage.cgImage(forProposedRect: nil, context: nil, hints: nil),
      let croppedShield = cropTransparentPadding(from: sourceCGImage) else {
    fputs("Unable to load the Legend transparent shield artwork.\n", stderr)
    exit(1)
}

guard let context = CGContext(
    data: nil,
    width: iconSize,
    height: iconSize,
    bitsPerComponent: 8,
    bytesPerRow: iconSize * 4,
    space: CGColorSpaceCreateDeviceRGB(),
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
    fputs("Unable to create the AppIcon drawing context.\n", stderr)
    exit(1)
}

context.setFillColor(CGColor(red: 14 / 255, green: 27 / 255, blue: 61 / 255, alpha: 1))
context.fill(CGRect(x: 0, y: 0, width: iconSize, height: iconSize))

let scale = CGFloat(iconSize) * targetShieldCoverage / max(CGFloat(croppedShield.width), CGFloat(croppedShield.height))
let targetSize = CGSize(
    width: CGFloat(croppedShield.width) * scale,
    height: CGFloat(croppedShield.height) * scale)
let targetRect = CGRect(
    x: (CGFloat(iconSize) - targetSize.width) / 2,
    y: (CGFloat(iconSize) - targetSize.height) / 2,
    width: targetSize.width,
    height: targetSize.height)

context.interpolationQuality = .high
context.draw(croppedShield, in: targetRect)

guard let iconImage = context.makeImage(),
      let pngData = NSBitmapImageRep(cgImage: iconImage).representation(using: .png, properties: [:]) else {
    fputs("Unable to encode the AppIcon PNG.\n", stderr)
    exit(1)
}

try FileManager.default.createDirectory(at: destinationURL.deletingLastPathComponent(), withIntermediateDirectories: true)
try pngData.write(to: destinationURL, options: .atomic)
print("Generated \(destinationURL.path)")

private func cropTransparentPadding(from image: CGImage) -> CGImage? {
    let width = image.width
    let height = image.height
    var pixels = [UInt8](repeating: 0, count: width * height * 4)

    guard let context = CGContext(
        data: &pixels,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        return nil
    }

    context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))

    var minX = width
    var minY = height
    var maxX = -1
    var maxY = -1

    for y in 0..<height {
        for x in 0..<width where pixels[(y * width + x) * 4 + 3] > alphaThreshold {
            minX = min(minX, x)
            minY = min(minY, y)
            maxX = max(maxX, x)
            maxY = max(maxY, y)
        }
    }

    guard maxX >= minX, maxY >= minY else { return nil }
    return image.cropping(to: CGRect(
        x: minX,
        y: height - maxY - 1,
        width: maxX - minX + 1,
        height: maxY - minY + 1))
}
