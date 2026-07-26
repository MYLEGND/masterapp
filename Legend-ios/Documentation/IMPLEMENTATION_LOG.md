# Implementation Log

## 2026-07-25 — Phase 0: authority-first native foundation

- Audited `AgentPortal`, `ClientApp`, `Infrastructure`, `Domain`, and `SHARED`
  for identity, messaging, Journey Circles, billing, finance, authentication,
  profile image, realtime, and persistence authority.
- Recorded that the current portals use browser OpenID Connect/cookie flows and
  anti-forgery-protected MVC/JSON endpoints; no native bearer API was found.
- Recorded the required `(Normalize(UserId), ParticipantType)` messaging
  identity rule and existing server authority services.
- Created the `Legend-ios` native app boundary, value-free configuration
  templates, Xcode project structure, source target, unit target, and UI target.
- Added a configuration-driven PKCE/browser-session architecture and
  Keychain token store abstraction. Missing config is surfaced explicitly.
- Added the messaging view-model/data-contract boundary without mock data or
  local authorization logic.
- Did not deploy, migrate production, change server authorization, commit, or
  push.

## Validation recorded

- `xcodebuild build` completed successfully for the `Legend` app target with a
  local placeholder bundle identifier and signing disabled.
- `xcodebuild build-for-testing` completed successfully for the app, unit-test,
  and UI-test targets.
- `simctl` cannot enumerate runtimes in this workspace because the host
  `CoreSimulatorService` refuses connections. Runtime test execution is blocked
  by that host limitation and has not been represented as a passing test run.
- `git diff --check` is run after patches.
