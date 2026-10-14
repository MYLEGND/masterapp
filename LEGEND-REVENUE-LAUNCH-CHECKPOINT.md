# LEGEND revenue-launch checkpoint
Updated: 2026-10-13 (America/Phoenix). Business client identity and linked-owner managed-account repair: focused source verification complete; independent acceptance and remote identity recorded in the session handoff. Production release remains separately gated. Prior revenue/release evidence is preserved below.

## Authority and continuation
Repository: MYLEGND/masterapp. Release authority: Legend/fast-track-release.
Implementation branch: isolation/legend-revenue-launch-20261013.
Pinned source/base: dc10c7aabbba6cd5b516071c9e16d32dafeba9a6.
Remote branch was absent, then created at this exact source. Readback compare: identical, 0 ahead/0 behind. Payment integration commit ddb03578cf26c2e8076e0e34f03c271df5166bc6 is an ancestor (163 commits ahead, 0 behind, merge base equals payment commit). No cherry-pick or completed work restarted.
Setup checkpoint: af5384537623b84055cea245a83b35f5b5bd1f7d. Work Item 01 resumes that exact head and preserves its source ancestry. See the implementation SHA and evidence below; obtain the latest checkpoint SHA from this file's commit history. No PR, merge, deployment, charge, DNS or production-data mutation performed.

## Mandatory operating and acceptance contract
One bounded assignment at a time. Astra owns investigation, design, scope, independent review and sole technical acceptance. Codex alone owns production implementation, cleanup and focused test execution; disclose if delegation unavailable. Setup required no implementation agent or code tests. Work Item 01 delegated production changes and focused test execution exclusively to Codex; Astra independently reviewed the source diff and regression evidence.
Identify existing authoritative owner before edits; all affected consumers must use its contracts. No symptom-masking specificity escalation, !important, duplicate handlers, route interception, redundant middleware, fallback branches or runtime overrides as repairs. Repair/replace at the source; no stacked active implementation or patch-on-patch development.
Remove superseded implementations, dead code, conflicting registrations and redundant authority only after proving consumers and replacements. Preserve legitimate compatibility adapters, security controls, tenant content/styling/configuration/permissions, customer data, credentials, subscriptions, published versions and live Parfait.
Verify frontend/backend/persistence/published-runtime ownership without competing writers. Codex supplies focused diff and tests; Astra independently reviews ownership, removed duplication, consumers and regression coverage. COMPLETE requires functional verification AND canonical cleanliness. Unsafe consolidation is BLOCKED with exact reason, never accepted as a temporary override.
Reuse services, commits and compatible evidence; do not repeat successful gates. No broad audits/builds, speculative improvements or parallel overlapping investigation. Accumulate accepted work on this branch; no production-branch edits, merge, deploy, cutover or destructive operations without subsequent authorization. Every assignment reports PASS/PARTIAL/BLOCKED, reused/changed work, exact SHA, validation, risks and next assignment. Maintain this one checkpoint.

## Engineering continuity repair — 2026-10-13 (America/Phoenix)

Resumed the exact durable WI04C checkpoint
`4e90281da5ffd73e0054d4daa55559e4f60db3c2` on the existing isolated revenue branch.
That checkpoint and WI01–WI04C implementations/evidence remain ancestors. This assignment
changes only AGENTS.md, the existing Git synchronization utility and its focused tests,
and this checkpoint. No application business logic, WI05, release pipeline, merge or
deployment is included. Obtain this continuity revision's exact SHA from Git history and
actual remote branch readback; this file is not a second mutable HEAD registry.

The existing utility now supplies explicit session preflight/recovery/completion and
full-bundle verification using native Git. AGENTS.md documents the command contract,
acceptance boundaries and storage limitations. New conversations resume the actual
GitHub branch after ancestry verification; unpublished work must be preserved first.
Native macOS/Linux Git and Python 3.9+ are required; GitHub CLI is not required. No
privileged dependencies or credentials are installed.

Focused verification: 56 disposable-repository tests passed on macOS, including all 34
existing launcher/native tests and 22 continuity tests. Coverage includes fresh remote,
unpublished/dirty work, missing authentication and no GitHub CLI dependency, advanced
remote and divergence, failed/ambiguous push, exact authorized push readback, full
bundle export/restore and checksum, invalid/missing/incremental/wrong-ref bundles,
rejected temporary storage, dirty restoration, remote movement, branch-switch races and implicit tag/submodule push suppression.
No application tests or prior accepted WI validations were repeated. Linux is supported
by the native Git/Python/fcntl interfaces but was not executed in this Mac session.

Astra independently reviewed the diff, found a recovery branch-switch race, and reviewed
its correction plus stronger real bundle recovery tests and explicit push-scope flags. No remaining blocking source
findings were reported. Astra also ran the earlier 48-test utility suite and a disposable
race reproducer before noting the existing Codex-only test-execution role; final focused
evidence is Codex's run. Storage durability still requires genuine operator confirmation
and external readback; local verification cannot prove cloud retention or cross-machine
access. Session modes require an isolation branch and exclusive checkout use. Native
transport operations time out safely; large/slow repositories may need explicit follow-up.
Required integrity/security/release gates remain mandatory before any future merge.
Prior release gates and historical evidence below remain unchanged.

## Readiness map
PARTIAL means implementation/evidence exists but full functional and canonical acceptance is not established. BLOCKED means recorded prerequisites prevent acceptance; historical configuration observations require fresh read-only verification, not assumed current state.

| Area | Existing owner and reused evidence | Readiness / missing proof |
| --- | --- | --- |
| Subscription purchase | Infrastructure/Billing/MasterAppBillingOrchestrator.cs owns billing through IBillingGateway and entitlement service; ClientApp/Services/SubscriptionActivationService.cs delegates activation to IBillingOrchestrator. Exact-source files inspected. | PARTIAL: three concrete source defects repaired and focused local regression proof recorded below. External sandbox checkout, signed provider delivery and live ClientApp sign-in remain unproven; merchant checkout evidence is not platform billing proof. |
| Tenant onboarding | CommerceBusinessProvisioningService owns creation/ownership; BusinessWorkspaceAccess and WebsiteBusinessAccess enforce actor/membership/capabilities; existing ClientApp subscription/lifecycle policies remain authoritative. | PARTIAL: WI02 repairs and focused local proof below; live Entra/ClientApp/Studio and SQL Server verification remain outstanding. |
| Website publishing | Shared legend-public-cms.js editor/hydration, WebsitePlatformController publication authority and existing WebsitePageCompiler/render-business compiler preserved. | PARTIAL: WI03 direct state repairs, 37 focused tests and three compiled viewport checks below; live per-site editing/publication, browser visuals and server/SQL concurrency/tenant proof remain open. |
| Protected website tracking | AnalyticsEventCatalog, shared tracking.js, WebsiteTrackingProxyAuthority/UnifiedAnalyticsWriter, canonical eligibility/projection and existing reporting queries. | PARTIAL: WI04A repairs false acknowledgments, site/origin fallthrough and nested-metadata reporting authority; 160 focused tests passed. Live runtime/SQL, provider acknowledgment/corrections (04C) and historical gap reconciliation remain open; focused inquiry lifecycle proof is recorded in 04B below. |
| Inquiry delivery | WebsiteInquiryAuthority/WebsiteLeadSubmission, CommerceWebsiteInquiry, WebsiteLifeLeadCaptureService, existing email/app workers, recipient resolver and template; unchanged canonical conversion writer. | PARTIAL: WI04B direct owner/recovery/completion/receipt repairs and 115 focused checks passed. Text-form/CRM/conversion and local notification state verified; live SQL/runtime/inbox/device delivery, public media and authorized SMS remain open. |
| Advertising measurement and reconciliation | CanonicalAdvertisingEventProjection/CanonicalMarketingEligibility, existing owner-bound connections, provider dispatchers, MetaSendAuthority, MarketingMeasurementEvidenceService and canonical outcome/reporting projections. | PARTIAL: WI04C repairs and 161 unique focused checks below. OpenAI and Meta external configuration read-only evidence exists; MasterApp owner binding, event-specific live receipts, downstream attribution, SQL Server and commerce refund/provider-correction proof remain open. |
| Merchant Square connections | CommerceCore/CommerceServiceRegistration.cs calls AddMerchantPayments and registers MerchantCheckoutService, MerchantPaymentCompletionService and reconciliation worker from shared payment infrastructure. PR 581 merged; 38124421310 focused payment/commerce/package checks passed. | PARTIAL source readiness; launch acceptance remains BLOCKED. WI05A observed all six production Square configuration names as enabled Key Vault metadata; effective loading/provider registration and current business connections remain unverified. WI05B corrects capability/settlement reporting and canonical Payments navigation. Sandbox OAuth, signed webhook/isolation/checkout/refund proof and production verification remain open. Stripe/PayPal remain Coming soon. |
| Shared commerce | CommerceCore shared registration/catalog/order/automation/management runtime; permanent CommerceBusiness identity. Payment integration ancestry proven. | PARTIAL: shared implementation and prior focused CI exist; current two-tenant management/auth/order/refund/automation and production routing proof incomplete. Preserve required legacy compatibility until dependencies are proven migrated. |
| Parfait cutover | Existing docs/parfait-canonical-transition/canonical-internal-ownership-and-cutover.md is the detailed domain transition evidence source, not a new implementation. Original tenant retained; durable Oct 11 checkpoint records full no-overwrite 31-file/28,981,133-byte staging readback. | BLOCKED: runtime storage/cutover recorded disabled; merchant readiness, final mutable-state reconciliation, designed publication, www binding, visual/auth/payment proof, duplicate-draft resolution and retirement dependency proof outstanding. Preserve legacy host and current customer draft. Do not redo staged copy or SQL. |

## Reused verification and production evidence
- Current source AGENTS.md read through authenticated GitHub, replacing stale local guidance.
- PR 581 is merged at ddb03578cf26c2e8076e0e34f03c271df5166bc6. Its old PR body migration blockers are superseded where later durable evidence records completed schema; do not re-open them solely from old prose.
- Workflow 38124421310: build-and-focused-tests success, including scoped payment ownership/replay/provider contracts and exact Parfait/Protect package inspection. Historical source proof, not exact current-head revalidation.
- Detailed cutover document records SQL completion in 38157422917 attempts 2/3, and five-app publication at 3017d7e7c5527b1f34dc3b120a7d166b90c2a053 in 38161033893. These are retained documentary records, not new live probes.
- Fresh GitHub job readback of 38323933521: admission and read-only already-ready schema reconciliation successful; SQL mutation skipped; Website, Protect and ClientApp publication successful; Portal publication/reconciliation failure; routing skipped.
- Fresh GitHub job readback of 38329876334: Portal GET-only installed-file comparison successful. PR 624/625 document 680/680 parity against original dba66284ff23e973dc28aa2572da6224c601ee65 package, without a provider deployment ID. Do not fabricate a receipt, replay publication, or equate content parity with functional acceptance.
- PR 624/625 are merged according to GitHub search. Their future transport/evidence repairs do not turn the historical red publication job green.
- Setup reran no tests, builds or workflows. Work Item 01 adds the focused local evidence below; no deployed application function is declared COMPLETE.

## Work Item 01 — canonical subscription activation and billing
Status: PARTIAL. Astra accepts the bounded source repairs and canonical consolidation with the recorded focused test result; revenue-launch acceptance still requires external evidence. No fourth feature expansion.
Implementation commit: fdab392d3e404da8e3b323006c7cc5f077edc0ce. Final focused result: 171/171 passed, zero failures/skips; BillingCentralizationTests 91, ClientAppSubscriptionActivationTests 47, HouseholdMembershipServiceTests 30, SquareBillingGatewayTests 3 (58s). Astra independently read the TRX counters and reviewed the focused diff; Codex alone executed tests. Local artifact: /workspace/scratch/b20142940ac1/codex-billing/TestResults/wi01-final.trx.

Canonical path: AgentPortal ClientsController ownership/offer authorization -> ClientSubscriptionOfferPricing and IBillingOrchestrator -> hashed invitation in SubscriptionActivationService -> MasterAppBillingOrchestrator -> SquareBillingGateway -> persisted subscription/payment attempt -> BillingEntitlementService -> ClientIdentityAccessService and ClientSubscriptionActiveHandler. Existing CommerceBusinessProvisioningService remains business ownership authority. Signed webhook ingress and BillingReconciliationService/worker remain the provider-confirmation/recovery authority. No schema, migration, provider configuration or service registration changed.

Reused: server-selected price/currency and account ownership, invitation validation, payment idempotency/indexes, gateway, authenticated webhook validation, reconciliation, renewal/cancellation, identity linkage and tenant workspace authorization. Existing tests and services were extended, not rebuilt.

Changed and independently reviewed:
- Entitlement evaluation previously accepted expired/absent grace deadlines and could let a future grace date override terminal states. Household fallback could grant access after explicit membership/household denial. The existing evaluator was moved into BillingStateMapper; the old evaluator and duplicate household status predicate were removed. Entitlement evaluation, refresh and household invitation eligibility share this owner. Explicit denials cannot refresh an owner's projection; legitimate missing-membership hydration remains supported without reviving inactive households.
- Initial ambiguous payment results could become ActivationFailed and permit another charge, while an interrupted initial attempt was not selected by the existing reconciliation worker. The orchestrator now persists platform management and reconciliation state with the original attempt before submission. Only verified successful COMPLETED results with a payment ID activate; definitive failures/cancellations remain distinct. Pending, contradictory, retryable and interrupted results retain the original attempt for reconciliation. The gateway's actual HTTP_400/CARD_DECLINED contract is covered. No alternative worker or writer was added.
- A valid invitation could still activate a superseded or otherwise unavailable offer. ClientSubscriptionOffer now owns one status/effective/expiry predicate, used by activation context and the orchestrator before provider calls or new subscription writes. Existing already-active replay behavior is retained. Exact effective/expiry boundaries and rejection of an old superseded price are covered.

Focused evidence: entitlement regression baseline 26 failures/6 passes before repair; initial payment baseline 10 failures/2 passes, with additional reproduced decline, cancellation-token and contradictory-result failures; offer baseline 16/16 failures before its guard. Codex ran the final linked-source harness against actual repository billing, household, EF model and ClientApp activation/identity sources plus existing test classes. It uses local fixtures/mocked provider responses; it is not a Square sandbox transaction. A broad Infrastructure build was stopped for memory pressure; no full host build is claimed. No production test workflow was run.

Exact remaining proof: no configured authorized Square sandbox app/location/token/webhook and test-tenant runtime were available in this session. Provider checkout, real authenticated webhook delivery/replay, browser OIDC/ClientApp access and purchase-to-business ownership across two live test tenants were not executed. Existing focused tests exercise renewal, cancellation, replay, tenant/identity separation and payment state transitions locally; SQL Server uniqueness/concurrency, full AgentPortal administration route integration and deployed runtime configuration remain unverified. No real payment, customer-data change or credential exposure occurred.

## Work Item 02 — business-client onboarding and tenant isolation
Resume/source: 3a5dd6dd1e27c8ee27914ff97832415cefaf122b. Status: PARTIAL; bounded source acceptance and local proof do not establish live onboarding. Codex implementation: 78773f8c42cb749c8b0d11536a2c8ee6c947e949. Focused final result: 58/58 passed, zero failures/errors/skips (14s): BusinessClientOnboardingTests 49 and existing ClientProfileControllerTests 9. Astra read back /workspace/scratch/b20142940ac1/codex-business/TestResults/wi02-final.trx.

Canonical owners confirmed: AgentPortal ClientsController registers profile/AgentClient/business/offer/invitation in its existing transaction; ClientEntraLifecycleService and ClientIdentityAccessService own identity activation/binding. CommerceBusinessProvisioningService owns permanent CommerceBusiness IDs, account membership and linked personal owners. Platform entitlement remains WI01's ClientSubscription/BillingEntitlementService authority; onboarding callers do not create a competing commerce billing subscription. EffectiveClientContextService, WebsiteBusinessAccess and BusinessWorkspaceAccess resolve actual actor/profile/membership/capabilities; ClientSubscriptionAuthorizeFilter/handler and ClientAccountLifecycleAuthorizeFilter enforce subscription and account state. BusinessWorkspaceService owns settings writes to business-scoped CommerceBusinessStorefrontSettings with revision checks. Existing hashed, expiring Website Studio handoff is reused.

Three proven defects repaired directly in four existing production files:
- Removed ProfileController's class-wide subscription exemption, retaining it only on profile/recovery actions. Narrowed the existing lifecycle filter's whole-controller exemption to those actions. Business setup/management/Studio session issuance now uses the existing policies; no added middleware or entitlement writer.
- Added the legitimate account role to the existing workspace administrator decision. Settings/team/CRM work for the provisioned business account; capability flags, actor ownership, lifecycle and active membership checks remain in force. Personal-owner identity and ownership shares remain separate.
- Removed setup's visibility-based existence check. Canonical provisioning validates the persisted business-account profile/email and reads all associations, including revoked/inactive/dangling/conflicting associations. Valid legacy associations return unchanged; unsafe associations fail closed. New account provisioning uses a profile-derived stable key under the existing unique index. Exact business-key collisions become an explicit concurrency refusal; a fresh request reads the winner. No nested transaction, new schema, second writer, automatic reassignment or existing key/ID rewrite. General personal-owner multi-business and platform-owner paths are preserved.

Evidence: unchanged-production baseline 46 cases, 21 failed/25 passed; same core set passed 46/46 after repairs. Final focused tests add stable-ID replay, identity mismatch, stale/revoked/conflicting associations, two-tenant actor/ID denial, capability limits, actual subscription/lifecycle enforcement, recovery access, SQLite unique-key race/fresh retry and caller rollback, SQL exception classification, and actual CustomizeAsync persistence/isolation/revision rejection. Existing ClientProfileControllerTests cover supported profile flows and correct-business Studio handoff. Astra independently reviewed the four-owner diff, removed authority, affected consumers and test receipts; Codex alone implemented and ran tests.

Evidence boundaries: source-linked local .NET harness, real affected source and EF model; EF InMemory plus SQLite with Foreign Keys=False for the uniqueness/transaction fixtures. SQL-error shapes are fixtures, not live SQL Server. Unexercised analytics/marketing/website-projection dependencies in the scratch harness are strict throwing doubles; no settings, provisioning, authorization or persistence implementation was substituted. Full app/AgentPortal registration-controller integration and live identity provider were not run. Registration's existing transaction and duplicate-email protection were source-reviewed; WI01's 171 tests are retained without replay or billing changes.

Remaining release checklist: authorized live/test Entra registration and sign-in; deployed ClientApp/Studio handoff and membership revocation; SQL Server uniqueness/concurrency/FK behavior and two-tenant runtime settings/access. WI01 Square sandbox checkout, verified provider webhook/replay, real purchase-to-entitlement and live payment/access checks remain mandatory before revenue-readiness acceptance. No production data, identities, memberships, settings, credentials, payments, migrations, merge or deployment changed.

## Work Item 03 — Website Studio persistence and responsive publishing
Resume/source: 5097c1c541cec8b6c6dcee6fbd4800c24b44e51d. Status: PARTIAL. Astra accepts the bounded canonical source repairs and focused local evidence; full runtime acceptance remains open. Codex implementation: 441743e4a21fe1aaf073909af9711ae2b8b4d88c. Codex alone edited product code and executed tests; Astra independently reviewed the complete affected diff, existing ownership contracts and test receipts.

Canonical owners preserved: SHARED/WebsitePlatform/legend-public-cms.js owns editor state, breakpoint style/layout selection, scoped draft mutation requests and published hydration. Its single v3 document keeps shared content/base styling and per-breakpoint presentation. Existing LEGEND and Protect packaging consumes this same source; scoped businesses use the shared runtime. WebsitePlatformController owns authorized mutations, exact saved-revision publication and request-ID reconciliation; WebsiteTicketAuthorization/WebsiteBusinessAccess retain actor and tenant checks. WebsitePageCompiler and render-business.mjs retain the existing compiler/shared-renderer path. WebsiteContentState/WebsiteContentVersion retain owner/site uniqueness, revision concurrency and immutable publication records. No backend, schema, CSS, compiler, authorization or tenant-content changes.

Confirmed defects repaired directly:
- The Desktop shortcut selected shared Base and falsely appeared selected for tablet/custom/Base. It now selects the existing desktop breakpoint; selected-state reporting matches the actual breakpoint. Explicit Base remains available for intentionally shared styling; shared content stays shared.
- Publication success unconditionally replaced local state and cleared dirty status, losing edits made while awaiting confirmation. It now records the submitted snapshot/revision, advances the saved baseline from the confirmed response, preserves newer local edits and saves them through the existing draft mutation path. The confirmed publication remains the submitted saved revision.
- A valid current original-request confirmation could leave newer edits without autosave after their timer had expired. It now resumes the same draft scheduler only after current-request confirmation; missing/conflicting/error/noncurrent receipts do not trigger that resumption. Existing timer scheduling was consolidated into one helper, without another writer or publish handler.
- The existing unload guard ignored unapplied Source edits. It now covers both dirty document state and unapplied Source. Existing navigation save/Source safeguards remain unchanged.

Cleanup: removed Desktop/Base conflation and the unconditional publication state reset; replaced duplicate timer scheduling with one helper and amended the existing unload listener. No parallel implementation, override, !important, new middleware or competing persistence/publication authority. Existing field IDs, inquiry bindings, signals, metadata, assets, locked components, published records and business-specific design remain untouched.

Focused evidence: three initial executable JSDOM regressions failed against unchanged source. Final run: 37/37 passed (14 new and 23 existing), zero failures/cancellations/skips, 28.992s. Actual shared editor tests cover independent desktop/mobile styling, shared content, save/reload, delayed responses with/without documents, draft/publication separation, next-draft 409/500 refusal, same-request recovery/idempotency, source unload, existing responsive layout and hydration. Integer-revision fixtures reject stale revisions and distinguish draft from published snapshots. Tests use mocked HTTP persistence, not a running database. Receipt: /workspace/scratch/b20142940ac1/codex-website/final-focused.log. Three existing hydration fixtures emit fetch-not-defined console diagnostics; passing assertions do not establish live fetch behavior.

Actual unchanged render-business.mjs compiled a LEGEND fixture using the repaired shared source in scratch. JSDOM hydration passed at 390/1024/1440 pixels, verifying breakpoint width/font/color where specified, shared text, same DOM identity and mobile-to-desktop resize. Compiler receipt: /workspace/scratch/b20142940ac1/codex-website/compiler-parity.json. This is DOM/style proof, not computed browser layout or three-tenant deployment parity. No full repository build or production workflow ran.

Reused/source-reviewed evidence: controller revision/fingerprint conflicts, scoped idempotency, compile-before-publication and EF concurrency protection; existing WebsitePublishingAuthorityTests for stale drafts, repeated publication, active immutable HTML and forged tickets. These backend tests were not executed in WI03 because their current host dependency graph requires broader integration setup. WI02's unchanged business-access tests are retained without replay; no new executed backend tenant-isolation claim.

Remaining release checklist: authorized real-browser save/reload/publish and desktop/mobile visual proof for LEGEND, Protect and scoped businesses; actual two-tenant owner/revoked/unauthorized save and publish; server/SQL Server stale-revision/concurrency/idempotency and failed-compile preservation; deployed public version identity/cache parity. Chromium was unavailable locally, and full controller/database integration was not run. Carry forward WI01 Square sandbox checkout, verified webhook/replay and real payment-to-access proof, plus WI02 live Entra/ClientApp/Studio and SQL Server validation. No production writes, deployment, merge, domain change or Parfait cutover.

## Work Item 04A — protected website events and tracking integrity
Resume/source: 97f8c8551c2eb0073c1e0c4b6b54b952936514ba. Status: PARTIAL. Astra accepts only the reviewed source repairs and focused local evidence; external event lifecycle, deployed SQL/browser behavior and provider acknowledgment remain unverified. Codex alone implemented and executed tests; Astra traced contracts and independently reviewed the diff and receipts. Implementation: 9cc291b7ae59afdeaae5417285da2f14038f4168.

Canonical owners reused: SHARED/Analytics/AnalyticsEventCatalog.cs defines allowed browser/server events; SHARED/WebsitePlatform/tracking.js owns the browser envelope, scoped attribution, queue and triggers. WebsiteTrackingProxyAuthority resolves runtime ownership and protected published bindings; UnifiedEventMapper/UnifiedAnalyticsWriter stamp and persist canonical scope with replay checks. CanonicalMarketingEligibility, CanonicalAdvertisingEventProjection and existing Meta/OpenAI/external destination bridges remain the eligibility/projection/delivery authorities. AnalyticsScopeQueryExtensions and AnalyticsQueryService remain the reporting owners. WebsiteEventMapQuery/MarketingMeasurementEvidenceService retain observed, projected, blocked, receipt-unverified and acknowledged distinctions. No alternative tracker, listener, queue, event writer or provider engine.

Proven defects and direct repairs:
- tracking.js treated any HTTP 2xx as an acknowledgment, even malformed or mismatched receipts. The existing sender now requires status ok/duplicate_ignored and the same valid event GUID before removing the queued fact or publishing it to subscribers. Missing eligibility remains fail-closed for optional providers. Beacon and retry retain the original identity.
- Public ingestion allowed unknown/mixed-case site keys to fall into Protect attribution, and explicit Origin could fall through without a valid Protect runtime scope. Normalize/allowlist dispatch once and require the existing origin resolver for explicit-Origin Protect requests. Preserve intentional no-Origin compatibility, real owner resolution, immutable binding validation and the same writer.
- Reporting scope searched whole JSON for site/owner marker strings, allowing nested browser metadata to select the wrong report and missing valid whitespace-formatted root metadata. Existing Analytics/Meta/WebsiteLead predicates now query their established case-sensitive root keys with mapped SQL Server JSON_VALUE, guarded by ISJSON/CASE for malformed historical JSON. Typed owner guards and no-site behavior remain; Founder site switching never reassigns CRM ownership.

Cleanup: removed false HTTP-only acknowledgment and permissive site fallthrough decisions. Removed the obsolete BuildJsonMarker helper and replaced its whole-document authority matches in the existing query owners. No existing historical events, tenant settings, published content, CRM ownership, payment state or legitimate compatibility adapter was rewritten.

Focused evidence: browser regression failed before repair; final 52/52 JavaScript tests passed, including real shared runtimes/adapters with mocked HTTP/provider calls, three Studio suppression modes, consent, repeat initialization, protected bindings, retries and stable correlation. Existing catalog tests 8/8. Ingestion baseline reproduced six defects (32 passed); final 74/74 tests passed against actual authority, runtime resolver, published-business fixtures, mapper/writer and EF model. Every server-only catalog definition plus conversion aliases is rejected at public ingest; two business origins retain ownership and cross-owner event replay is rejected. Reporting baseline: four genuine Analytics/Meta predicate failures; two initial lead failures were invalid fixtures and are not counted as defect proof. Corrected lead fixtures then failed 2/2 against the exact original query source. Final query packet 26/26 (3 existing +23 new), zero failed/skipped, verifies root-only values, whitespace/order/case, null/malformed/non-scalar/duplicate-key metadata, owner aliases/site switching and typed business isolation. Five actual SQL Server queries generated guarded JSON_VALUE/ISJSON/CASE without whole-document matching. Total distinct final results: 160/160 (52+8+74+26); Astra read receipts and generated SQL independently.

Evidence boundary: linked-source local .NET harness with EF InMemory; unexercised commerce resolver, external domain probe and shared-key persistence dependencies are strict scratch doubles. Provider browser SDK calls are stubs, not external acceptance. No live provider, production analytics event, database, ad account or deployment was used. Browser and catalog receipts were reused after backend-only changes. Receipts under /workspace/scratch/b20142940ac1/codex-tracking/: final-js.log, TestResults/catalog.trx, TestResults/ingest-final.trx, TestResults/query-final.trx and query-sql-translation.sql. Full host build and live SQL Server execution are not claimed.

### Event truth matrix
VERIFIED refers only to the stated local evidence. Source tracing alone is UNVERIFIED functional evidence. A dispatch/SDK invocation or HTTP acceptance is not provider acknowledgment; unavailable signals are never recorded here as zero.

| Existing event family | Canonical origin, identity and persisted scope | Destination / evidence boundary | Truth |
| --- | --- | --- | --- |
| Sessions, page/landing views, visibility and session end | tracking.js; stable ClientEventId/EventUtc, scoped session/visitor and attribution; shared ingest resolves public owner and stores AnalyticsEvent | First-party analytics; eligible page_view maps to Meta ViewContent and OpenAI page_viewed. Browser invocation is not provider acknowledgment. | VERIFIED local contracts; acknowledgment/site dispatch DEFECT REPAIRED; live UNVERIFIED |
| Referral/campaign/click attribution | Scoped current-session and first-touch storage; canonical runtime owner; backend owner resolution and normalized click references | Analytics attribution; consent/connection/mapping-gated provider matching | VERIFIED local browser contracts; live lineage UNVERIFIED |
| CTA, navigation, scroll, dwell, section/download and friction signals | Existing catalog/automatic triggers or protected published bindings; same event envelope and dedupe authority | First-party behavior; no automatic lead/purchase claim. Existing Meta behavior mappings are internal unless allowed by provider catalog. | VERIFIED local trigger/protection contracts; live coverage UNVERIFIED |
| Form views, starts, fields, attempts, failures and browser success | Existing form tracker, catalog and bindings; browser observations remain browser authority | First-party funnel metrics; browser success cannot authorize server Lead | VERIFIED local contract/classification; detailed lifecycle UNVERIFIED (04B) |
| Persisted inquiry / CRM lead | WebsiteInquiryAuthority, WebsiteLeadSubmission, CanonicalLeadEventIdentity; server-confirmed website_lead_submitted/lead_persisted; owner-bound lead and event identity | Meta Lead, OpenAI lead_created; configured Google/TikTok server mappings only after canonical eligibility | Source traced; full lifecycle/delivery UNVERIFIED (04B/04C) |
| Chat/contact | Contact interactions reuse CTA/form/verified inquiry paths; no dedicated chat-conversion definition found in shared catalog | No invented chat event or conversion count | UNVERIFIED; unavailable is not zero |
| Booking and appointment lifecycle | CanonicalCrmOutcomeService, AppointmentAnalyticsEventCatalog; appointment/status-stable identity, server timestamps and acquisition lineage | Booked/completed mappings from MarketingConversionDestinationCatalog; other lifecycle states remain existing internal facts | Catalog/source traced; live lifecycle/provider receipts UNVERIFIED |
| Storefront product/cart/checkout | ProductViewed browser observation; accepted cart and checkout commands through CommerceSignalService with scoped deterministic identity | First-party product view; server AddToCart/InitiateCheckout via existing conversion destinations | Source traced; action-to-ingest integration UNVERIFIED |
| Paid purchase / settlement | MerchantPaymentCompletionService accepts verified Captured attempt with matching amount, order/business and payment reference; CommerceSignalService writes stable Purchase | Meta Purchase, OpenAI order_created; configured Google/TikTok mappings. Checkout start is not purchase. | Source traced; current sandbox/live proof UNVERIFIED |
| Cancellation / refund / net revenue | Existing order payment/refund state and ParfaitOrderService/ParfaitInternalAnalyticsService net-revenue projection; historical purchase facts are not proof of current net revenue | No new refund event invented; cross-channel refund/correction and provider receipt reconciliation require explicit proof | UNVERIFIED; do not claim refund-adjusted marketing revenue |
| CRM qualification/application/issued/paid outcomes | CanonicalCrmOutcomeService and canonical outcome projection; stable server identities, state snapshots and correction markers | Existing destination catalog; corrections may require external reconciliation | Source traced; live outcomes/provider reconciliation UNVERIFIED |
| Consent and tracking diagnostics | Canonical measurement consent, GPC, consent-changed event and client_tracking_error; Studio exits before initialization | First-party diagnostic truth; consent controls optional providers; editor/materialization emit no production signals | VERIFIED local suppression/consent tests; live deployment UNVERIFIED |

| Selected-site reporting (cross-family) | Existing AnalyticsScopeQueryExtensions/AnalyticsQueryService; root site/owner metadata plus unchanged typed ownership | First-party Founder/tenant reports; no CRM reassignment | DEFECT REPAIRED locally; live SQL/runtime UNVERIFIED |

Historical limits: prior false acknowledgments or incorrect attribution may have left gaps or misclassified facts; this assignment does not reconstruct or rewrite historical events.

Remaining release proof: authorized live browser/runtime and SQL Server validation of corrected ingestion/reporting, two-tenant scope and published binding behavior; actual action-to-stored-fact lifecycle and owner delivery in 04B; consent-qualified provider dispatch/acknowledgment/rejection/deduplication and correction/refund reconciliation in 04C. No dedicated chat conversion was established from the shared catalog. Purchase history must not be presented as verified refund-adjusted marketing revenue. Carry forward WI01 Square sandbox checkout, verified webhook/replay and real payment-to-access proof; WI02 live Entra/ClientApp/Studio and SQL Server validation; WI03 live responsive editing/publication/version proof. No merge, deployment, live production writes, ads configuration, domain change or Parfait cutover.

## Work Item 04B — canonical inquiry, CRM and owner delivery
Status: PARTIAL. Local source corrections accepted by Astra after independent diff/consumer and focused-evidence review; full live/media/provider proof remains open. Resumed exact WI04A checkpoint 367030268ba52328086511e7b7f2f10cadeaf30a on the same branch. Codex exclusively implemented/tested; implementation commit: 995d9daae4d7b82f373a97f9cf3db033a1a7361a.

Preserved owners: legend-public-inquiry.js + shared public-inquiry-form.mjs; WebsiteInquiryAuthority and published runtime scope resolver; CommerceWebsiteInquiry/WebsiteLeadSubmission; WebsiteLifeLeadCaptureService/WebsiteLeadIntakeLink; WebsiteIntakeRecipientResolver; BusinessInquiryNotificationService; WebsiteLeadNotificationAuthority; WebsiteLeadAppNotificationService/NotificationEngine; WebsiteLeadEmailTemplate/IWebsiteInquiryEmailSender; unchanged WI04A CanonicalLeadEventIdentity/UnifiedAnalyticsWriter contracts.

Direct repairs and cleanup:
- Business in-app recipient selection formerly matched a global profile email without permanent membership/lifecycle authority. It now resolves an active primary membership's ClientProfileId and existing CRM workspace access; ambiguous, revoked, inactive, mixed or empty ownership fails closed. Removed the unrelated-profile/agent email fallback.
- App delivery's first-100 selection and two-day age cutoff could permanently hide later/older pending leads. The same worker now advances by bounded identity batches, checks its existing deterministic notification ledger, skips ineligible/internal rows, and caps new projections. No new queue or notification engine.
- Email recovery missed committed-but-unattempted inquiries and expired interrupted attempts unless CRM Status was NotificationFailed. The existing worker now recovers canonical inquiry kinds and expired attempts, excludes CommerceWebsiteInquiry-owned delivery before batching and advances past unavailable recipients. Removed the status-only/early-limit selection. Historical unrelated never-attempted forms are excluded.
- Notification completion could overwrite newer reclaimed leases and owner-managed CRM status. Existing claim timestamp now fences relational completion atomically; database-current Contacted/Closed states survive and stale tracked data is reloaded. Existing public wrappers, retry delay, sender and business-inquiry revision token remain.
- Browser acknowledgment now requires boolean accepted=true, replacing truthy acceptance that could confirm malformed responses. Existing fields, styling, in-place confirmation, retry identity and server-only conversion authority remain.

Focused proof: 115 passing checks, zero failures/skips: app recipient/backlog 34; email recovery/completion 33; inquiry lifecycle 29; business delivery 5 plus local MVC HTTP 1; browser/form 13. Nine failing baseline cases reproduced across recipient (2), backlog (2), recovery (2), late completion (2), and malformed receipt (1). Actual SQL Server query generation covers both worker selections. Eight SQLite cases exercise lease/revision concurrency and completion with foreign keys disabled. Local Kestrel/MVC canonical JSON returns 200/accepted=true, malformed/null JSON 400, text/plain 415. Existing 500 sequential retry/conflict pairs prove replay stability, not concurrent SQL submissions. Receipts: codex-business/TestResults/wi04b-app-final.trx and wi04b-email-cas-final.trx; codex-tracking/TestResults/wi04b-inquiry-final.trx and wi04b-business-http-final.trx; codex-business/inquiry-js-final.log (scratch evidence). Committed test classes make the focused assertions durable; linked-source harnesses reuse actual services and mock transport boundaries, without a broad application build. No provider transport used; mocked acceptance is not inbox delivery. SQLite relational tests do not prove SQL Server or foreign-key behavior. SQL generation is not database execution.

Readiness:
| Stage/channel | Evidence and exact limit |
| --- | --- |
| Text inquiry → CRM → conversion | Locally verified: correct published-site/business routing for LEGEND, Protect and business fixtures, consent/input rejection, scoped CRM access, retry identity and lead-correlated conversion timestamps. Standard JSON transport has local HTTP proof; deployed CORS/runtime proof remains open. Distinct inquiries retain distinct persisted intake/conversion identities; intentional contact merging is not inquiry deduplication. No new tracker/writer/event definitions. |
| Owner email | Durable queue/lease/retry state locally verified. Sent means transport accepted; inbox receipt unverified. Ambiguous provider responses can redeliver: no exactly-once external-email claim. |
| In-app notification | Permanent owner and deterministic ledger projection locally verified through existing engine contract. Live workspace visibility/device push/APNs/FCM receipt unverified. |
| Media | Standard public form/JSON request has no attachment field, public upload storage or inquiry-link contract. With-media workflow and scoped multipart/415 behavior are NOT verified; authenticated Studio uploads are not a substitute. |
| SMS | No configured/authorized SMS adapter or owner-recipient/consent contract was established in this inquiry notification path. No alternate sender added; SMS unverified. |

Before release: authorized live LEGEND/Protect/business text-form → correct workspace → consenting test-recipient delivery; SQL Server transaction/uniqueness/race/FK proof; supported media transport/storage/linking decision and validation; provider acceptance/inbox/device receipt reconciliation in 04C. Read-only review of historical pending inquiries is required before activating recovery changes, since previously stranded rows become eligible. Preserve all WI01 Square sandbox/live payment gates, WI02 identity/SQL gates, WI03 live browser/publication gates and WI04A live tracking/provider/correction gates. No production leads, notifications, ads events, migrations, merge, deployment, domain changes or Parfait cutover.


## Work Item 04C — canonical analytics and advertising conversion reconciliation
Status: PARTIAL. Astra independently accepts the bounded source changes, cleanup and focused evidence; this is not production acceptance. Resumed local and remote HEAD 41b22db4f2deaf71980be662114f9c3a5aafe58a on the same isolated branch. All five WI01–WI04B implementation commits remain ancestors. No intervening remote work was present at the pre-commit readback. Codex exclusively implemented, tested and committed implementation 51677cac8de57761187cef9336911a675816138b (34 source/test files). Obtain the final checkpoint SHA from this file's commit history.

Canonical ownership verified through targeted inspection: persisted AnalyticsEvent and the unchanged CRM/commerce action producers; CanonicalAdvertisingEventProjection and CanonicalMarketingEligibility; MarketingConversionDestinationCatalog; owner-bound OpenAiAdsAccountConnectionAuthority and MarketingConnectionStore; existing OpenAI/external dispatchers and MarketingDestinationDelivery; existing Meta projection/dispatcher with MetaSendAuthority; MarketingMeasurementEvidenceService, WebsiteEventMapQuery/trace evidence, CanonicalMarketingOutcomeProjection, UnifiedMarketingPerformanceService, BlendedGrowthEconomicsService and their existing dashboard/AI consumers. Browser event identity, consent/GPC, Studio suppression, root-level site attribution and verified inquiry facts reuse WI04A/WI04B evidence. No new tracker, provider, queue, reporting engine, business routing or migration.

Accepted direct repairs and cleanup:
- OpenAI health formerly interpreted malformed successful responses as available with zero events. Availability now requires a valid object/data-array contract; valid empty samples remain empty, malformed/unavailable samples remain unknown.
- OpenAI HTTP-only receipts were labeled provider accepted. Reporting now separates transport acknowledgment from unverified provider acceptance/attribution. Existing stored `sent` receipts remain unchanged as resend fences; historical transport success is not replayed or relabeled as confirmed provider acceptance.
- Google transport and reporting accepted missing/unmatched result bodies. Both now use the existing evidence policy's single matching action/click/timestamp parser. Explicit rejection, unverified retryable response and matching acceptance remain distinct; retries retain the canonical identity and payload. Historical HTTP-only receipts remain preserved and unverified.
- OpenAI/external late completions could overwrite reclaimed delivery state and lost interrupted-attempt evidence. The existing ClaimToken is now an EF concurrency fence; attempts are persisted before transport, active leases are skipped, expiry invalidates stale claims, and stale tracked completions are discarded. SQL Server generated schema is unchanged.
- Meta stable IDs could fall through to temporal lead/session matching, or be outranked by temporal memory keys. Stable identity now governs retries and distinct conversions. Canonical PolicyPaid→Purchase claims/sent lookup resolve through the existing destination catalog. Dispatcher requests without a persisted source fail closed; memory nesting cannot bypass dispatcher durability or reuse a completed reservation.
- Meta had competing completion writers: its authority released the claim before the dispatcher wrote receipt metadata. MetaSendAuthority now atomically persists the full receipt, presence flags, sent state and lease release under the owned token. Removed the dispatcher's unconditional provider-result writer and moved its metadata merge to that authority. Pre-dispatch decisions use guarded writes; interrupted transport retains a recoverable lease without inventing a receipt. Active leases cannot fill the eligible batch, and streamed readers close before writes. Root-only retry counters, limits and metadata remain authoritative.
- Unavailable/partial provider reports and unrequested conversion counts formerly became zero. Existing service/AI contracts now preserve null; malformed/incomplete OpenAI reports fail closed. Valid empty reports, explicit zeros and existing numeric aliases remain valid. The existing UI renders unknown values as unavailable.
- Missing monetary values formerly became recorded revenue and ROAS of zero. One nullable recorded-revenue projection now feeds existing totals, economics, AI calibration and priority averages. Known zero and existing CRM correction/deletion reconciliation remain valid; missing or unsupported USD values propagate as unknown. Existing labels/notes identify recorded revenue and explicitly disclose unverified commerce refunds, cancellations, current net revenue and provider correction acceptance. Historical business facts and existing CRM/pipeline compatibility helpers are preserved; no refund engine or adjustment events were added.

Focused proof: 161 unique passing checks, zero final failures/skips: 155 C# (OpenAI health 18; transport evidence 5; Google receipt contracts 19; shared delivery leases/schema 26; Meta 58; reporting availability/isolation 21; recorded revenue 8) and 6 JavaScript checks. Repeated executions are not counted. Meta includes CAPI 16, identity 13, original authority 5, dispatcher 6 and receipt/lease 18. Health baseline reproduced ten failing cases. Fixture corrections during Google/revenue testing are not claimed as production defects. Actual modified C# owners, including AI builder/manager, compiled; final Shared build had zero warnings/errors.

Evidence logs: `/workspace/scratch/b20142940ac1/codex-tracking/` — `measurement-final.log`, `evidence-final.log`, `evidence-ui-final.log`, `google-final.log`, `lease-final.log`, `lease-schema-final.log`, `meta-contracts-final.log`, `meta-receipts-final.log`, `meta-counter-final.log`, `availability-final.log`, `availability-alias-final.log`, `availability-ui-final.log`, `revenue-final.log`, `revenue-isolation-final.log`, `revenue-ui-final.log`, `shared-revenue-build.log`. Committed tests preserve the assertions. Linked-source harnesses exercise actual owners with mocked provider transports; unused collaborator declarations and registration-excluding source prefixes avoid an unrelated full application build. SQLite race fixtures disable foreign keys; AI projection fixtures also use EF InMemory. Captured SQLite claim predicates and SQL Server model/schema equivalence are not live SQL Server/FK/concurrency execution. UI helper/label checks are not real-browser acceptance. Adapted existing `ScopedMarketingMeasurementTests`, `ExternalAdsCanonicalIntegrationTests` and `LaunchAuditReliabilityTests` were not executed as whole classes; dedicated focused receipt/evidence tests cover the changed behavior. No broad build or unrelated workflow was run.

Read-only provider evidence during this session:
| Provider | Verified evidence | Exact limit |
| --- | --- | --- |
| OpenAI | Accessible MYLEGND, LLC account `adacct_6ab78a328a20819d92c88b989404c4fb`; Legend Conversion Pixel source `cds_6ab8e68ce8c0819d9ac236a8f4553592`, pixel `8T9udjcARZ1SG6wKM5PqLN`; nine unarchived event settings. Lead setting references the LEGEND business-inquiry and Legend Legacy Protection campaigns. Recent-events endpoint returned a valid empty sample. | Recent sampling covers only the endpoint's short window; empty is not historical zero or proof of delivery failure/success. MasterApp permanent-owner/account/pixel binding and event-specific server receipts were not accessible. Public CAPI documentation did not establish an acceptance response-body contract; HTTP acknowledgment remains transport-only. |
| Meta | HYPD exposed two accounts. Account `act_1302964771864711` belongs to MyLegnd LLC, USD/America-Phoenix. Legend Lead Pixel `2163590517771481` last fired `2026-10-12T19:56:30-0700`; Life Insurance Pixel `2134710060604657` last fired `2026-09-18T15:40:10-0700`. Two active offsite-conversion ad sets optimize LEAD using Legend Lead Pixel. The other accessible account returned no pixels. | This verifies external inventory/configuration and reported pixel activity, not a particular business action's CAPI receipt, browser/server deduplication, MasterApp binding or downstream attribution. Fresh official response documentation returned HTTP 429. Existing `events_received > 0` acceptance behavior is preserved and adversarially mock-tested. |
| Google / TikTok | Existing code for explicitly mapped, owner-bound destinations was inspected. HYPD returned no connected Google Ads accounts. | Connector absence does not prove absence in MasterApp. No authenticated MasterApp configuration/receipt session or live TikTok connection was established; code presence alone is not a connected destination. Google receipt evidence is mocked. |

Contract references checked: https://developers.openai.com/ads/conversions-api ; https://developers.openai.com/ads/api-reference/conversion-setup ; https://developers.openai.com/ads/api-reference/insights ; https://developers.google.com/google-ads/api/reference/rpc/v22/UploadClickConversionsResponse ; https://developers.google.com/google-ads/api/reference/rpc/v22/ClickConversionResult . Meta documentation retrieval at https://developers.facebook.com/docs/marketing-api/conversions-api/using-the-api/ was rate-limited, not accepted as a fresh receipt.

Remaining WI04C release proof: authenticated read-only MasterApp owner/site/account/pixel bindings and stored receipts; authorized provider sandbox/validate-only or existing genuine-event acknowledgment/rejection evidence; real browser/server deduplication and consent/Studio suppression on deployed code; SQL Server/FK/reclaim/concurrency execution; selected-site reporting readback across LEGEND, Protect and two businesses; delayed/downstream attribution and historical-gap reconciliation. Existing CRM `providerCorrectionStatus=requires_reconciliation_if_previously_delivered` remains a reconciliation marker, not an external correction receipt. Commerce cancellation/refund/current-net reconciliation and provider adjustment acceptance remain unverified; recorded purchase history is not net revenue.

All previous gates remain open exactly as recorded: WI01 Square sandbox/signed webhooks/live subscription and SQL concurrency; WI02 live Entra/ClientApp/Studio identity and SQL tenant isolation; WI03 browser desktop/mobile save/publication/version and SQL proof; WI04A deployed ingestion/reporting; WI04B live scoped inquiry/SQL-FK/backlog review/owner inbox and workspace receipts. Public inquiry media storage/linkage and authorized SMS remain explicit launch-scope decisions, not completed capabilities. No fake production conversion, live ad/account change, customer-data mutation, charge, migration, merge, deployment, DNS change or Parfait cutover occurred.

## Work Item 05A — accepted read-only merchant readiness audit

Resumed continuity checkpoint `3640de5bd9b75c7a6276a66024f8ed8afeaa1e0b` without
changing source, running tests, migrations or provider actions. Astra identified two
bounded defects: Square card-processing readiness was reused as settlement readiness;
Payments navigation duplicated canonical settings roles and omitted `account`.

Existing owners/evidence were retained: shared CommerceCore merchant OAuth, scoped
credential protection, hosted checkout, signed webhook routing, immutable payment and
refund identities, completion/reconciliation, PR581 and prior migration/test receipts.
Founder application authorization does not connect independent sellers. Platform
subscriptions remain separate. Parfait retains its gated legacy checkout and credentials.

Fresh presence-only Azure evidence: Key Vault `masterapp-kv-1221` has enabled metadata
for all six `MerchantPayments--Square--Production` entries: ApplicationId,
ApplicationSecret, CallbackUrl, Enabled, WebhookSignatureKey and WebhookUrl. No secret
values were read. This supersedes the old claim that production configuration is missing;
it does not prove effective loading, Enabled's value, registration match or validity.
No sandbox entries were observed in the inspected inventories. Protect has configured
DataProtection BlobUri/KeyVaultKeyId; Parfait retains its separate Square settings.
Different application isolation names do not prove cross-host credential decryptability.
No current authenticated business inventory was available: historical zero connections
must not be reported as a current count. Management/preview webhook and cutover-worker
registration gates remain intentional, not defects to bypass.

## Work Item 05B — Square readiness and Payments authorization

Starting accepted checkpoint: `3640de5bd9b75c7a6276a66024f8ed8afeaa1e0b` on the same
isolated revenue branch. All prior accepted ancestry is preserved. Canonical preflight
passed with exact local/remote equality, clean worktree and native Git transport.

Codex repaired only the two accepted source defects and necessary consumers:
- Existing MerchantReadiness now represents unknown payment/settlement observations
  with nullable fields and independent automatic-transfer capability. Square evaluates
  card processing and automatic transfers separately for the verified selected location.
  Capabilities never establish a completed payout; Square settlement status stays unknown.
  A card-capable merchant without automatic transfers can remain eligible for payments.
- Existing readiness contract owns the shared eligibility predicate used by default
  selection, checkout and UI. Unknown card readiness fails closed; merchant identity,
  environment, currency, selected-location and provider availability checks remain.
  Other adapters retain their existing payment/settlement eligibility requirements.
- Existing cached-read boundary normalizes historical Square settlement assertions to
  unknown without modifying stored rows; new explicit transfer evidence is retained.
  Missing/malformed cached readiness is displayed as unverified, not success or zero.
- Existing BusinessWorkspaceAccess owns the reused capability evaluator. Controller
  navigation no longer has a separate owner-role decision; account/owner/platform-owner
  settings access and separately permitted team access use the same evaluator as routes.
  Ticket, actor, active membership, business and lifecycle boundaries are preserved.

Focused Codex evidence: 174 unique checks passed (70 synthetic adapter checks; 104 core
payment/authorization/Parfait compatibility cases). The smallest pre-change adapter
reproducer failed on fabricated Square settlement readiness. Existing focused core
project compiled the actual Infrastructure and LegendCommerce owners. Compatible passed
checks were retained while only failed/new or fixture-affected checks were rerun.
Synthetic controller fixtures required valid business owner keys, canonical documents,
GET requests and MVC TempData; their setup failures were not production defects.
The actual shared Payments Razor view compiled separately with zero warnings/errors.
No solution-wide build, application-wide test suite or previously completed migration ran.

Temporary execution receipts: `/private/tmp/wi05b-adapters.log`,
`/private/tmp/wi05b-results/wi05b-core.trx`, `wi05b-targeted.trx`,
`wi05b-navigation-final.trx`, `wi05b-navigation-accepted.trx`, `wi05b-denial-final.trx`, and
`/private/tmp/wi05b-razor.log`. Latest outcomes by exact test name give 104 distinct core
passes, zero remaining failures/skips; repeated attempts are not additional checks.
Committed regression tests and this result record persist with this checkpoint; temporary
log paths are not claimed as durable backups or live provider evidence.

Astra prescribed the bounded design and independently reviewed the source diff, cleanup
and focused evidence; no implementation or tests were delegated to Astra. Exact commit
acceptance and remote readback are attached to the commit in the session handoff; obtain
the checkpoint SHA from Git history and actual remote HEAD rather than a self-reference.
No new gateway, authorization engine, credential writer, payout processor, schema,
migration, configuration, platform billing path or Parfait checkout authority was added.

Remaining external Square proof is unchanged: effective production/sandbox configuration;
authorized per-business seller scopes/location/default connections; real sandbox OAuth,
checkout, signed/replayed webhook, two-tenant isolation, refresh/refund/interruption tests;
deployed provenance, durable key-ring recovery and SQL constraints/concurrency; actual
settlement/payout evidence and separately authorized Parfait migration/cutover acceptance.
These are configuration, provider authorization, sandbox validation and production
verification gates—not completed capabilities or inferred source defects.

## Next assignment (recommendation only; stop here)
Consolidated revenue-baseline release readiness: separate mandatory prepublication safety
gates from controlled postdeployment acceptance tests. Preserve all WI01–WI05B evidence
and open external gates. Do not merge, deploy, charge, migrate or initiate Parfait cutover
without the separately authorized existing release process.

## Preserved WI04C risks and exact boundary (historical)
WI04C changes 17 existing production/contract/UI files and 17 test files; its single implementation commit and this checkpoint preserve earlier work. Nullable reporting values and consolidated receipt ownership require coordinated release validation through the existing release authority; no current deployment is claimed. Stop after this checkpoint and return control to the Founder.
Overall launch readiness PARTIAL, with merchant/cutover acceptance BLOCKED as recorded above. Setup PASS at af5384537623b84055cea245a83b35f5b5bd1f7d. WI01 changed nine affected source/test files; WI02 changed four canonical production files and one focused regression file; WI03 changes only the existing shared editor and its existing regression file. WI04A changes five existing production owners and five existing test files; no schema or event catalog definitions changed. WI04B changes four existing production files and five focused test files; no schema, tracker, provider bridge, public upload engine or new queue. All update this single checkpoint. WI03 makes no backend, merchant onboarding, Parfait, production data, migration, merge, deployment or domain changes. WI04A adds no tracker, writer, queue, provider, migration or live-data operation.
The old local worktree has a missing gitdir; Work Item 01 used a fresh clone of the exact isolated checkpoint and preserved branch ancestry. No repeated repository-wide audit or production workflow. Historical evidence above remains dated and cannot establish current provider configuration.
Evidence links: https://github.com/MYLEGND/masterapp/pull/581 ; https://github.com/MYLEGND/masterapp/pull/624 ; https://github.com/MYLEGND/masterapp/pull/625 ; https://github.com/MYLEGND/masterapp/actions/runs/38124421310 ; https://github.com/MYLEGND/masterapp/actions/runs/38323933521 ; https://github.com/MYLEGND/masterapp/actions/runs/38329876334 .

## Fast-track independence and autonomy gate (2026-10-13; isolated repair)

Verdict: NOT independently production-governed; release remains BLOCKED pending
Founder-approved governance and retirement. Canonical preflight verified clean local
and remote revenue HEAD dad91d6ec7de0dd8462b7a2c291bc05e2bb5f329, native Git
transport and preserved prior ancestry. Observed release HEAD was
dc10c7aabbba6cd5b516071c9e16d32dafeba9a6; obsolete/default branch HEAD was
5fcc93fb4279ec7c875f9c3fb27bc3a31a1cca96. No release branch or production write
was performed. A push dry-run is not proof a later push succeeds.

PROVEN SOURCE: fast-track owns exact-source admission and physical-schema/journal
migration reconciliation before app publication. Shared security validation is
base-aware. Historical approved-lane receipts remain necessary compatible evidence;
they do not grant execution authority. The old-named legend-approved-release mutex
remains necessary collision protection while competing writers coexist.

Confirmed bounded correction: every selected app or routing publication now requires
the existing exact-source admission, closing the UI-only bypass. The existing
publisher repeats that admission before provider access. Only backend scope requires
the existing migration gate. Existing isolated validation covers all fast-track source
trigger paths. Publisher outer timeout is 30 minutes; the canonical reconciliation
budget remains 420 seconds. No new validator, planner, writer or journal was added.

REAL LIVE CONFIGURATION GAPS (authenticated read-only GitHub evidence): fast-track
classic protection returned Branch not protected; the sole active ruleset24319290
protects only legend/approved-changes. No tracked CODEOWNERS establishes the requested
review binding. Production has no protection rules or deployment branch policy.
Old lifecycle362222668 still schedules mutation-capable orchestration; direct release
361187664 remains a runnable competing writer. Queued old run37642351262 requires
intent/provider reconciliation before retirement. Four active legacy registrations
361007272,360915565,380110017,361195095 have no workflow file on either observed
default or fast-track tree: stale registrations, not proven runnable source paths.
Shared security372470990 and historical evidence must remain.

REUSE/RECOVERY: dependency fingerprints and authenticated executed-child observations
can preserve compatible successful child evidence even from failed parents. Schema
readiness separately observes physical state; app success separately requires provider
operation, live SHA and durable receipt. Exact immutable packages prohibit rebuild
after possible upload. Failed-jobs reruns preserve successful siblings; rerun-all
reconciles prior writes. General automatic successful-target pruning across a fresh
parent/new candidate is NOT established; ambiguous predecessors fail closed. No
additional recovery engine or blind replay is authorized. Owned migration denial
codes retain precise reasons and stage/run context; unknown provider failures still
require read-only investigation.

DATED EXECUTION EVIDENCE: run38323933521 retained successful schema reconciliation
with SQL write skipped, successful Client/Protect/Website jobs, and Portal immutable
package restoration without compilation; Portal reconciliation then failed. Its
full multi-attempt span was52m02s; reconciliation step437s includes overhead around
the420s budget. Runs38312609525 and38309478217 show concurrent two-app publication
and totals10m03s/12m18s, but source admission and migration were skipped. Thus the
fully secured normal-path10–15m goal remains UNVERIFIED. Latest successful
run38330781086 took16s with all publication/admission stages skipped: no deploy proof.
Artifact metadata for38323933521 shows retained packages/intents/receipts, not fresh
validation of their payload or runtime state. Historical Portal evidence in
38329876334 remains ambiguous without a provider deployment identity.

CONTINUITY: read-only Azure metadata shows five apps running with HTTPS-only; four
server apps have managed identities. Portal AI key/model, Entra/Graph and Data
Protection binding names are present; Client Entra/Marketing Data Protection names
and enabled Key Vault SQL/Entra secret metadata remain present. Existing Square
presence evidence remains dated. No values were read or changed. Presence does not
prove exact OIDC federation, AI tools/conversation persistence, key decryptability,
live SQL readiness, payments, Cloudflare routing or functional business journeys.
These remain pre/post-publication verification gates; Parfait cutover remains barred.

FOCUSED CODEX VALIDATION:216 unique tests passed:84 fast-track,7 ready/no-SQL and
125 publisher methods. Twenty publisher methods initially hit the existing
hashlib.file_digest dependency on macOS Python3.9; only those failed methods were
rerun successfully on installed Python3.13, retaining other compatible passes.
Five added tests cover UI denial before provider access, admitted UI without invented
SQL gates, backend schema enforcement, workflow bounds/admission, and trigger
coverage. No broad build, provider mutation or real SQL ran. Temporary logs under
/private/tmp are not durable backups; committed tests and this checkpoint preserve
the result record. Required CI is NOT replaced by these tests: candidate run
38518181088 stopped at FAST_TRACK_SOURCE_VALIDATION_PR_UNPROVEN before build/tests.
Astra identifies this as expected fail-closed missing/ambiguous exact fast-track PR
binding, not a proven source defect; rerun alone is not guaranteed to supply binding.

FOUNDER DECISIONS REQUIRED BEFORE ACTIVATION: integrate the accepted bounded patch
through exact-candidate approved-release-security-validation.yml (PR) and
parfait-isolated-validation.yml (push) gates (release-branch push can
automatically publish); establish fast-track protection, applicable valid Code Owner
review and Production reviewers/branch restrictions using approved identities and
actual emitted checks; verify exact OIDC trust; reconcile queued/ambiguous legacy
operations before disabling the two confirmed writers and optionally stale Actions
registrations. Retain shared security, historical artifacts and collision protection
until writers are disabled. Do not transplant obsolete architecture-validation
requirements, delete branches or change the default branch automatically.

Astra independently reviews the five-file isolated diff and focused evidence; exact
acceptance and remote identity belong to the session handoff/Git history, avoiding
a self-referential checkpoint SHA. No merge, deployment, workflow disablement,
credential/configuration change, migration or production-affecting retirement is
part of this isolated repair. Stop for Founder approval before those actions.

## Business client identity and owner account management repair

Founder scope: separate business entity name/email from linked personal owner identity,
current personal email and ownership percentage; enable the full existing managed business
client account from an owner's personal profile. Existing isolation branch continued from
fb139911b43ed0de3c269d67f852f6e11d73e804 after canonical preflight verified clean
local/remote equality and native authenticated transport. No release activation authorized.

Confirmed source defects and canonical replacements:
- CommerceBusinessProvisioningService now accepts only canonical personal Client profiles
  as linked owners, rejecting BusinessClient/Lead without inferring ownership from email.
  Exact profile-ID/email matching, ownership shares, agent scope and membership remain.
- Profile owner editing reads current personal name/email from the persisted linked
  ClientProfile ID; entity email comes separately from its unique business account profile.
  Malformed/unavailable historical links remain unavailable; no automatic reassignment or
  fabricated recovery. Existing membership shares and immutable identities are preserved.
- Existing create model/controller, shared native form contract and browser requiredness
  no longer require business contact first/last names as the entity identity. Entity name,
  business email and linked owners retain their validations. Contact update uses persisted
  classification; personal/lead first-name and length validation remain. The one-line lead
  adapter preserves the shared DTO's existing lead contract. Profile business contact
  names are optional without relaxing personal profile validation.
- BusinessWorkspaceAccess resolves authenticated personal actor -> explicit active owner
  membership -> active business -> unique active account membership -> business profile.
  It checks both existing app and external identity lifecycle records, role/capabilities
  and ambiguity. No email grant, replacement identity, new service or membership writer.
- Existing Support entry and selfClientProfileId selection now support that owner scope.
  EffectiveClientContextService revalidates it each request, hydrates the business subject
  and its existing household/agent metadata, and separately carries the real owner actor.
  Revoked, inactive, missing or ambiguous access fails closed rather than falling back to
  a personal write. Existing agent access rules remain separate.
- Existing business route/handoff/navigation adapters use the owner's capabilities and
  fence writes to the selected business. Business financial state uses only the business's
  existing household scope; it never borrows the personal owner's scope. Explicit entity
  management/return links identify the current account. Existing self-only messaging,
  billing/lifecycle mutations, restricted finance and Journey operations retain their
  managed-view restrictions; owner access does not impersonate an agent or message sender.
  Protection Snapshot reflects that existing read-only restriction in UI and server.

Focused evidence: 57 unique checks passed, including existing personal profile, agent
shared/self-managed access and mobile CRM regressions. Initial three-case reproducer
failed precisely because a BusinessClient could be selected as a personal owner. Added
coverage includes valid switch/exit, actual actor preservation, revocation/inactivity,
capability loss, cross-tenant and same-email denial, missing/duplicate account membership,
app-vs-external identity pauses, selected-business-only finance and owner mutations,
current email projection, malformed historical links, optional business contacts,
creation/native form parity, entitlement without agent bypass and managed snapshot UI.
AgentPortal and ClientApp plus affected shared dependencies/Razor compiled through a
focused temporary test project linking committed tests and existing repository helpers.
Compatible successes were retained; only failed/new/affected cases reran. One malformed
fixture initially tried to mutate an EF key and was corrected to seed malformed historical
state instead. No production data/provider calls were used. JS syntax and diff whitespace
checks passed. Temporary /private/tmp/business-owner-*.log receipts are not durable backup;
committed tests and this checkpoint retain the result record. Normal repository test
entry: AgentPortal.Tests filtered to BusinessOwnerIdentityTests, ClientProfileControllerTests,
ClientAccountManagementTests and MobileCrmManagementTests.

Astra owns diagnosis/design and independently reviews the exact diff and focused evidence;
Codex alone implemented and executed tests. Exact acceptance/commit/readback is recorded
in the session handoff, not as a self-referential commit inside this file. Affected apps:
AgentPortal and ClientApp, with their existing Infrastructure dependency. No schema,
migration, credential, business-data, release-workflow or deployment changes. Existing bad
owner data is not guessed or rewritten; eligible owners must be explicitly selected through
the repaired flow. Required release validation and live create/update/switch journey proof
remain outstanding until an authorized deployment. All prior accepted commits remain.

Release audit addendum: Founder moved active ruleset24319290 to
refs/heads/Legend/fast-track-release; authenticated readback confirmed protection, PRs,
no bypass actors and deletion/non-force safeguards. Its required architecture-validation
check remains the audited obsolete validation dependency, Code Owner binding/approval
count still needs reconciliation, and Production still has no protection rules or branch
policy. No settings were changed by this repair. Legacy retirement/release remains gated.
