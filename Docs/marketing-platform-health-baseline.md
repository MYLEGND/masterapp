# Marketing Platform Health Baseline

Date: 2026-05-20
Branch: `stabilization/marketing-platform-health`

## Scope

This baseline captures the current state of the MYLEGND marketing platform before stabilization work:

- `Protect-Website`
- `AgentPortal`
- `SHARED`
- `Infrastructure`
- Website analytics
- Meta browser/CAPI tracking
- Quote funnel instrumentation
- Lead persistence and workstation capture
- AI analytics review

## Baseline Build And Test Status

### Branch

- Requested branch created successfully: `stabilization/marketing-platform-health`

### Build status

- `dotnet build Protect-Website/ProtectWebsite.csproj`: passed
- `dotnet build AgentPortal/AgentPortal.csproj`: passed
- `dotnet build SHARED/Shared.csproj`: passed
- `dotnet build Infrastructure/Infrastructure.csproj`: passed
- `dotnet build MASTERAPP.sln`: failed because `AgentPortal.Tests` is out of sync with the current `WebsiteAnalyticsController` constructor

### Current solution/test drift

`MASTERAPP.sln` failed with:

- `AgentPortal.Tests/WebsiteAnalyticsDeleteLeadTests.cs`: missing required `config` argument for `WebsiteAnalyticsController`
- `AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs`: missing required `config` argument for `WebsiteAnalyticsController`

### Current package warnings observed during baseline

- `xunit.runner.visualstudio` resolved `2.8.0` instead of requested `>= 2.6.4`
- `Microsoft.AspNetCore.DataProtection 10.0.0` advisory `GHSA-9mv3-2cwr-p262`
- `System.Security.Cryptography.Xml 10.0.0` advisories `GHSA-37gx-xxp4-5rgx` and `GHSA-w3x6-4m5h-cxqf`
- `MailKit 4.14.1` advisory `GHSA-9j88-vvj5-vhgr`
- `Microsoft.Kiota.Abstractions 1.21.1` advisory `GHSA-7j59-v9qr-6fq9`
- `MimeKit 4.14.0` advisory `GHSA-g7hc-96xr-gvvx`

## Current Tracking Reality

The current platform does **not** have one source of truth for events. Event knowledge is split across:

- client browser allowlist in `Protect-Website/wwwroot/js/tracking.js`
- server ingest allowlist in `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
- funnel classification logic in `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`
- Meta signal browser rules in `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
- Meta signal server rules in `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`
- product-specific emitters such as `Protect-Website/Views/Quote/Life.cshtml`

This is the primary source of taxonomy drift.

## Primary Event Surfaces

### Browser analytics event source

- `Protect-Website/wwwroot/js/tracking.js`
  - Browser allowlist: `allowedEvents`
  - Async sender: `sendEvent`
  - Browser lifecycle emitters: `page_view`, `page_exit`, engagement, scroll, form, abandon

### Life funnel event source

- `Protect-Website/Views/Quote/Life.cshtml`
  - Product-specific quote funnel `trackEvent(...)`
  - AJAX submit path
  - browser Lead pixel attempt
  - estimate/contact-step progression events

### Meta signal browser source

- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
  - `emitSignal(...)`
  - Meta signal classification and browser pixel dispatch

### Backend ingest acceptance

- `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
  - `AllowedEventTypes`

### Analytics query classification

- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`
  - quote funnel stage classification
  - CTA/page/funnel/behavior query filters

### Meta signal server classification

- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`
  - accepted Meta signal event types
  - lead confirmation rules
  - dedup and CAPI dispatch

## Current Analytics Event Names

### Browser analytics allowlist in `tracking.js`

Current browser allowlist is defined in:

- `Protect-Website/wwwroot/js/tracking.js:38`

Important current families:

- Core page/CTA:
  - `page_view`
  - `cta_click`
  - `quote_click`
  - `risk_assessment_click`
  - `outbound_click`
- Generic form lifecycle:
  - `form_start`
  - `form_submit`
  - `form_submit_attempt`
  - `form_abandon`
  - `form_field_focus`
  - `form_field_complete`
  - `form_field_error`
- Behavior intelligence:
  - `page_exit`
  - `page_engaged_10s`
  - `page_engaged_30s`
  - `page_engaged_60s`
  - `scroll_depth_25`
  - `scroll_depth_50`
  - `scroll_depth_75`
  - `scroll_depth_90`
  - `scroll_depth_100`
  - `page_visibility_hidden`
  - `page_visibility_return`
- Lead modal:
  - `lead_modal_open`
  - `lead_modal_close`
  - `lead_form_start`
  - `lead_form_submit_success`
  - `lead_form_submit_failed`
- Life quote micro-funnel:
  - `life_step1_intro_view`
  - `life_step1_protecting_view`
  - `life_step1_protecting_select`
  - `life_step1_goal_view`
  - `life_step1_goal_select`
  - `life_step1_tobacco_view`
  - `life_step1_tobacco_select`
  - `life_step1_age_view`
  - `life_step1_age_continue`
  - `step1_age_entered`
  - `life_processing_bridge_view`
  - `life_processing_bridge_complete`
  - `life_value_bridge_view`
  - `life_value_bridge_continue`
  - `life_step2_view`
  - `life_step2_back`
  - `life_step2_submit_attempt`
  - `life_step2_submit_success`
  - `mini_results_view`
  - `recommendation_generated`
  - `results_contact_submit`
- Product submit/start events:
  - `life_general_form_start`
  - `life_general_submit`
  - `life_term_form_start`
  - `life_term_submit`
  - `life_whole_form_start`
  - `life_whole_submit`
  - `life_finalexpense_form_start`
  - `life_finalexpense_submit`
  - `life_mp_form_start`
  - `life_mp_submit`
  - `life_iul_form_start`
  - `life_iul_submit`

### Life funnel events emitted in `Life.cshtml`

Current life page emits the following notable events:

- `life_step1_intro_view`
- `life_step1_goal_view`
- `life_step1_protecting_view`
- `life_step1_coverage_view`
- `life_step1_age_view`
- `life_step1_tobacco_view`
- `life_processing_bridge_view`
- `life_processing_bridge_complete`
- `life_step2_view`
- `life_step2_back`
- `life_step1_goal_select`
- `life_step1_protecting_select`
- `life_step1_coverage_select`
- `life_step1_age_continue`
- `step1_age_entered`
- `recommendation_generated`
- `estimate_results_viewed`
- `estimate_inline_contact_view`
- `estimate_results_error`
- `estimate_contact_continue`
- `life_contact_first_view`
- `life_contact_first_submit_attempt`
- `life_contact_first_submit_success`
- `life_step2_submit_attempt`
- `life_step2_submit_success`
- `results_contact_submit`

Primary emitter file:

- `Protect-Website/Views/Quote/Life.cshtml`

### Backend ingest accepted event names

Current ingest allowlist is defined in:

- `AgentPortal/Controllers/API/AnalyticsIngestController.cs:37`

Important baseline fact:

- backend ingest accepts several life events that the browser allowlist still blocks
- this proves client/backend taxonomy drift already exists

Examples accepted by ingest:

- `life_step1_coverage_view`
- `life_step1_coverage_select`
- `life_contact_first_view`
- `life_contact_first_submit_attempt`
- `life_contact_first_submit_success`
- `estimate_results_viewed`
- `estimate_contact_continue`
- `estimate_results_error`

### Meta signal event names

Current browser/server Meta signal event vocabulary includes:

- `ViewContent`
- `RapidBounce`
- `SessionEngaged5s`
- `SessionEngaged15s`
- `MeaningfulScroll`
- `LeadFormStart`
- `DiscoveryComplete`
- `FunnelStepComplete`
- `RecommendationViewed`
- `ContactStepReached`
- `ContactInputStarted`
- `PhoneFieldCompleted`
- `RequiredContactFieldsCompleted`
- `SubmitAttempt`
- `HighIntentLeadSignal`
- `LeadReadySignal`
- `Lead`
- `QualifiedLead`
- `FieldError`
- `Backtrack`
- `DeadClick`
- `RageClick`
- `AbandonedHighIntentLead`

Primary files:

- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`
- `AgentPortal/Services/Analytics/MetaSignalAnalyticsService.cs`

## Known Event Drift Already Confirmed

These events are currently emitted or classified in at least one layer but are not fully aligned across all layers:

- `life_step1_coverage_select`
- `estimate_results_viewed`
- `estimate_contact_continue`
- `estimate_inline_contact_view`
- `life_contact_first_view`
- `life_contact_first_submit_attempt`
- `life_contact_first_submit_success`

## Where Events Are Emitted

### Shared browser tracking

- `Protect-Website/wwwroot/js/tracking.js`
  - page view
  - page exit
  - engaged time
  - scroll milestones
  - field focus/complete/error
  - generic form start/submit/abandon

### Quote-product-specific emitters

- `Protect-Website/Views/Quote/Life.cshtml`
  - life micro-steps
  - recommendation flow
  - contact-first flow
  - submit attempt/success
- `Protect-Website/Views/Quote/Auto.cshtml`
  - quote form submit handling
- `Protect-Website/Views/Quote/Home.cshtml`
  - quote form submit handling
- `Protect-Website/Views/Quote/Health.cshtml`
  - quote form submit handling
- `Protect-Website/Views/Quote/Disability.cshtml`
  - quote form submit handling
- `Protect-Website/Views/Quote/Commercial.cshtml`
  - quote form submit handling

### Meta browser emitters

- `Protect-Website/Views/Shared/_Layout.cshtml`
  - Meta PageView
- `Protect-Website/Views/Quote/Life.cshtml`
  - Meta Lead browser event on AJAX success
- `Protect-Website/Views/Quote/ThankYou.cshtml`
  - Meta Lead browser fallback
- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
  - Meta signal browser events + `ViewContent`

## Where Events Are Accepted

### Analytics ingest

- `AgentPortal/Controllers/API/AnalyticsIngestController.cs`

### Meta signal ingest

- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`

### Lead submission pipeline

- `AgentPortal/Controllers/API/LeadSubmitController.cs`
- `Protect-Website/Controllers/TrackingProxyController.cs`

## Where Events Are Queried

### Quote funnel and stage reporting

- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`

Current notable query usage:

- funnel starts
- page views
- CTA clicks
- form starts
- form submits
- field focus/completion/error
- form abandon
- life-specific step completion
- recommendation viewed/generated
- contact-step progression
- page engagement and exit timing

### Meta signal reporting

- `AgentPortal/Services/Analytics/MetaSignalAnalyticsService.cs`

### AI review data builder

- `AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs`
- `AgentPortal/Controllers/WebsiteAnalyticsAiController.cs`

## Where Events Impact Meta

### Browser pixel

- `Protect-Website/Views/Shared/_Layout.cshtml`
- `Protect-Website/Views/Quote/Life.cshtml`
- `Protect-Website/Views/Quote/ThankYou.cshtml`
- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`

### Server CAPI

- `Protect-Website/Controllers/AutoQuoteController.cs`
- `Protect-Website/Controllers/HomeQuoteController.cs`
- `Protect-Website/Controllers/HealthQuoteController.cs`
- `Protect-Website/Controllers/DisabilityQuoteController.cs`
- `Protect-Website/Controllers/CommercialQuoteController.cs`
- `Protect-Website/Controllers/LifeQuoteController.cs`
- `Protect-Website/Services/Meta/MetaConversionsApiService.cs`
- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`

## Where Events Impact Dashboard Metrics

### Website Analytics

- `AgentPortal/Controllers/WebsiteAnalyticsController.cs`
- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`

Current metrics that rely on event taxonomy correctness:

- top pages
- CTA performance
- page performance
- Quote Funnel
- abandonment
- behavior intelligence
- conversion center
- traffic overview
- leads snapshot

### Meta Signal Intelligence

- `AgentPortal/Services/Analytics/MetaSignalAnalyticsService.cs`

Current metrics that rely on signal taxonomy correctness:

- signal events
- signal visitors
- lead-ready visitors
- high-intent visitors
- signal-to-lead conversion
- event ladder
- campaign score quality
- page variant score quality

## Current Quote Submit Handlers

Confirmed submit handlers currently found in:

- `Protect-Website/Views/Quote/Life.cshtml`
- `Protect-Website/Views/Quote/Auto.cshtml`
- `Protect-Website/Views/Quote/Home.cshtml`
- `Protect-Website/Views/Quote/Health.cshtml`
- `Protect-Website/Views/Quote/Disability.cshtml`
- `Protect-Website/Views/Quote/Commercial.cshtml`

Current quote post controllers:

- `Protect-Website/Controllers/LifeQuoteController.cs`
- `Protect-Website/Controllers/AutoQuoteController.cs`
- `Protect-Website/Controllers/HomeQuoteController.cs`
- `Protect-Website/Controllers/HealthQuoteController.cs`
- `Protect-Website/Controllers/DisabilityQuoteController.cs`
- `Protect-Website/Controllers/CommercialQuoteController.cs`

## Current Lead Pipeline Surfaces

### Website lead persistence

- quote controllers persist `WebsiteLead`

### Life workstation capture

- `Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs`
- `Protect-Website/Controllers/LifeQuoteController.cs`

### Workstation queue

- `AgentPortal/Controllers/LeadBridgeController.cs`
- `Infrastructure/Data/MasterAppDbContext.cs`
  - `WorkstationLeadProfiles`

## Current Thank-You Context Surfaces

- `Protect-Website/Controllers/ThankYouController.cs`
- `Protect-Website/Views/Quote/ThankYou.cshtml`
- `Protect-Website/Views/Shared/_Layout.cshtml`

Baseline fact:

- layout expects `PageKey`, `PageVariant`, `PageMode`, `PageCategory`, and `QuoteTypeForTracking`
- thank-you controller currently sets very little of this context

## Current Consent And EMQ Surfaces

### Life

- `Protect-Website/Views/Quote/Life.cshtml`
  - hard-posts `MarketingEmailConsent=false`
- `Protect-Website/Controllers/LifeQuoteController.cs`
  - hashed contact eligibility depends on `TermsAccepted && MarketingEmailConsent`

### Other products

- `Protect-Website/Controllers/AutoQuoteController.cs`
- `Protect-Website/Controllers/HomeQuoteController.cs`
- `Protect-Website/Controllers/HealthQuoteController.cs`
- `Protect-Website/Controllers/DisabilityQuoteController.cs`
- `Protect-Website/Controllers/CommercialQuoteController.cs`

Baseline fact:

- life consent/matching semantics are not aligned with the rest of the quote stack

## Current AI Review Surfaces

- `AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs`
- `AgentPortal/Services/Analytics/OpenAiWebsiteAnalyticsReviewService.cs`
- `AgentPortal/Controllers/WebsiteAnalyticsAiController.cs`

Baseline fact:

- AI Review currently acts as a summarizer over a curated analytics payload
- it is not yet a health-aware growth operator

## Baseline Risk Summary

The highest-risk baseline conditions before stabilization are:

1. Event taxonomy is split across frontend, ingest, analytics queries, Meta signal browser logic, and Meta signal server logic.
2. Life funnel events are not fully aligned across emit/accept/query layers.
3. Browser event send failures are easy to miss.
4. LeadReady vs QualifiedLead semantics are inconsistent.
5. Life Meta fallback is weaker than other quote products.
6. Life consent handling likely suppresses EMQ-quality matching inputs.
7. Thank-you page context is incomplete.
8. Non-life quote instrumentation is thinner than life instrumentation.
9. Solution tests are already drifting from production code.

## Baseline Files To Refactor First

- `Protect-Website/wwwroot/js/tracking.js`
- `Protect-Website/Views/Quote/Life.cshtml`
- `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
- `AgentPortal/Services/Analytics/AnalyticsQueryService.cs`
- `Protect-Website/wwwroot/js/meta-signal-intelligence.js`
- `Protect-Website/Services/MetaSignal/MetaSignalIntelligenceService.cs`
- `Protect-Website/Controllers/LifeQuoteController.cs`
- `Protect-Website/Controllers/ThankYouController.cs`
- `Protect-Website/Views/Quote/ThankYou.cshtml`
- `AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs`
- `AgentPortal.Tests/*analytics*`

## Phase 1 Exit Criteria

Phase 1 is considered complete because:

- the requested branch exists
- the current production build state is recorded
- the current test drift is recorded
- the current event emit/accept/query/meta surfaces are mapped
- the known drift points are documented

Phase 2 begins by creating one shared event catalog and replacing hardcoded drift with parity-tested shared definitions.
