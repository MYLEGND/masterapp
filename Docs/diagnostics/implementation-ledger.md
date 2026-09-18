# Founder diagnostics integration — 2026-09-18

Base: c3009bfb346a0b08f718f4a245ec89d7259d5071, current release branch.
Worktree: /private/tmp/masterapp-founder-diagnostics-20260918.
Main checkout and translation repairs remain untouched.

## Ownership
- Lead: shared server capture integration, application startup registrations, project discovery, existing remediation release contract, integration verification.
- Backend specialist: shared diagnostic contract, durable incident persistence and migration, ingestion, Founder management controller/view, focused backend tests.
- Web specialist: existing Page Health observer, privacy of error responses/views, Founder-only navigation, browser tests.
- Native specialist: iOS/Android existing diagnostics and transport integration, bounded capture/replay, native tests.

## Agreed contract
- Shared.Diagnostics.RuntimeDiagnosticEvent: camelCase JSON properties appIdentifier, platform, route, sourceFilePath, errorName, errorMessage, stackTrace, gitCommitHash, timestamp; additional operation, correlationId, category, statusCode, appVersion permitted.
- Shared.Diagnostics.IRuntimeDiagnosticSink.RecordAsync(RuntimeDiagnosticEvent, CancellationToken): Task. Server capture uses DI; ingestion never invokes model/provider/release operations.
- POST /api/runtime-diagnostics: bounded untrusted observations; public web clients require antiforgery; native clients use the existing authenticated /api/v1/mobile/runtime-diagnostics adapter. No arbitrary raw content or cross-user reads. Server-derived identity and release metadata take precedence. Returns no diagnostics.
- Founder management route /founder/diagnostics with canonical Founder authorization and antiforgery on mutations.
- Collection and classification are separate from permission to stage/publish. Unknown observations never become confirmed defects automatically.
- Preserve existing LegendPageHealth.current callers; remove general-user diagnostic UI, not user-facing retry guidance.

## Safety and acceptance
No new paid services, no production mutations or deployment until verified. No model calls on ingestion. No raw message content, tokens, cookies, URL queries, or user identifiers in diagnostic payloads. No unbounded queue/retry. Existing release authority remains unique; no direct main merge or competing deployment workflow. New apps require discoverable project metadata and explicit deployment binding. Native fatal crashes require crash-safe capture, not synchronous network requests.

## Evidence / remaining work
Baseline inspected; current main checkout f79750e6 is older and unchanged. GitHub default production is protected at d411d2e2. Existing remediation trigger/reporting contract differs from workflow. Implementation is under verification; no production claims.

Initial verification: 57 .NET focused tests, 22 Node observer tests, four Python discovery tests and 30 Android tests passed. iOS unsigned build-for-testing and actual-source Swift checks passed. A later independent review identified revocation freshness, batch staging/publication isolation and handled mobile exception capture gaps; those corrections require the final combined rerun. Native builds are dirty candidate builds based on c3009bfb, not clean deployed-revision evidence.

Migration 20260918155442 generated; EF pending-model check passed. No production migration, model call, deployment, paid resource or account changes performed. See operating-boundaries.md for unfinished automation and release prerequisites.


## Integrated verification
- Full MASTERAPP.sln Release build: 0 warnings, 0 errors (45.99 seconds).
- Final affected-project rebuild after reconciliation/source-context review: 0 warnings, 0 errors (40.80 seconds).
- Final integrated .NET filter: 96 passed, 0 failed, 0 skipped (3 seconds). Includes privacy, antiforgery, capture failure preservation, concurrent revocation, batch branch isolation, stale replay, closed/deleted publication status and existing release workflow contracts.
- Browser observer: 22 passed; project discovery: 4 passed; native checkout/provenance guard: 24 passed.
- Android: 30 focused tests passed. iOS unsigned test build, actual-source Swift checks, normal-build provenance, intentional guard failure and recovery passed. Physical devices/live ingestion not exercised.
- New translation source catalog regenerated through existing authority, no provider calls. Route/placeholder-only diagnostics are excluded from translation acquisition.
- Independent review found and resolved stale revocation reads, stage/publication race, stale replay receipts, loss of executable file modes, telemetry resolution masking original exceptions and handled mobile faults bypassing capture.

## Not yet release-qualified
Automatic repair-model execution/sandbox reproduction and verified fix closure are not implemented. The existing GitHub-App publication author's eligibility under the unchanged protected workflow must be established live. Read-only reconciliation does not unlock ambiguous writes. Automatic batch archival/reset is not implemented. Impact discovery now runs inside the existing workflow but does not skip existing build/deploy gates. Static Legend-Website is discovered but lacks a same-origin authenticated/antiforgery ingestion integration; no unsafe cross-origin collector was introduced. Startup/OS/fatal crash coverage remains incomplete. These are explicit gaps, not passed acceptance claims.
