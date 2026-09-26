# Shared workspace backend audit and implementation plan

Date: 2026-09-23. Audit only; no application code or deployment changed during this audit.

Reviewed draft: `862cf842305c66855a19063229408175f98c6a1f` (PR #139). Approved branch: `e72a2b7f60fc8a74e760785cea0fcdbac29c2496` (Release 84 baseline). Branch references were checked during this audit. Earlier live-release receipts established Release 84; this audit did not repeat production browser or Meta delivery verification.

## Decision

Use stable backend workspace, resource, operation, workflow, and event identities across all application surfaces. Display names, translated text, industry labels, and routes are presentation metadata. A frontend label cannot select ownership, grant a capability, or redefine what a backend outcome means.

The draft must be completed through common contracts and scope adapters, rather than accumulating more business-only handlers. Existing Protect and AgentPortal behaviors remain compatibility baselines. Their event catalogs, delivery authority, CRM logic, and stored identifiers should be reused and generalized incrementally.

Dynamic behavior means that authorized configuration supplies labels, available workflows, navigation, and valid bindings. Executable handlers and payload validation remain explicitly registered server code. There must be no arbitrary method dispatch, reflection over browser-provided names, or inference that a label such as “Won” proves a payment.

## Verified findings

| Area | Evidence in current source | Consequence |
| --- | --- | --- |
| Shared CRM views | ClientApp links AgentPortal Clients/Leads Razor views and scripts in `ClientApp/ClientApp.csproj`. | Page source reuse exists, but is insufficient without backend contract coverage. |
| Incomplete route coverage | Static extraction of the two scripts finds 52 distinct literal `crmRoute(...)` destinations; 21 have declared counterparts in the business controller files. | The remaining 31 require implementation, an adapter, or a capability-specific contract. This is a route inventory, not a count of 31 always-visible broken buttons. |
| Auxiliary routes | Scripts still call `/production/*`, `/WorkstationNotes/*`, `/calendar/*`, `/api/finance-state/*`, and `/LeadBridge/Select` outside the CRM route helper. Scheduling also embeds calendar routes. | Fixing the main CRM URL prefix does not cover embedded tools. |
| Draft owner specialization | `Infrastructure/Businesses/ExecutionEngine.cs` and `CommitmentService.cs` accept an optional business ID and branch on business ownership. | They reuse engines but are not yet a general resolved-workspace contract. Replace these branches with owner policy adapters while retaining their isolation tests. |
| Actor and owner are different | `BusinessWorkspaceAccess` resolves membership/assigned-agent access; actions retain a separate `CreatedBy`. Existing agent ownership uses `OwnershipQueries`. | A founder or assigned agent managing a business must act within that business; their actor ID must not become the business owner. |
| CRM terminology is identity | `BusinessWorkspacePreferences.Stages` is a list of display strings. `BusinessWorkspaceCrm` validates and stores those strings as `CrmStage`. | A label rename changes the identifier. Immutable stage IDs are required. |
| Several record identities | Client CRM uses client profile/user IDs; leads and business contacts use workstation lead IDs. Website intake has separate public lead/link IDs. | A typed resource reference and legacy mapping are needed. A business contact must not become a fake portal account to fit an existing controller. |
| Agent-owned persistence | `ProductionRecord` stores `AgentUserId`; `LeadAppointment` stores `OwnerAgentUserId` and provider-specific booking fields. | Generalizing routes alone cannot establish business ownership. Add explicit scope mappings/columns where needed. |
| Notes schema | `WorkstationNotesController` creates/queries `AgentLeadSelfNotes` at request time, keyed by actor, lead, date. | Move this behind a shared repository and governed migrations. Preserve the existing private “note to self” semantics separately from workspace-shared notes. |
| Shared analytics foundation | `ScopeContext`, `AnalyticsScopeQueryExtensions`, `IAnalyticsQueryService`, and the new shared detail projection already support business filtering. | Extend these authorities rather than create parallel reports. Scope resolution is still duplicated in analytics and AI controllers. |
| Marketing owner foundation | `MarketingOwnerScope` provides owner keys; `MarketingConnectionStore` enforces unique owner/provider mappings. | Reuse this store and adapt canonical workspace identity to its existing owners. Do not create another credential store. |
| Meta connection gap | OAuth state in `MetaAdsOAuthService` is keyed by agent tracking profile; business analytics lacks the full connection/campaign/AI surface. | Generalize OAuth ownership and shared handlers while preserving existing callbacks and agent credentials. |
| Published mapping gap | `WebsiteSignalBindingPolicy` validates editor mappings from `MetaSignalEventCatalog`; `legend-public-cms.js` saves mappings, but the reviewed business runtime emits only `/analytics/business-page`. | Saved mappings alone are not runtime dispatch. Published binding execution is still required. |
| Visitor attribution gap | `AnalyticsController.BusinessPage` uses the session ID as both session and visitor ID; its request has event ID, session ID, and path only. | Current business page events cannot independently represent returning visitors and do not carry the original campaign context through this request. Use the existing consent-aware identity/attribution contract. |
| Public host enforcement | `BusinessWebsiteMiddleware` resolves the host and published version before serving pages. Its custom-host API allowlist includes page tracking, content and inquiry APIs; CSP restricts scripts and connections to self. | New event ingestion and any browser-pixel delivery need explicit host routing and narrowly scoped policy support. Adding a listener alone will not produce Meta browser delivery. |
| Existing Meta authority | `MetaSignalSingleTruthPolicy` names `MetaSignalOutcomeDispatcherHostedService > MetaConversionsApiService` as the authoritative server send path; Protect registers the dispatcher. | Retain this single sender and its decision policy. Do not add senders to each frontend application. |
| Sitemap authority | `BusinessWebsiteMiddleware` generates sitemap entries from the verified host’s published `CompiledPagesJson`. | Preserve published-only, host-scoped behavior; use the same page identity/version for editor selection, analytics bindings, canonical URLs, and sitemap projection. |
| Native dependency | `MobileAgentCrmController` calls existing Clients/Leads controller methods for several mutations. | Those routes and payloads are compatibility requirements. Extract shared service commands beneath them before switching native clients. |
| Other application producers | Parfait has its own commerce analytics producer and site/reporting-owner constants; LEGEND public pages have a build-time page catalog. | These must map through the same contracts while preserving commerce and site semantics. They are not interchangeable with an agent CRM owner. |

The findings concern source-level wiring. They do not establish that existing Protect or AgentPortal production behavior is broken. The previous 32 targeted .NET tests and 16 script tests cover the completed draft portions; they do not certify whole-system parity.

## Canonical contract

Use one typed `WorkspaceKey` namespace backed by stable existing owner identifiers and explicit server-maintained mappings. Keep the transport representation opaque to UI code. An agent identity OID, an agent tracking profile ID, a business entity ID, and a website/site owner key are distinct identities; never assume their GUIDs are interchangeable.

Do not introduce a universal replacement contact table or re-key existing records in the first migration. Resolve existing identities through the common ownership registry/adapters. A new persistent workspace registry is warranted only for durable mappings that the existing owner tables cannot represent; it must have unique owner mappings and cannot become a second competing authority.

| Contract field | Meaning and rules |
| --- | --- |
| `workspaceKey` | Stable owner scope. Server resolves it from authenticated context or verified public host and checks authority on every operation. |
| `actorKey` | Actual caller for authorization and audit; derived from authentication, never accepted as authority from the browser. |
| `resourceRef` | Resource kind plus stable existing ID. A shared resolver verifies its workspace before any read/write. |
| `operationKey` | Stable registered behavior, e.g. proposed `crm.contact.update`, `crm.activity.add`, `crm.task.complete`, `website.draft.save`, `analytics.kpi.read`. Examples are proposed, not existing APIs. |
| `stageId` / `outcomeKey` | Immutable configured workflow identity and separately defined outcome semantics. Labels and ordering are editable metadata. |
| `siteKey`, `pageId`, `elementId`, `bindingId`, `publishedVersionId` | Scope and immutable publication provenance for editor, runtime events, reporting, and sitemap routing. Existing IDs get compatibility mappings where necessary. |
| `eventId`, `correlationId`, `causationId` | Distinguish one interaction/outcome, its end-to-end trace, and its producer. Retries retain the event/idempotency identity. |
| `expectedRevision`, `schemaVersion` | Concurrency and contract compatibility. Stale or unsupported requests return a typed error without partial mutation. |

Renaming “Clients” to “Customers,” changing “Estimate” to “Proposal,” translating a button, renaming CAMO, or moving a page URL must not alter the workspace, record, action, workflow stage, or event identity.

`ResolvedWorkspaceContext` should be immutable and constructible through the server resolver only. It contains actor, effective owner, allowed operations, authorized reporting scope, and applicable policy profile. Public website events obtain a narrower context from verified host plus publication; it cannot authorize private CRM operations. Founder global/team reporting is an explicit aggregate capability, never an invalid-scope fallback.

## Central responsibilities

1. **Scope resolver and resource ownership repository:** adapt existing agent assignment, client workspace access, business membership, founder delegation, site/domain bindings, and mobile actor authorities. Unknown, mixed, revoked, or ambiguous scope fails closed. Batch operations validate all referenced resources before mutation.
2. **Operation registry:** bind stable keys to typed payloads, shared handlers, authorization requirements, revision/idempotency rules, and supported owner policy profiles. Keep existing URLs as adapters to these same handlers. Avoid a new generic endpoint that merely reimplements every controller.
3. **Workspace descriptor:** generate authorized navigation, fields, workflow stages, filters, labels, endpoint links, and supported operation keys from server contracts. Global Quick Find, shared Razor pages, JS, and native clients consume these descriptors. It contains no credentials.
4. **Workflow configuration:** store immutable stage IDs with display labels, order, allowed transitions and separately approved semantic outcomes. Move insurance-specific catalogs into a policy profile. General business workflows use the same engine with different configuration. A cosmetic rename cannot trigger an insurance or commerce conversion.
5. **Event authority and projections:** committed domain outcomes feed the existing analytics/Meta authority through a durable transactional handoff. Extend the existing event persistence/dispatcher rather than building a competing delivery queue. Reporting reads the same scoped event lineage that dispatch uses.

All supported UI operations need real handlers. Capability descriptors may express authorization, product applicability, or disconnected-provider states, but cannot be used to conceal unfinished promised features or silently return success/empty data. Insurance-only tools retain their explicit domain semantics; generic commerce revenue is not “PolicyPaid.”

## Implementation sequence and acceptance gates

### 1. Freeze contracts and establish the compatibility baseline

Expand the attached static ledger into a complete control-to-contract matrix: app, screen/control ID, operation key, route/method, payload/result/error schema, owner/resource resolver, persistence handler, side effects, analytics events, capabilities, and tests. Include Razor form actions, dynamically constructed URLs, modals, auxiliary scripts, native APIs, background workers, public inquiry forms and provider callbacks—not just the two main JS files.

Record current agent/Protect results using deterministic fixtures. Capture route shapes, stored identifiers, authorization behavior, key reports, event names, consent rules and deduplication behavior. Maintain a separate manifest of deployed app SHAs and successful release targets. Gate: every advertised control is classified; no unknown dispatch destination remains in the required release scope.

### 2. Establish scope and operation contracts beneath existing endpoints

Introduce shared resolved context, resource references, registered operations, typed results/errors and metadata descriptors. Extract common ownership checks through adapters to existing authorities. Preserve existing endpoint paths and response contracts. Route the current agent endpoints through the extracted service layer first and verify parity before extending another owner profile. Replace the draft’s optional-business-ID service branches with this shared context boundary.

Gate: unauthorized/mixed scopes, removed memberships, assigned-agent removal, invalid resource references, replayed commands and stale revisions are tested. Existing Protect and AgentPortal contract suites remain green. No business identifier is written into an agent identifier field.

### 3. Migrate configuration and ownership additively

Assign immutable IDs to existing configured stage strings, preserving a per-workspace legacy alias map. Keep old readers/writers compatible during rollout. Add owner mappings/columns and indexes only where production, scheduling, notes, billing or access records cannot represent their real owner. Backfill with deterministic, count-reconciled mappings; ambiguous ownership must be reported rather than guessed.

Review draft enum additions (`Business`, `BusinessContact`) against old string-enum readers: mixed-version deployments must be safe before any new values are written. Use governed migrations for note storage. Existing portal-account lifecycle and customer-contact lifecycle stay distinct. Gate: dry-run/backfill evidence, uniqueness and scoped joins, old/new reader compatibility, and rollback behavior without destructive schema rollback.

### 4. Complete each CRM family end to end

| Family | Required complete behavior | Existing authority to extract/reuse |
| --- | --- | --- |
| Contacts | Create, list/search/filter, quick view, edit, convert, import preview/validation/idempotency, export, archive/restore/delete with correct lifecycle | Clients/Leads controllers and existing profile/lead ownership services |
| Workflow | Stage change, reorder, bulk edit, priority, next action, outcome, call counters, My Day and queue filtering | Existing CRM metadata, attempt tracking and queue/outcome logic |
| History and notes | Activities, governed clearing/deletion/audit, personal self-notes versus shared workspace notes | CRM activity metadata and notes repository extracted from WorkstationNotes |
| Tasks and commitments | Create, list, edit, complete, delete, fulfill/break, transactional linked actions, dashboard projection | Shared ExecutionEngine and CommitmentService |
| Revenue/production | Record/update/reset/delete/history/summaries, correct status and amount semantics per policy profile | ProductionService, preserving existing agent totals and attribution |
| Scheduling | Connection state, authorized calendar selection, availability, booking, reschedule/cancel/status, provider callbacks | Existing calendar, booking and appointment services |
| Billing and access | Offers, invitation lifecycle, subscription changes, cancellation, collaborator lookup/grant/revoke with the correct account owner | Existing billing, account lifecycle and access services |
| Workspace customization | Labels, immutable stages, columns, metrics and valid recipient configuration | Shared descriptor/configuration services; same CRM renderer |

Gate for each family: every associated control works through the canonical service for every supported owner policy; scope-denial tests and legacy-agent tests pass. Avoid separate copied business implementations. Complete general CRM behavior before advertising it in the release.

### 5. Complete analytics, ads and published event mapping together

Keep the existing query service and shared KPI/timeline projections. Resolve one reporting scope for summary, charts, modal details, exports, AI snapshots/review/follow-up, campaign metrics and connection controls. Preserve time zone, range, traffic-quality and attribution filters consistently. Reconcile public event ingestion schemas and the existing analytics/Meta aliases.

Generalize OAuth state to a signed, expiring, replay-protected owner binding and recheck actor authority at callback. Preserve legacy callback compatibility. Use MarketingConnectionStore for scoped credentials, status and disconnect. Missing business credentials must never resolve to another owner’s pixel/ad account. Never expose secrets in descriptors, logs or UI.

Publish an immutable mapping manifest from the existing editor catalog: page/element/binding IDs, permitted trigger, approved event key, delivery mode and publication version. Runtime dispatch validates against that manifest and verified host. Draft/preview events cannot become production conversions. Server outcomes (saved lead, confirmed booking, confirmed payment) originate from verified persistence/provider results, not a click label. Preserve the single Protect dispatcher, server-authority precedence, consent, matching-field rules, stable event IDs and deduplication.

Use the shared identity/attribution contract for visitor versus session identity, campaign/click IDs and source lineage. Update custom-domain routing/CSP narrowly for the chosen approved runtime; do not broadly relax host or script restrictions. Preserve the published page catalog for page selection, canonical URLs and sitemap output.

Gate: traced published interaction → scoped intake/contact → analytics row → authorized Meta decision → recorded provider acknowledgement, with replay, tenant separation, consent-denied, preview, missing-credential and provider-failure cases. “Browser invoked,” “server accepted” and “Meta accepted” are different evidence states and must remain distinct in analytics. External Meta acceptance cannot be certified by unit tests or a configured mapping alone.

### 6. Apply the same contracts across applications and release safely

AgentPortal and ClientApp use the same shared pages and operation descriptors. Protect remains the existing public event and server-delivery authority. LEGEND website rendering and Parfait commerce producers bind their real site/commerce owners to the same event contracts. Native iOS/Android retain their current authenticated API routes while backend adapters delegate to the shared handlers; new native UI consumes versioned descriptors/contracts where applicable. Public storefront navigation should not expose private CRM merely because the framework is shared.

Build a contract coverage gate from registered operations and UI declarations, plus interaction tests for dynamic controls. Validate navigation and tenant switching; cache, storage, query keys, drafts and exports must carry workspace identity. Verify dependency injection, linked Razor partials, asset publication, background workers and mixed-version rollout—not compilation alone.

Release only through `legend/approved-changes`, with required checks and additive migrations. Keep PR #139 draft until the matrix is complete. Deploy affected targets using the governed release workflow, verify origin/public commit provenance and smoke-test actual authorized flows. Preserve successful unchanged targets. If a target fails, resume that target/stage from recorded receipts; do not rerun successful targets. This audit does not request or start a release.

## Definition of complete

- Every required frontend representation has a registered backend contract and a verified authorized behavior, including meaningful validation, denial and disconnected-provider states.
- Labels and routes can change without changing record, workflow or outcome identity.
- Cross-tenant and mixed-owner requests cannot read, mutate, count or send events for another owner.
- Existing agent/Protect behavior and native contract compatibility are demonstrated by regression evidence.
- CRM, Website Analytics, ads attribution, editor publication and sitemap use consistent identity and event provenance.
- No orphaned controls, fake-success handlers, hidden unfinished features, duplicate senders, or business-specific copies of the canonical CRM pages remain in the promised release scope.
- Deployment receipts and end-to-end evidence exist for the exact approved commit. Zero regressions is a release objective to verify, not a guarantee that can be made from this source audit.

## Initial route ledger

The table below is an exact-string static scan of literal `crmRoute(...)` calls in `clients-index.js` and `leads-index.js`, compared with declared business controller routes at the reviewed checkpoint. Query strings are removed. It does not establish full runtime coverage, payload parity, authorization or feature visibility. Auxiliary/dynamic/Razor/native paths require the expanded matrix in phase 1.

| Script destination | Script surfaces | Declared business route |
| --- | --- | --- |
| `/Clients` | clients | Unresolved in scoped controller |
| `/Clients/Actions` | clients | GET |
| `/Clients/AddActivity` | clients, leads | POST |
| `/Clients/AdvancedMarketsInputs` | clients | Unresolved in scoped controller |
| `/Clients/ApplyOutcome` | clients | Unresolved in scoped controller |
| `/Clients/BreakCommitment` | clients | POST |
| `/Clients/BulkUpdate` | clients, leads | POST |
| `/Clients/CancelClientSubscriptionAtPeriodEnd` | clients, leads | Unresolved in scoped controller |
| `/Clients/ClearActivities` | clients, leads | Unresolved in scoped controller |
| `/Clients/ClientAccessCollaborators` | clients | Unresolved in scoped controller |
| `/Clients/CollaboratorLookup` | clients | Unresolved in scoped controller |
| `/Clients/Commitments` | clients | GET |
| `/Clients/ConfigureSubscriptionOffer` | clients, leads | Unresolved in scoped controller |
| `/Clients/Create` | clients, leads | Unresolved in scoped controller |
| `/Clients/CreateAction` | clients | POST |
| `/Clients/CreateCommitment` | clients | POST |
| `/Clients/Delete` | clients, leads | Unresolved in scoped controller |
| `/Clients/Edit` | clients | Unresolved in scoped controller |
| `/Clients/FulfillCommitment` | clients | POST |
| `/Clients/GrantClientAccess` | clients | Unresolved in scoped controller |
| `/Clients/ImportLeadsCsv` | clients | Unresolved in scoped controller |
| `/Clients/MyDaySnapshot` | clients | Unresolved in scoped controller |
| `/Clients/Queue` | clients | Unresolved in scoped controller |
| `/Clients/QuickView` | clients | GET |
| `/Clients/Reorder` | clients | POST |
| `/Clients/ResendClientInvite` | clients | Unresolved in scoped controller |
| `/Clients/ResendSubscriptionInvitation` | clients, leads | Unresolved in scoped controller |
| `/Clients/RevokeClientAccess` | clients | Unresolved in scoped controller |
| `/Clients/RevokeSubscriptionInvitation` | clients, leads | Unresolved in scoped controller |
| `/Clients/SaveAdvancedMarketsInputs` | clients | Unresolved in scoped controller |
| `/Clients/SaveQuickView` | clients | POST |
| `/Clients/UpdateClientSubscription` | clients, leads | Unresolved in scoped controller |
| `/Dashboard/CompleteAction` | clients, leads | POST |
| `/Leads/Actions` | leads | GET |
| `/Leads/ApplyOutcome` | leads | Unresolved in scoped controller |
| `/Leads/BreakCommitment` | leads | POST |
| `/Leads/Commitments` | leads | GET |
| `/Leads/CreateAction` | leads | POST |
| `/Leads/CreateCommitment` | leads | POST |
| `/Leads/Delete` | leads | Unresolved in scoped controller |
| `/Leads/DeleteBucket` | leads | Unresolved in scoped controller |
| `/Leads/DeleteBulk` | leads | Unresolved in scoped controller |
| `/Leads/FulfillCommitment` | leads | POST |
| `/Leads/Import` | leads | Unresolved in scoped controller |
| `/Leads/IncrementCall` | leads | Unresolved in scoped controller |
| `/Leads/Lead` | leads | GET |
| `/Leads/Leads` | leads | Unresolved in scoped controller |
| `/Leads/MyDaySnapshot` | leads | Unresolved in scoped controller |
| `/Leads/Queue` | leads | Unresolved in scoped controller |
| `/Leads/Reorder` | leads | POST |
| `/Leads/SaveQuickView` | leads | POST |
| `/Leads/UpdateLeadAppointmentStatus` | leads | Unresolved in scoped controller |


Key inspected sources: `ClientApp/ClientApp.csproj`; `AgentPortal/Controllers/ClientsController.cs`, `LeadsController.cs`, `WorkstationNotesController.cs`, `WebsiteAnalyticsController.cs`, `WebsiteAnalyticsAiController.cs`; `AgentPortal/Mobile/MobileAgentCrmController.cs`; `AgentPortal/Services/ProductionService.cs`; `Infrastructure/Businesses/*`; `SHARED/Crm/BusinessWorkspacePreferences.cs`; `SHARED/Analytics/MarketingOwnerScope.cs`, `ScopeContext.cs`, `MetaSignalEventCatalog.cs`, `MetaSignalSingleTruthPolicy.cs`; `Infrastructure/Analytics/AnalyticsScopeQueryExtensions.cs`, `MarketingConnectionStore.cs`, `MetaAdsService.cs`; `Infrastructure/WebsiteEditing/WebsiteSignalBinding.cs`; `Protect-Website/Controllers/AnalyticsController.cs`, `WebsiteInquiriesController.cs`; `Protect-Website/Services/BusinessWebsiteMiddleware.cs`, `WebsiteEditorPageCatalog.cs`, `MetaSignal/MetaSignalOutcomeDispatcherHostedService.cs`; `SHARED/WebsitePlatform/legend-public-cms.js`; `Legend-Website/scripts/build.mjs`; `ParfaitApp/Services/ParfaitAnalyticsService.cs`.
