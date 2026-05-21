# Sales Operating System Discovery

## 1. Current architecture summary

### Executive read

MYLEGND already has the core layers needed for a Sales Operating System:

- `Protect-Website` is the public demand capture and qualification surface.
- `Website Analytics` is the attribution, funnel, and trust layer.
- `Leads` is already the best candidate for the internal command center.
- `Workstation` already acts as the call-script and execution surface.
- `AgentPortal` should remain the internal host/container, not become a monolithic Sales OS.

The current foundation supports evolution by extension rather than rebuild. The main gaps are not "missing app shells." The main gaps are:

- durable lead-intake to CRM linkage
- structured appointment / outcome data
- sales-grade attribution visibility inside Leads and Workstation
- closed-loop analytics from lead to booked call to show / no-show / sold

### Public conversion engine

Primary public entry points:

- [LifeQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/LifeQuoteController.cs)
- [HomeQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/HomeQuoteController.cs)
- [AutoQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/AutoQuoteController.cs)
- [CommercialQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/CommercialQuoteController.cs)
- [DisabilityQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/DisabilityQuoteController.cs)
- [HealthQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/HealthQuoteController.cs)

Key observations:

- Life, Term, Whole Life, Final Expense, Mortgage Protection, and IUL are already unified under one controller and one wizard surface via [LifeQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/LifeQuoteController.cs).
- Life flows already capture richer qualification context than the other quote flows.
- Life flows already include estimate/recommendation logic through [LifeEstimateEngine.cs](/Users/zacowen/MASTERAPP/Protect-Website/Services/LifeEstimateEngine.cs).
- Non-life flows are mostly traditional lead forms with strong capture breadth but weaker downstream intelligence mapping.

### Shared lead persistence and bridge

Core persistence and handoff seams:

- [WebsiteLead.cs](/Users/zacowen/MASTERAPP/Domain/Entities/WebsiteLead.cs)
- [WebsiteLifeLeadCaptureService.cs](/Users/zacowen/MASTERAPP/Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs)
- [IWebsiteLifeLeadCaptureService.cs](/Users/zacowen/MASTERAPP/Infrastructure/Leads/IWebsiteLifeLeadCaptureService.cs)
- [WorkstationLeadProfile.cs](/Users/zacowen/MASTERAPP/Domain/Entities/WorkstationLeadProfile.cs)
- [WorkstationLeadBuckets.cs](/Users/zacowen/MASTERAPP/Infrastructure/Leads/WorkstationLeadBuckets.cs)

Key observations:

- Public quote submissions already save `WebsiteLead`.
- Public quote submissions already attempt workstation/CRM capture.
- Product bucket routing is already standardized.
- Owner assignment already resolves from agent tracking profile, slug, or recipient email.

This is the most important reuse seam in the whole system.

### Website Analytics / intelligence layer

Core analytics surfaces:

- [AnalyticsIngestController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/API/AnalyticsIngestController.cs)
- [LeadSubmitController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/API/LeadSubmitController.cs)
- [WebsiteAnalyticsController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/WebsiteAnalyticsController.cs)
- [AnalyticsQueryService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/AnalyticsQueryService.cs)
- [TrafficAttribution.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/TrafficAttribution.cs)
- [WebsiteAnalyticsAiDataBuilder.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs)
- [OpenAiWebsiteAnalyticsReviewService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/OpenAiWebsiteAnalyticsReviewService.cs)

Current analytics coverage already includes:

- traffic overview
- page performance
- CTA performance
- quote funnel
- marketing health
- conversions
- lead snapshots
- agent performance
- engagement / time-on-page / exit / journey
- source performance
- landing page performance
- form friction / abandonment
- Meta campaigns
- Meta signal intelligence
- AI review snapshots and operator analysis

What is missing is not a reporting engine. What is missing is sales-outcome data for analytics to query.

### Leads CRM command center

Primary Leads surfaces:

- [LeadsController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadsController.cs)
- [ClientListItemViewModel.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientListItemViewModel.cs)
- [ClientCrmMeta.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientCrmMeta.cs)
- [Index.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/Index.cshtml)
- [_LeadPipelineView.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadPipelineView.cshtml)
- [_LeadQuickView.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadQuickView.cshtml)
- [_ActionsTab.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_ActionsTab.cshtml)
- [_CommitmentsTab.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_CommitmentsTab.cshtml)
- [_LeadQuickViewUtilities.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadQuickViewUtilities.cshtml)

Key observations:

- Leads already behaves like an operating console, not a simple list page.
- It already has queueing, stage management, next-date planning, collaboration, notes, document checklist, actions, commitments, production rollups, and meeting utilities.
- `ClientCrmMeta` is already the system's flexible workflow metadata carrier.
- The fastest path is to keep Leads as the command center and enrich it, not replace it.

### Workstation execution center

Primary Workstation surfaces:

- [WorkstationController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/WorkstationController.cs)
- [LeadBridgeController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadBridgeController.cs)
- [LeadBridgeStateService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/LeadBridgeStateService.cs)
- [DistributedLeadBridgeStateService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/DistributedLeadBridgeStateService.cs)
- [_LeadBridge.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/_LeadBridge.cshtml)
- [_ScriptSwitcher.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/_ScriptSwitcher.cshtml)
- [Rebuttals.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/Rebuttals.cshtml)
- [LifeInsuranceRebuttals.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/LifeInsuranceRebuttals.cshtml)
- [FinalExpenseRebuttals.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/FinalExpenseRebuttals.cshtml)
- [WorkstationNotesController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/WorkstationNotesController.cs)

Key observations:

- Workstation already loads lead context.
- Workstation already supports queue progression and active lead state.
- Workstation already exposes outcome buttons like `Booked`, `FollowUp`, `NoAnswer`, `NotInterested`, and `PolicyPlaced`.
- Workstation currently routes script experiences primarily by product bucket, not by richer funnel intelligence or lead state.

### Calendar / meeting support

Primary appointment-related surfaces:

- [CalendarController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/CalendarController.cs)
- [ClientCrmMeta.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientCrmMeta.cs)
- [_LeadQuickView.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadQuickView.cshtml)
- [_LeadQuickViewUtilities.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadQuickViewUtilities.cshtml)

Key observations:

- Outlook calendar integration already exists.
- Meeting time, meeting duration, meeting location, Zoom join URL, and last calendar event references already exist in CRM meta.
- Appointment support exists operationally, but not yet as first-class, queryable sales pipeline data.

## 2. Current lead flow map

### Current real flow

`Meta / paid / non-paid traffic`

→ public landing or quote route in `Protect-Website`

→ browser analytics events sent to [AnalyticsIngestController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/API/AnalyticsIngestController.cs)

→ quote submit handled in quote controller

→ `WebsiteLead` persisted

→ server-side analytics events persisted

→ workstation capture attempted through [WebsiteLifeLeadCaptureService.cs](/Users/zacowen/MASTERAPP/Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs)

→ `WorkstationLeadProfile` created or updated

→ lead appears in Leads CRM queues

→ lead can be pulled into Workstation via Lead Bridge

→ agent logs stage/outcome in Leads / Workstation

→ calendar event can be created manually from CRM tooling

### Where lead intelligence is captured today

Strongly captured today:

- public attribution: session, visitor, UTM, Meta IDs, fbclid
- product / offer context
- page context
- event timeline
- life discovery answers
- life recommendation output
- basic contact information
- basic stage / queue state
- meeting utility fields in CRM meta

Partially captured today:

- preferred contact method on Home / Auto / Commercial / Disability
- best time to contact on Home / Auto / Commercial / Disability
- contact preferences are not consistently promoted into internal lead workspace

Weak or missing today:

- explicit appointment intent
- urgency
- preferred appointment windows as structured internal data
- AI lead score
- fit score
- appointment readiness score
- funnel behavior summary inside CRM
- durable booked / confirmed / completed / no-show / cancelled event history
- closed-loop source-to-booked-call and source-to-close reporting

### Where intelligence survives

Best survival today:

- `WebsiteLead`
- `AnalyticsEvent`
- `LifeQuote` metadata snapshot inside `WebsiteLead.MetadataJson`
- `ClientCrmMeta` for internal workflow state

### Where intelligence is lost or thinned out

Main losses today:

- `WebsiteLead.MetadataJson` is rich, but only a small slice is mapped into `WorkstationLeadProfile`
- attribution remains strong in analytics, but is not surfaced as first-class CRM or Workstation context
- meeting state exists in CRM meta, but appointment outcomes are not modeled as a durable timeline
- workstation outcomes update stage, but do not currently feed a dedicated sales-outcome analytics model
- repeated website submissions to the same lead do not have a strong historical linkage model for downstream reporting

### Current answers to the discovery questions

Where lead intelligence is captured:

- Yes, strongly on the public/analytics side

Where it is lost:

- Mostly at the handoff from `WebsiteLead` into `WorkstationLeadProfile`

Where attribution survives:

- In `WebsiteLead` and `AnalyticsEvent`

Where attribution breaks:

- In CRM/workstation visibility and closed-loop reporting, not in top-of-funnel capture

Whether Leads CRM currently receives all needed data:

- No

Whether Workstation can be launched with lead context:

- Yes

Whether outcomes sync back to CRM:

- Yes, stage-level sync exists

Whether appointment tracking exists:

- Partially

Whether follow-up orchestration exists:

- Partially, through `ActionItem`, `Commitment`, queueing, and CRM next-step fields

## 3. Existing files and components to reuse

| Area | Reuse candidate | Why it should be reused |
| --- | --- | --- |
| Public intake | [LifeQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/LifeQuoteController.cs) | Already handles multi-offer life-family flows, recommendation logic, and rich metadata capture. |
| Public intake | [HomeQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/HomeQuoteController.cs), [AutoQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/AutoQuoteController.cs), [CommercialQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/CommercialQuoteController.cs), [DisabilityQuoteController.cs](/Users/zacowen/MASTERAPP/Protect-Website/Controllers/DisabilityQuoteController.cs) | Already persist `WebsiteLead`, analytics events, and workstation capture attempts. |
| Lead persistence | [WebsiteLead.cs](/Users/zacowen/MASTERAPP/Domain/Entities/WebsiteLead.cs) | Best place to keep public-source truth and intake snapshots. |
| Intake bridge | [WebsiteLifeLeadCaptureService.cs](/Users/zacowen/MASTERAPP/Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs) | Already maps public submissions into the internal lead workspace. |
| Internal lead workspace | [WorkstationLeadProfile.cs](/Users/zacowen/MASTERAPP/Domain/Entities/WorkstationLeadProfile.cs) | Already powers the Leads and Workstation operating surfaces. |
| CRM workflow metadata | [ClientCrmMeta.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientCrmMeta.cs) | Fastest place to add new workflow fields before promoting high-value ones into columns. |
| Lead command center | [LeadsController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadsController.cs) and [Index.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/Index.cshtml) | Already acts like an internal execution dashboard. |
| Execution tooling | [LeadBridgeController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadBridgeController.cs) and [_LeadBridge.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/_LeadBridge.cshtml) | Existing seam for "Work Lead" behavior and active lead execution context. |
| Follow-up engine | [ActionItem.cs](/Users/zacowen/MASTERAPP/Domain/Entities/ActionItem.cs) and [Commitment.cs](/Users/zacowen/MASTERAPP/Domain/Entities/Commitment.cs) | Already model work due, promises, and follow-up surfaces. |
| Calendar | [CalendarController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/CalendarController.cs) | Existing meeting/event integration should be extended, not replaced. |
| Intelligence | [AnalyticsQueryService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/AnalyticsQueryService.cs) | Already the read-model layer for operator reporting. |
| AI operator | [WebsiteAnalyticsAiDataBuilder.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs) and [OpenAiWebsiteAnalyticsReviewService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/OpenAiWebsiteAnalyticsReviewService.cs) | Clean place to evolve from growth-only review into growth + sales intelligence. |

## 4. Missing data fields and entities

### Fields that should be added to public lead intake

Needed for future Sales OS behavior:

- appointment intent
- urgency / buying timeframe
- preferred contact window
- preferred meeting window
- preferred contact channel normalization
- lead motivation summary
- recommendation summary / estimate summary in structured form
- qualifying signals per product line

Best initial home:

- add structured fields to `WebsiteLead`
- keep product-specific overflow in `MetadataJson`

### Fields that should be carried into the internal lead workspace

Needed in Leads / Workstation:

- source / medium / campaign / page key
- latest funnel stage reached before submit
- public qualification summary
- urgency score
- fit score
- appointment readiness score
- latest recommendation / estimate summary
- intake timestamp and latest resubmission timestamp
- lead temperature
- stale lead indicator
- no-contact warning / SLA warning
- next-best-action
- estimated opportunity value

Best initial home:

- add low-risk workflow fields to `ClientCrmMeta`
- promote query-heavy fields to real columns later

### New entities that should exist before full Sales OS scale

Recommended additions:

1. `LeadIntakeLink` or `LeadIntakeEvent`
Purpose:
- durable link between `WebsiteLead` and `WorkstationLeadProfile`
- preserve repeated submissions
- preserve attribution continuity
- preserve offer / page / recommendation / urgency snapshot at intake time

2. `LeadAppointment`
Purpose:
- first-class appointment object instead of scattering appointment state across CRM JSON and Outlook IDs
- status values like `Requested`, `Booked`, `Confirmed`, `Rescheduled`, `Completed`, `NoShow`, `Cancelled`

3. `LeadOutcomeEvent` or `SalesOutcomeEvent`
Purpose:
- durable call and pipeline outcome history
- allows analytics to track booked, no answer, callback, follow-up needed, sold, lost

4. optional `LeadScoreSnapshot`
Purpose:
- freeze AI / urgency / fit / readiness scores by date without recalculating history

## 5. Required database migrations

Do not implement in this phase. These are the recommended future migrations.

### Migration set A: intake continuity

- extend `WebsiteLead` with structured appointment-intent and timing fields
- add structured public qualification summary fields where high-value and stable
- keep large offer-specific payloads in `MetadataJson`

### Migration set B: durable lead linkage

- add a link/history table from `WebsiteLead` to `WorkstationLeadProfile`
- include `CapturedUtc`, `CaptureResult`, `AgentUserId`, `Bucket`, and snapshot fields

This is the most important migration for preserving attribution continuity at sales stage.

### Migration set C: appointment model

- create `LeadAppointment`
- store lead id, owner, scheduled time, channel, calendar event id, zoom link, status, booked source, completed/no-show/cancelled timestamps

### Migration set D: sales outcome model

- create `LeadOutcomeEvent`
- store lead id, actor, outcome code, notes, created time, related appointment id when present

### Migration set E: queryable sales signals

- promote selected CRM meta fields into columns only if needed for scale:
- priority
- next due date
- appointment status
- lead temperature
- latest score values
- stale warning flags

## 6. Leads CRM upgrade plan

### Goal

Make Leads the single operating console for:

- queue priority
- pipeline progression
- appointment readiness
- follow-up visibility
- source-aware selling
- one-click handoff into Workstation

### Recommended upgrade path

Phase 1 inside Leads:

- show source / campaign / page attribution on list cards and quick view
- show intake snapshot summary from public funnel
- show latest recommendation / estimate summary when available
- show appointment-intent and preferred-contact-window data
- add explicit `Work Lead` CTA into current active Workstation flow

Phase 2 inside Leads:

- compute and display lead temperature
- compute and display urgency score
- compute and display fit score
- compute and display appointment readiness score
- surface stale/no-contact warnings
- surface next-best-action using existing action / commitment / timing data

Phase 3 inside Leads:

- add booked / confirmed / no-show / completed appointment visibility
- add closed-won / closed-lost quality outcome capture
- add opportunity value estimation

### Strong reuse points

- [LeadsController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadsController.cs)
- [ClientCrmMeta.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientCrmMeta.cs)
- [ClientListItemViewModel.cs](/Users/zacowen/MASTERAPP/AgentPortal/Models/ClientListItemViewModel.cs)
- [Index.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/Index.cshtml)
- [_LeadQuickView.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Leads/_LeadQuickView.cshtml)

## 7. Workstation integration plan

### Goal

Keep Workstation as the call execution surface, but feed it richer context from Leads.

### Recommended evolution

Add to Workstation lead context:

- source / campaign / page context
- latest funnel behavior summary
- recommendation / estimate snapshot
- appointment readiness summary
- objections likely based on behavior and product type
- recommended opener based on source + product + current stage

Keep current script-routing model, but extend beyond bucket-only:

- product bucket remains primary route
- stage / appointment status / source can add script overlays

Outcome logging should write to:

- current lead stage
- durable sales outcome history
- appointment record if one exists

Strong reuse points:

- [LeadBridgeController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/LeadBridgeController.cs)
- [_LeadBridge.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/_LeadBridge.cshtml)
- [_ScriptSwitcher.cshtml](/Users/zacowen/MASTERAPP/AgentPortal/Views/Workstation/_ScriptSwitcher.cshtml)
- [WorkstationController.cs](/Users/zacowen/MASTERAPP/AgentPortal/Controllers/WorkstationController.cs)

## 8. Protect-Website appointment-intent plan

### Goal

Keep `Protect-Website` as the conversion engine, while increasing the quality of downstream sales intelligence.

### Current state

- Life flow captures rich qualification and recommendation data.
- Home / Auto / Commercial / Disability already capture preferred contact method and best time to contact.
- Life flow does not yet capture structured appointment intent or preferred contact window.
- No public flow currently acts like a proper appointment-intent collector.

### Recommended evolution

Add only minimal, high-signal fields:

- "Would you like help reviewing this now?" or equivalent appointment-intent capture
- preferred callback / review window
- urgency / timeframe
- optional preferred meeting type

Rules:

- do not turn public funnels into CRM forms
- do not overload public UX with internal sales questions
- do not break current analytics attribution contracts

Best implementation seam:

- extend view models
- persist new fields in `WebsiteLead`
- pass them through workstation capture
- preserve raw capture in `MetadataJson`

## 9. Website Analytics sales-metrics plan

### Goal

Evolve Website Analytics from funnel truth center into funnel-to-sales truth center.

### Metrics that already exist or are close

- campaign to visitor
- visitor to quote intent
- funnel progression
- lead capture success
- source / campaign attribution
- trust / tracking diagnostics

### Metrics that need new data

- campaign to booked call
- booked call to show
- show to sold
- source to close
- owner conversion by appointment stage
- appointment no-show rate by source / campaign / page
- follow-up SLA health
- stale lead rate

### Recommended implementation approach

- keep [AnalyticsQueryService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/AnalyticsQueryService.cs) as the read model
- add new sales queries only after durable appointment and outcome entities exist
- preserve current trust logic so operator dashboards can still warn when analytics and CRM data disagree

## 10. AI Growth Operator expansion plan

### Current state

Current AI operator services are marketing-focused and already disciplined about:

- aggregate-only data
- no PII
- data trust warnings
- funnel / campaign diagnosis

Primary reuse seams:

- [WebsiteAnalyticsAiDataBuilder.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/WebsiteAnalyticsAiDataBuilder.cs)
- [OpenAiWebsiteAnalyticsReviewService.cs](/Users/zacowen/MASTERAPP/AgentPortal/Services/Analytics/OpenAiWebsiteAnalyticsReviewService.cs)

### Recommended evolution

Add a second operator layer, not a replacement:

- Growth Operator remains marketing / funnel / attribution-focused
- Sales Operator adds lead quality, appointment readiness, stale lead, follow-up risk, and source-to-booked-call analysis

Future AI outputs should answer:

- which leads should be worked now
- which leads are high urgency but under-touched
- which campaigns are creating booked calls versus low-quality leads
- which agents or queues have follow-up leakage
- where data trust is too weak to automate decisions

## 11. Testing plan

### Discovery-phase recommendation

Future implementation should add tests in four layers.

1. Public intake tests
- quote controller persistence
- metadata passthrough
- appointment-intent field persistence

2. Bridge tests
- `WebsiteLead` to `WorkstationLeadProfile` linkage
- duplicate intake behavior
- owner resolution
- attribution carry-forward

3. CRM / Workstation tests
- lead score computation
- stage transitions
- appointment lifecycle transitions
- outcome logging
- Workstation launch context hydration

4. Analytics tests
- campaign to booked call joins
- show / no-show / sold metrics
- trust warnings when link data is missing
- AI payload contract coverage

## 12. Risks

### Main technical risks

1. Lead identity continuity
- current workstation capture can update an existing lead without preserving a durable intake history link

2. JSON meta overgrowth
- `ClientCrmMeta` is a great extension seam, but too much critical reporting data in JSON will hurt analytics and indexing

3. Split internal surfaces
- `Leads` and `Clients` both exist; the system needs a clear rule that Sales OS work lives in `Leads`

4. Outcome logging is stage-heavy, not history-heavy
- today the system is good at current state, weaker at durable event history

5. Appointment state is operational, not analytical
- calendar and meeting helpers exist, but booked/show/no-show is not a first-class metrics model yet

6. Attribution can still get stranded before CRM
- analytics knows source and behavior, but the salesperson does not always see it in the operating surface

## 13. Recommended phased implementation order

### Phase A: data continuity first

- add durable intake-to-lead linkage
- carry forward public qualification and attribution into CRM context
- keep public UX stable

### Phase B: Leads command center enrichment

- source-aware lead cards
- intake summary
- urgency / fit / readiness scoring
- `Work Lead` handoff

### Phase C: Workstation context enrichment

- script overlays from funnel/source/stage
- richer outcome logging
- sync outcomes back into Leads and sales history

### Phase D: appointment model

- first-class appointment record
- calendar linkage
- booked / confirmed / completed / no-show reporting

### Phase E: sales analytics

- booked-call funnel
- campaign-to-appointment
- source-to-close foundation
- operator trust warnings for broken joins

### Phase F: AI Sales Operator

- queue prioritization
- stale lead warnings
- next-best-action
- source-quality versus close-quality diagnosis

## Recommendation

This codebase should be evolved, not rebuilt.

The strongest strategy is:

- keep `Protect-Website` public
- keep `Website Analytics` as truth and diagnostics
- keep `Leads` as the command center
- keep `Workstation` as the execution surface
- add durable linkage and sales data models between them

That path is the safest way to get a real Sales Operating System without breaking attribution continuity or creating a second lead platform.
