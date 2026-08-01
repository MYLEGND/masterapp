# Legend iOS

Legend iOS is the native SwiftUI client for MASTERAPP. It is deliberately
isolated in this directory and shares no UI runtime, WebView, cookie, or local
business-authority implementation with the web portals.

## Current implementation state

Legend iOS has a native PKCE/session foundation and a versioned,
bearer-authenticated mobile API surface in `AgentPortal/Mobile`. The server
derives the active Agent or Client identity from the validated bearer and keeps
all business authority in MASTERAPP. Native features currently consume typed
home, account, messaging, social, discovery, Journey Circles, financial, and
agent-workspace contracts.

Checked-in build settings deliberately contain no live identity or API values.
Until deployment or an ignored local configuration supplies the approved native
registration, the app presents its configuration-unavailable state rather than
falling back to browser cookies, a WebView, or mock authority.

The current transport expectations are in
[API_CONTRACTS.md](Documentation/API_CONTRACTS.md). The older
[MASTERAPP_MOBILE_AUTHORITY_AUDIT.md](Documentation/MASTERAPP_MOBILE_AUTHORITY_AUDIT.md)
is retained as a pre-implementation decision record, not as the current API
status.

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
