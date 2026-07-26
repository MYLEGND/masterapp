# Legend iOS

Legend iOS is the native SwiftUI client for MASTERAPP. It is deliberately
isolated in this directory and shares no UI runtime, WebView, cookie, or local
business-authority implementation with the web portals.

## Current implementation state

The native foundation, configuration validation, privacy-safe diagnostics,
authenticated-session architecture, and messaging presentation slice live here.
The existing backend currently has no approved native PKCE/bearer API contract.
Consequently, the app correctly presents a configuration/contract-unavailable
state instead of pretending an authenticated session or a message list exists.

The required contract and the exact verified backend gaps are in
[MASTERAPP_MOBILE_AUTHORITY_AUDIT.md](Documentation/MASTERAPP_MOBILE_AUTHORITY_AUDIT.md).

## Project structure

```
Legend-ios/
├── Legend.xcodeproj/         Native Xcode project
├── Legend/                   App target source
├── LegendTests/              Unit tests
├── LegendUITests/            UI tests
├── Configuration/            Value-free build configuration templates
└── Documentation/            Architecture, security, API, and release records
```

## Build prerequisites

- macOS with full Xcode installed (not Command Line Tools only)
- Xcode 16 or newer with an iOS 17+ simulator runtime
- A local, uncommitted configuration file populated from the templates only
  after the platform owner supplies the native identity/API contract

Open `Legend.xcodeproj`, select a `Legend` scheme, select a simulator, then
build and run. The checked-in configuration intentionally leaves all live
identity/API values absent.

## Security rules

- Do not add a WebView or reuse browser authentication cookies.
- Do not commit identity tenant values, client IDs, backend URLs, tokens,
  signing assets, or Apple team identifiers.
- Do not turn off server authorization, entitlement checks, anti-forgery
  protections, or role-aware messaging identity checks to support mobile.
- Do not configure production signing in source control.

See [BUILD_AND_SIGNING.md](Documentation/BUILD_AND_SIGNING.md) before running
on a device and [KNOWN_GAPS.md](Documentation/KNOWN_GAPS.md) before claiming a
server-connected release.
