# Marketing Platform Health Final

Date: 2026-05-20  
Branch: `stabilization/marketing-platform-health`

## Scope Of This Pass

This pass prioritized measurement stability, taxonomy unification, life-funnel recovery, attribution truth, and operator-facing health visibility over new growth features.

## Before / After Health Scores

These scores are provisional engineering-readiness estimates based on code-path hardening, builds, and targeted tests. They are **not** live-traffic SLAs and should not be treated as production-certified until controlled QA and Meta Events Manager validation are completed.

| Area | Before | After | Basis |
|---|---:|---:|---|
| Tracking Reliability | 45 | 72 | Shared event catalog, browser allowlist sync, fail-loud tracking, retry/queue path, client diagnostics |
| Attribution Accuracy | 55 | 74 | Stricter server classifier, unknown no longer silently becomes direct, internal/test/bot buckets added |
| Funnel Integrity | 40 | 70 | Life missing events restored, generic quote-stage signals added, contact-step and estimate continuation surfaced |
| Meta Optimization Readiness | 50 | 71 | Shared lead-signal rules, Meta browser event catalog sync, stronger semantic dedup, thank-you fallback context fixed |
| AI Analytics Reliability | 58 | 73 | AI payload now includes marketing health warnings and scale-readiness contract |
| Scalability Readiness | 45 | 68 | Safer signal quality, pipeline telemetry, verification script, but live QA and consent-policy cleanup still pending |

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

Life lead persistence and workstation capture now emit explicit analytics events:

- `lead_persisted`
- `workstation_capture_attempt`
- `workstation_capture_success`
- `workstation_capture_failure`

This was added in:

- `Protect-Website/Controllers/LifeQuoteController.cs`

### 9. AI review upgrade

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

## Tests Added / Updated

Added:

- `AgentPortal.Tests/AnalyticsEventCatalogTests.cs`
- `AgentPortal.Tests/TrafficAttributionTests.cs`

Updated:

- `AgentPortal.Tests/AnalyticsIngestControllerTests.cs`
- `AgentPortal.Tests/AnalyticsQueryServiceQuoteFunnelTests.cs`
- `AgentPortal.Tests/WebsiteAnalyticsDeleteLeadTests.cs`
- `AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs`

Coverage improved for:

- frontend/backend event-catalog parity
- browser-ingest acceptance rules
- life contact-first funnel classification
- attribution bucket semantics
- unknown-vs-direct protection

## Verification

Verified during this pass:

- `dotnet build SHARED/Shared.csproj` passed
- `dotnet build Protect-Website/ProtectWebsite.csproj` passed
- `dotnet build AgentPortal/AgentPortal.csproj` passed
- targeted `dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj` slices exited successfully
- added `scripts/verify-marketing-platform-health.sh`

Note:

- the full script entered the long-running test phase in this environment and did not emit a final completion line during the observation window
- package warnings remain in `AgentPortal.Tests` restore output (`NU1603`, `NU1903`, `NU1904`)

## Remaining Risks / Limitations

### Not finished yet

- Life consent / EMQ policy is **not** fully resolved
- non-Life quote products are **not yet** all upgraded to equal funnel richness
- universal `IWebsiteLeadCaptureService` across all quote products is **not yet** implemented
- Health Center UI in Website Analytics is **not yet** built
- full quote-product QA matrix from the mission brief is **not yet** executed
- no live Meta Events Manager validation was performed in this pass

### Important truth constraints

- workstation capture telemetry is currently strongest on Life
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
5. workstation capture outcomes across non-Life quote flows

## Readiness Verdict

**Not ready to scale ad spend aggressively yet.**

The platform is materially stronger than the baseline and is much closer to being trustworthy, but it still needs:

- live QA across quote products
- consent / EMQ resolution
- non-Life instrumentation parity
- broader lead-routing standardization
- health-center UI / operational visibility completion

## Most Important Next Moves

1. Finish consent / EMQ policy cleanup and validate allowed hashed user-data coverage.
2. Extend workstation capture telemetry and routing consistency across Auto, Home, Health, Disability, Commercial, and the other Life variants.
3. Build the Website Analytics Health Center UI on top of the new health payloads.
4. Execute the controlled multi-product QA script with paid landing test URLs and verify every emitted stage end-to-end.
