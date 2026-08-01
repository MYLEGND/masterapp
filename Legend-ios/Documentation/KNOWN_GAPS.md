# Known Gaps

## Release gates

| Gate | Why it matters | Required owner/action |
| --- | --- | --- |
| Native Entra deployment configuration | The checked-in project intentionally has no live client ID, redirect, scope, audience, or API URL. Both iOS and `MobileAuth` must agree before bearer validation can succeed. | Identity/platform owner configures the public native registration and deployment values through the approved secret/configuration path. |
| Staging end-to-end verification | Unit tests validate typed contracts, but they cannot prove a deployed Entra registration, DNS/TLS path, or production authorization data. | Release owner validates Agent and Client PKCE, session restore, role switching, all key API routes, and a rejected-token path in staging. |
| Mobile realtime policy | Foreground reconciliation is available; a native bearer SignalR handshake or APNs policy has not been adopted as the universal realtime channel. | Product/platform owner chooses and documents the supported notification/reconciliation model before relying on instant delivery. |
| App Store release evidence | A simulator build and automated tests do not substitute for signed-device, privacy-label, accessibility, and store-submission validation. | Release owner completes the checklist in `APP_STORE_READINESS.md` with the actual shipping configuration. |

## Explicit non-solutions

- A WebView is not a substitute for a native mobile auth/API contract.
- Reusing a browser cookie is not a secure native session.
- Adding anonymous/weakly protected endpoints is not an acceptable bridge.
- Rendering mocked conversations as if live is not acceptable.
- Moving business authorization into Swift is not acceptable.

## Foundation completed

- isolated `Legend-ios` project structure;
- configuration validator with no default live authority;
- PKCE/system-browser, Keychain token storage, 90-day checkpoint, and optional
  Face ID session protection;
- bearer-authenticated, role-aware mobile controllers in AgentPortal;
- typed native adapters for the implemented mobile features;
- deterministic tests for configuration, identity, launch cache, API failures,
  financial presentation, messaging, and navigation;
- architecture, authorization, API, security, build, and release documents.

These completed items do not claim production readiness. The release gates
above remain mandatory for every target environment.
