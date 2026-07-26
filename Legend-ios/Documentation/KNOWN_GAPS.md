# Known Gaps

## Blocking gaps

| Gap | Why it blocks live mobile behavior | Required owner/action |
| --- | --- | --- |
| No native OAuth application contract | iOS cannot perform a legitimate PKCE token flow | Identity/platform owner registers public native client and documents redirect/audience/scopes |
| No bearer-authenticated mobile API | Existing JSON endpoints require browser cookie + anti-forgery context | Backend owner exposes versioned mobile endpoints that call existing services |
| No native messaging realtime decision | Browser hub does not declare native bearer handshake behavior | Platform owner decides bearer SignalR or APNs + reconciliation path |
| No API contract schema/versioning | Swift cannot safely decode undocumented web response shapes | Backend owner publishes DTO/OpenAPI or equivalent versioned contract |
| No Xcode installation in workspace | Native target cannot be compiled, simulator-tested, or archived locally | Install/select full Xcode on build Mac |

## Explicit non-solutions

- A WebView is not a substitute for a native mobile auth/API contract.
- Reusing a browser cookie is not a secure native session.
- Adding anonymous/weakly protected endpoints is not an acceptable bridge.
- Rendering mocked conversations as if live is not acceptable.
- Moving business authorization into Swift is not acceptable.

## Foundation completed despite gaps

- isolated `Legend-ios` project structure;
- configuration validator with no default live authority;
- PKCE/system-browser abstraction ready for approved endpoints;
- Keychain-backed token store abstraction;
- typed messaging DTO/protocol boundary and no-data unavailable state;
- deterministic tests for configuration, identity, redaction, and navigation;
- architecture, authorization, API, security, build, and release documents.

The completion items become live only after contract configuration and end-to-end
staging verification. They do not mean production connectivity is claimed.
