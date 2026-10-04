# Canonical measurement repair — 2026-10-03

Branch: `repair/canonical-measurement-truth-20261003`
Approved base: `49d7e5d783523ce0d8df27e4fbd0932f7ce7c92d`

This is a source repair and validation record, **not a production delivery sign-off**. No deployment or provider-side attribution is established by this document.

## Authority map

| Boundary | Canonical owner | Required invariant |
|---|---|---|
| Website ownership and publication | PublicWebsiteRuntimeScopeResolver; WebsiteContentState/Version | Permanent business/agent/founder ownership and the published version determine runtime scope. Draft/edit mode cannot produce production measurement. |
| Published actions and presets | AnalyticsEventCatalog; PublishedWebsiteBindingResolver | Server resolves the published action/binding. Ordinary clicks, form attempts, and page views cannot manufacture verified leads or purchases. |
| First-party capture | WebsiteTrackingProxyAuthority → UnifiedEventMapper → UnifiedAnalyticsWriter → AnalyticsEvents | Stable client event identity; scope conflicts rejected; duplicate retry returns the persisted identity. |
| Human classification | TrafficQualityBucketFilters | One observed-behavior policy for SQL, in-memory reporting, and visitor trust. Owner + host + session + visitor bound evidence; internal/automation/anomaly exclusions take precedence. |
| Marketing eligibility | CanonicalMarketingEligibility | Reload persisted source; use shared classification; require consent evidence; suppress corrected/deleted production outcomes; fail closed with a reason. Both provider dispatchers and browser acknowledgements use this gate. |
| CRM production | CanonicalCrmOutcomeService | AgentPortal and ClientApp mutations and their immutable canonical facts share a transaction. Status changes, monetary corrections, and deletion have reporting projections. |
| Appointments | CanonicalCrmOutcomeService with booking/CRM authorities | Completion requires an actual recorded status transition. Merely passing the scheduled end does not create completion or qualification. |
| Revenue/economics | CanonicalMarketingOutcomeProjection | Current production projection, stable customer/opportunity identities, and shared monetary parsing. Provider transport rows do not become business truth. |
| Meta delivery | MetaSignalAnalyticsBridge → MetaSignalOutcomeDispatcherHostedService | MetaSignalEvents are destination projections/receipts, not a competing source for campaign CRM totals. Browser SDK invocation is not acceptance. |
| OpenAI delivery | OpenAiConversionDispatcherHostedService → MarketingDestinationDelivery | Independent destination; original canonical identity; pinned owner/account/data source/pixel; visible blocked, retry, failure and expiry states. |
| Website Event Map | WebsiteEventMapQuery | Distinguish observed, projected, HTTP accepted, provider accepted, partial, blocked and unverified receipt states. The map describes the current published version. |

The destination registry exposes **mapping readiness**, not authorization to deliver. Any future destination must use CanonicalMarketingEligibility and the existing canonical event catalog rather than invent a separate human classifier or business-outcome producer.

## Outcome mappings

Mappings remain owned by `MarketingConversionDestinationCatalog`; this table documents that catalog, not a new registry.

| Canonical outcome | Meta | OpenAI |
|---|---|---|
| Lead | Lead | lead_created |
| QualifiedLead | QualifiedLead (custom) | qualifiedlead (custom) |
| AppointmentBooked | AppointmentBooked (custom) | appointment_scheduled |
| AppointmentCompleted | AppointmentCompleted (custom) | appointmentcompleted (custom) |
| ApplicationSubmitted | ApplicationSubmitted (custom) | applicationsubmitted (custom) |
| PolicyIssued | PolicyIssued (custom) | policyissued (custom) |
| PolicyPaid | Purchase | order_created |
| AddToCart | AddToCart | items_added |
| InitiateCheckout | InitiateCheckout | checkout_started |
| Purchase | Purchase | order_created |

Ten canonical outcomes produce nine distinct OpenAI conversion types/names because PolicyPaid and Purchase share order_created. A configured conversion definition is not evidence of a received event. Browser page views and non-conversion engagement follow the existing browser behavior mappings and remain separate from verified outcomes.

## Human evidence policy

The score is a heuristic, not proof of identity or a calibrated probability. The stable `real_human_traffic` key means high-confidence observed human behavior; it must not be described as identity verification.

- Internal/QA evidence, automation indicators, and inconsistent telemetry override a high score.
- Event names, ad click references, revenue amounts and elapsed timers alone do not grant human confidence.
- Interaction: 35 points for at least one trusted browser interaction, 45 for at least three.
- Visible engagement: 20 points at five seconds, 30 at fifteen seconds.
- Scroll: 15 points at 25%, 20 at 50%; mouse movement adds five at five observations.
- A user agent is required for behavioral scoring. Script-generated pointer/touch/mouse events do not increment the shared runtime's interaction counters.
- High-confidence bucket: at least 90. Likely-human bucket: 60–89. Marketing requires high-confidence or likely-human score at least 80, plus consent and every other destination requirement.
- Unknown evidence remains review-needed. A lead/purchase name cannot turn it into human evidence.
- CRM lineage can reach an acquisition session outside the dashboard date range. Classifications use currently retained session evidence; the reporting date range continues to select the facts being counted.
- Delivery evidence above the bounded session read limit is blocked explicitly rather than partially sampled and approved.

Browser telemetry can be spoofed by sophisticated automation. Production false-positive/false-negative calibration and abuse controls still require observed runtime evidence; no source-only audit can promise perfect human detection.

## Repairs and regression coverage

| Finding | Repair |
|---|---|
| Beacon treated as acknowledgement | Keep the exact queued event until acknowledged fetch replay; preserve additions made during a pending flush. |
| Independent human shortcuts | Remove duplicate trust scoring and Meta browser time/event-name shortcuts; use shared server eligibility. |
| Server consent gap | Persist consent state with canonical browser events and changes as `measurement_consent_changed`; shared delivery gate rejects missing/denied evidence and later same-owner visitor denial. |
| CRM app divergence / partial saves | Both production write surfaces invoke one transactional authority. Relational failure regression verifies rollback. |
| Edited/deleted production counted as old revenue | Immutable reporting snapshots reconcile the current value/state. Original conversion identities remain intact. Superseded undelivered facts are blocked pending reconciliation, not sent as another purchase. |
| Clock-created appointment completion | Remove the auto-completion worker and registration. |
| Meta reporting sourced from transport projections | Campaign outcomes read canonical AnalyticsEvents and the shared value projection. |
| Customer/lifecycle double counting | Distinct customer identity; appointment identity dedupe; separate opportunities retain separate pipeline value. |
| Provider failures displayed as zero economics | Nullable spend/CAC/ROAS propagate through contracts, UI and AI summaries. Canonical outcomes remain available when providers fail. |
| OpenAI invisible configuration failures / abandoned retries | Create blocked receipts before readiness checks; pin a newly completed destination once; expire overdue canonical retries separately from the source scan. |
| Projected/browser invocation described as delivery | Event Map requires response evidence; mixed receipts stay partial and receipt-only historical status stays unverified. Validation-only API requests are not delivery. |
| Repeated owner/receipt lookups | Group owner resolution and batch historical receipt retrieval. |

## Release and live-proof checklist

These are remaining verification requirements, not promises that production already passes:

1. Exact branch-head CI and required repository checks; approved release lifecycle; deployed runtime SHA for each affected host. Do not merge around the protected branch gates.
2. One controlled, consented human session for each permanent owner scope: published version → page/CTA/form → persisted lead → CRM qualification/booking → explicit completion → genuine submitted/issued/paid outcome. Use test facilities and distinguish test traffic from production optimization.
3. Reconcile canonical IDs, owner IDs, session lineage, binding/version, first-party facts, provider request identity, HTTP response and provider-visible receipt. Provider acceptance and advertising attribution are separate proof obligations.
4. Negative checks: duplicate retry, denied consent/GPC, internal QA, automation, unknown telemetry, wrong owner, unpublished/edit state, provider timeout, destination change and aged retry. These must remain visible without becoming marketing conversions.
5. Replay production edits/deletion against reporting and inspect previously delivered outcomes. External revenue adjustments are **not** implemented as invented purchases. Already delivered corrections require a supported provider adjustment/reconciliation procedure; the source repair explicitly records that requirement.
6. Run the original dashboard timeout/JavaScript reproducer against the deployed revision and inspect query plans at production-like cardinality. Local build/unit results do not establish production latency or availability.
7. Verify live sitemap/robots, social preview imagery, canonical URL and organization/footer social links. The earlier live SEO findings are not repaired or certified by these analytics changes; the unpublished GPT V1 content is not silently published.
8. Historical missing action/binding/consent evidence remains unknown. Do not backfill guessed identities or replay uncertain historical deliveries. The current-version Event Map is not a complete publication-history report.
9. Browser retention is best-effort: offline storage limits, storage denial, ad blockers and a visitor never returning can prevent delivery. Provider receipts, not configuration or a browser queue return value, establish observed acceptance.

## Validation record

- Release build of AgentPortal.Tests and referenced applications: passed, zero warnings/errors; resumed incremental build also passed.
- Website JavaScript suite: 213 passed, zero failed.
- Focused server suite: 62 passed, zero failed.
- Broader server suite: 650 passed; one obsolete assertion pinned the mutable direct-release request to the September 29 mobile-shell release. Removed that historical assertion because canonical release scope has dedicated coverage in scripts/test-release-policy.py. The release request itself was not changed.
- Resumed MarketingManagerCentralizationTests: 10 passed, zero failed.
- Canonical ReleaseScopeSelection suite: 14 passed, including shared dependency expansion and unknown-target rejection.
- git diff --check: passed.

Compatible successful checks were preserved; the server-wide suite was not redundantly restarted for removal of the obsolete test. Exact-head remote CI and all live-proof obligations above remain required.
