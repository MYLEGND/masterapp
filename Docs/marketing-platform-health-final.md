# Marketing Platform Health Final

Date: 2026-05-20  
Branch: `stabilization/marketing-platform-health`

## Scope Of This Pass

This pass prioritized measurement stability, taxonomy unification, life-funnel recovery, attribution truth, and operator-facing health visibility over new growth features.

## Before / After Health Scores

These scores are provisional engineering-readiness estimates based on code-path hardening, builds, and targeted tests. They are **not** live-traffic SLAs and should not be treated as production-certified until controlled QA and Meta Events Manager validation are completed.

| Area | Before | After | Basis |
|---|---:|---:|---|
| Tracking Reliability | 45 | 81 | Shared event catalog, browser allowlist sync, fail-loud tracking, retry/queue path, client diagnostics, non-Life parity contracts |
| Attribution Accuracy | 55 | 80 | Stricter server classifier, unknown no longer silently becomes direct, internal/test/bot buckets added, verified green in staged tests |
| Funnel Integrity | 40 | 82 | Life missing events restored, generic quote-stage signals added, contact-step and estimate continuation surfaced, non-Life source contracts added |
| Meta Optimization Readiness | 50 | 80 | Shared lead-signal rules, Meta browser event catalog sync, stronger semantic dedup, thank-you fallback context fixed, Life generic CAPI telemetry aligned |
| AI Analytics Reliability | 58 | 79 | AI payload now includes marketing health warnings, scale-readiness contract, and guarded UI rendering |
| Scalability Readiness | 45 | 77 | Safer signal quality, cross-product pipeline telemetry, health center UI, clean verification script, dependency warning cleanup |

## What Changed

### 1. Central event taxonomy

Added shared catalog and signal definitions under:

- `SHARED/Analytics/AnalyticsEventDefinition.cs`
- `SHARED/Analytics/AnalyticsEventCatalog.cs`
- `SHARED/Analytics/MetaSignalEventCatalog.cs`
- `SHARED/Analytics/WebsiteLeadSignalDefinitions.cs`

This now acts as the source of truth for:

- browser-allowed analytics events
- backend ingest acceptance
- dashboard metric mapping
- Meta browser/server eligibility
- shared lead-state semantics

### 2. Frontend / backend parity

Wired the catalog into:

- `Protect-Website/Views/Shared/_Layout.cshtml`
- `Protect-Website/wwwroot/js/tracking.js`
- `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`

Key outcomes:

- hardcoded browser allowlist removed
- ingest now rejects unknown/browser-disallowed events via shared catalog
- rejected ingest events are logged with rejection reason and request context
- dashboard logic can classify shared metric flags instead of drifting string lists

### 3. Life funnel stabilization

Recovered missing/under-counted Life stages in:

- `Protect-Website/Views/Quote/Life.cshtml`

Added or normalized:

- `life_contact_first_start`
- `life_contact_first_complete`
- `estimate_contact_continue`
- `quote_step_complete`
- `quote_contact_step_view`
- generic step-complete emissions alongside Life-specific events

### 4. Fail-loud tracking and self-healing

Refactored:

- `Protect-Website/wwwroot/js/tracking.js`

Added:

- non-OK ingest detection
- critical-event retry with backoff
- local queue for critical events
- queue flush hooks on lifecycle transitions
- `client_tracking_error`
- global `window.onerror`
- global `window.onunhandledrejection`
- fetch failure / non-OK diagnostics

### 5. Shared lead-signal semantics

Unified the server/browser model around:

- `LeadFormStarted`
- `ContactIntentCaptured`
- `LeadReady`
- `QualifiedLead`
- `ConfirmedWebsiteLead`

Updated:

- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`
- `Protect-Website/Views/Quote/Life.cshtml`

### 6. Thank-you + Meta fallback hardening

Updated:

- `Protect-Website/Controllers/LifeQuoteController.cs`
- `Protect-Website/Controllers/ThankYouController.cs`

Key fixes:

- Life now persists both `MetaLeadEventId` and `MetaLeadLeadId`
- thank-you page gets quote tracking context for layout consumption
- quote-type / source / campaign / session context is forwarded more consistently

### 7. Attribution truth hardening

Updated:

- `AgentPortal/Services/Analytics/TrafficAttribution.cs`
- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`
- `AgentPortal/Services/Analytics/MetaSignalAnalyticsService.cs`
- `AgentPortal/Controllers/WebsiteAnalyticsController.cs`

Key behavior changes:

- empty attribution no longer silently becomes `Direct`
- `Unknown` is preserved when evidence is insufficient
- new buckets:
  - `Internal`
  - `Test`
  - `BotSuspicious`
- `NonPaid` now excludes `Unknown`, `Internal`, `Test`, and `BotSuspicious`
- Meta-attributed paid logic now refuses to classify internal/test/bot traffic as valid paid learning traffic

### 8. Lead pipeline telemetry

Lead persistence and workstation capture now emit explicit analytics events across the stabilized quote stack:

- `lead_persisted`
- `workstation_capture_attempt`
- `workstation_capture_success`
- `workstation_capture_failure`

This is present in:

- `Protect-Website/Controllers/LifeQuoteController.cs`
- `Protect-Website/Controllers/AutoQuoteController.cs`
- `Protect-Website/Controllers/HomeQuoteController.cs`
- `Protect-Website/Controllers/HealthQuoteController.cs`
- `Protect-Website/Controllers/DisabilityQuoteController.cs`
- `Protect-Website/Controllers/CommercialQuoteController.cs`

### 9. Non-Life instrumentation parity

Standardized generic quote-stage instrumentation beyond Life in:

- `Protect-Website/Views/Quote/Auto.cshtml`
- `Protect-Website/Views/Quote/Home.cshtml`
- `Protect-Website/Views/Quote/Health.cshtml`
- `Protect-Website/Views/Quote/Disability.cshtml`
- `Protect-Website/Views/Quote/Commercial.cshtml`

This pass ensures the stabilized non-Life forms now emit shared funnel signals such as:

- `quote_step_complete`
- `quote_contact_step_view`
- `lead_persisted`
- `workstation_capture_*`
- `capi_event_*`

### 10. Website Analytics Health Center

Built the health center surface in:

- `AgentPortal/Controllers/WebsiteAnalyticsController.cs`
- `AgentPortal/Views/WebsiteAnalytics/Index.cshtml`
- `AgentPortal/wwwroot/js/website-analytics.js`
- `AgentPortal/wwwroot/css/website-analytics.css`

This now exposes:

- a marketing health score
- verdict/status badge
- client tracking error counts
- inferred start counts
- lead persistence counts
- workstation success/failure counts
- unknown attribution counts
- warning list

### 11. AI review upgrade

Updated:

- `AgentPortal/Models/Analytics/AiInsightsDtos.cs`
- `AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs`
- `AgentPortal/Services/Analytics/OpenAiWebsiteAnalyticsReviewService.cs`
- `AgentPortal/Controllers/WebsiteAnalyticsAiController.cs`
- `AgentPortal/wwwroot/js/website-analytics-ai.js`

New AI contract includes:

- `growthOperatorScore`
- `scaleReadinessVerdict`
- `dataTrustWarning`
- `doNotScaleBecause`
- `nextThreeActions`

The AI payload now includes `MarketingHealth` with:

- client tracking errors
- inferred form starts
- unknown attribution count
- workstation capture attempt/success/failure counts
- no-owner failures
- internal/test/bot session counts
- health warnings

### 12. Life consent alignment and dependency cleanup

Completed final code-level stabilization for:

- explicit required Life contact consent in `Protect-Website/Views/Quote/Life.cshtml`
- server-side Life consent enforcement in `Protect-Website/Controllers/LifeQuoteController.cs`
- hashed-contact gating alignment with the existing quote-product rule set
- test-project dependency advisory cleanup by pinning patched transitive packages in:
  - `AgentPortal.Tests/AgentPortal.Tests.csproj`

The test-project build now completes with:

- `0 Warning(s)`
- `0 Error(s)`

## Tests Added / Updated

Added:

- `AgentPortal.Tests/AnalyticsEventCatalogTests.cs`
- `AgentPortal.Tests/AnalyticsQueryServiceMarketingHealthTests.cs`
- `AgentPortal.Tests/MetaSignalContractTests.cs`
- `AgentPortal.Tests/QuoteProductInstrumentationContractTests.cs`
- `AgentPortal.Tests/TrackingFailLoudContractTests.cs`
- `AgentPortal.Tests/ThankYouMetaFallbackContractTests.cs`
- `AgentPortal.Tests/WebsiteAnalyticsAiContractTests.cs`
- `AgentPortal.Tests/TrafficAttributionTests.cs`

Updated:

- `AgentPortal.Tests/AgentPortal.Tests.csproj`
- `AgentPortal.Tests/AnalyticsIngestControllerTests.cs`
- `AgentPortal.Tests/LeadSnapshotAttributionTests.cs`
- `AgentPortal.Tests/AnalyticsQueryServiceQuoteFunnelTests.cs`
- `AgentPortal.Tests/WebsiteAnalyticsDeleteLeadTests.cs`
- `AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs`

Coverage improved for:

- frontend/backend event-catalog parity
- browser-ingest acceptance rules
- life contact-first funnel classification
- marketing-health truthfulness and inferred-start detection
- thank-you context and Meta fallback contract checks
- fail-loud tracking contract checks
- AI review contract rendering / payload safety
- attribution bucket semantics
- unknown-vs-direct protection

## Final Verification

### Verification fixes made during this pass

This pass did not add new product features. It tightened verification truth and repaired the verification harness where it was masking real status.

Key fixes made before rerunning final verification:

- staged `scripts/verify-marketing-platform-health.sh` into explicit build/test gates with per-stage PASS output and final overall PASS/FAIL
- fixed `AgentPortal.Tests` execution as a real test project:
  - marked it as a test project
  - ensured runtime config is available to `testhost`
  - ensured adapter files are copied for xUnit discovery
  - enabled local test dependency copy so execution does not silently degrade into build-only behavior
- corrected analytics query truth:
  - `lead_persisted` health counts now use actual `lead_persisted` events
  - quote submit success uses canonical success classification instead of requiring a narrower downstream signal
  - inferred-start detection now distinguishes explicit starts from later implied progression
- corrected an attribution test fixture that accidentally labeled a fake `fbclid` value as `Test / QA` traffic because it literally contained the token `test`

### Exact commands run

Direct verification commands observed during this pass:

- `git branch --show-current`
- `dotnet build SHARED/Shared.csproj --nologo`
- `dotnet build Protect-Website/ProtectWebsite.csproj --nologo`
- `dotnet build AgentPortal/AgentPortal.csproj --nologo`
- `dotnet build AgentPortal.Tests/AgentPortal.Tests.csproj --nologo`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --nologo --filter "FullyQualifiedName~QuoteProductInstrumentationContractTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~AnalyticsEventCatalogTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~AnalyticsIngestControllerTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~AnalyticsQueryServiceQuoteFunnelTests|FullyQualifiedName~AnalyticsQueryServiceMarketingHealthTests|FullyQualifiedName~ThankYouMetaFallbackContractTests|FullyQualifiedName~TrackingFailLoudContractTests|FullyQualifiedName~WebsiteLifeLeadCaptureServiceTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~TrafficAttributionTests|FullyQualifiedName~LeadSnapshotAttributionTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~MetaSignalContractTests"`
- `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~WebsiteAnalyticsAiContractTests|FullyQualifiedName~WebsiteAnalyticsAiRedactorTests"`
- `./scripts/verify-marketing-platform-health.sh`

### Full verification status

Fully observed wrapper-script result after the final Life CAPI telemetry alignment:

- `./scripts/verify-marketing-platform-health.sh`
- final output: `[marketing-health] overall result: PASS (21s elapsed)`
- status: PASS
- elapsed time: 21 seconds
- hanging tests: none observed
- failing tests at final run: none

Observed per-stage results:

- build verification: shared: PASS
- build verification: protect website: PASS
- build verification: agent portal: PASS
- build verification: tests: PASS
- shared catalog tests: PASS
  - Failed: 0, Passed: 5, Skipped: 0, Total: 5
- ingest tests: PASS
  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
- quote funnel tests: PASS
  - Failed: 0, Passed: 15, Skipped: 0, Total: 15
- attribution tests: PASS
  - Failed: 0, Passed: 10, Skipped: 0, Total: 10
- meta signal tests: PASS
  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
- ai review contract tests: PASS
  - Failed: 0, Passed: 19, Skipped: 0, Total: 19
  - note: after the final parity-contract addition, the direct contract slice for `QuoteProductInstrumentationContractTests` passed 3/3 before the final wrapper run

Aggregate staged tests observed green:

- Failed: 0
- Passed: 55
- Skipped: 0
- Total: 55

### What was specifically verified

- all browser-side analytics events under source inspection are cataloged or explicitly excluded as non-analytics literals
- ingest validation is catalog-backed and rejects unknown/browser-disallowed events
- Life contact-first and estimate flow analytics remain accepted and query-visible
- Life now emits generic `capi_event_attempt`, `capi_event_success`, and `capi_event_failure` analytics alongside its existing Meta tracking flow
- non-Life quote views/controllers expose shared quote-stage and lead-pipeline instrumentation contracts
- fail-loud tracking hooks exist for HTTP-status inspection, retry, queueing, flush, and client diagnostics
- thank-you context and Life Meta fallback contracts remain present
- attribution no longer silently collapses unknown traffic into direct traffic
- AI review contract exposes growth score, scale-readiness, trust warnings, and guarded next actions
- Marketing Health Center is wired end to end through controller, UI, and styling
- wrapper verification script now finishes with an explicit end-state instead of requiring inference

### Tests failed

- none in the final observed verification run

### Diagnostics from earlier in this pass

Before the final green run, the main blockers were verification-harness issues rather than product-surface regressions:

- `AgentPortal.Tests` initially behaved like a buildable library more than a fully executable test project
- sandboxed `dotnet test` runs hit `TcpListener` permission issues, so final execution was rerun outside the sandbox as required
- one attribution test fixture was self-contradictory because its fake `fbclid` value included the token `test`

## Remaining Risks / Limitations

### Still requires live confirmation

- full quote-product manual QA matrix from the mission brief is **not** executed in a real browser session yet
- no live Meta Events Manager validation was performed in this pass
- legal/compliance sign-off is still required on the final business meaning of the Life contact-consent language, even though the code path is now aligned and enforced

### Important truth constraints

- workstation capture telemetry now covers Life, Auto, Home, Health, Disability, and Commercial, but still needs live route validation under real submissions
- attribution semantics are cleaner, but still need live session QA for paid landing reload paths
- AI review is safer now, but its quality still depends on live health data reaching the warehouse consistently

## Manual Live Validation Still Required

1. Meta browser Pixel + CAPI dedup in Meta Events Manager
2. thank-you browser fallback on redirect / AJAX-failure paths
3. Life consent-driven hashed contact eligibility review with compliance
4. paid landing session inheritance with:
   - `fbclid` only
   - UTM-only
   - reload after first click
   - internal preview / founder testing
5. workstation capture outcomes across Life, Auto, Home, Health, Disability, and Commercial quote flows

## Readiness Verdict

**Stable enough to merge and move into the next strategic build phase, but not yet certified for aggressive live scaling without live validation.**

The platform is materially stronger than the baseline and is now code-level complete for the stabilization scope, but it still needs:

- live QA across quote products
- live Meta Events Manager validation
- compliance confirmation on Life consent wording and matching use
- real-session verification of attribution inheritance and workstation capture outcomes

## Most Important Next Moves

1. Execute the controlled multi-product QA script with paid landing test URLs and verify every emitted stage end-to-end.
2. Validate browser Pixel + CAPI dedup in live Meta Events Manager.
3. Get compliance sign-off on the final Life contact-consent language and matching policy.
4. Use the Health Center to monitor real traffic before expanding the next growth/system layer.
