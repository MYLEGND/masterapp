# Shared website studio implementation map

Status: active shared-editor implementation, not a release or production completion certificate.
Current baseline: `legend/approved-changes` at `3c24960ee32af2fb9bcd7f4ec04d60558659f879`.
Current isolated branch: `feature/shared-website-studio-fluid-editing-20260924`.
Prior 2026-09-23 implementation and validation history remains documented below.

## Implemented in this branch

The existing `Legend-Design/legend-public-cms.js` remains the only editor/runtime used by LEGEND, Protect, and business websites. This change replaces its navigation and control styling in place. It adds persistent tool navigation, searchable page layers with hidden-content recovery, independent block duplication, accessible save status, keyboard save/undo/redo, page title/description controls, and constrained design choices that match the server sanitizer. No duplicate editor, stylesheet, delivery pipeline, database, or application controller is introduced.

Draft page keys now use route paths accepted by `WebsiteContentSanitizer`. Legacy template keys migrate into the same document; canonical values win conflicts and unrelated elements are retained. New sections have a nonempty parent marker even on an empty page. Deleting an added block removes it from the draft rather than persisting an unsupported `hidden` field. Existing section deletion remains reversible through Layers.

This is an editor foundation, not the requested completed website-building platform. Arbitrary page creation/navigation, responsive breakpoint overrides, reusable section patterns, a media library browser, visual event bindings, marketing destination generalization, and business CRM/analytics are not implemented here.

## 2026-09-24 direct-canvas and public-form invariants

These are shared platform rules, not site-specific patches:

- **One editor/runtime:** `Legend-Design/legend-public-cms.js` remains the editor for Founder/LEGEND, Protect/agent, and business scopes. Scope-specific pages may supply owned content and authorization context, but they must not fork editor behavior.
- **Text edits on the canvas:** editable text, headings, and link/button labels are edited directly on the rendered page with the same canonical element/extra override. The side panel is not a second text source.
- **Direct geometry in the canonical style contract:** width, optional height, horizontal offset, and vertical offset live in `WebsiteStyleOverride` and are sanitized by `WebsiteContentSanitizer`. The canvas exposes move/resize handles, a 12-column horizontal placement grid, a 24px vertical rhythm grid, and center snap guides. Do not add page-specific drag CSS or a second placement store.
- **Existing placement compatibility:** legacy `WebsitePlacement` values remain readable so published documents are not broken, but the active studio interaction uses direct canvas movement/resizing rather than the former destination/column/span/drop controls.
- **Code/embed blocks use the existing Extras model:** `WebsiteExtraComponent.Type == "code"` stores source in the existing `Text` field and geometry in the same `Style` object. Preview/public rendering uses an opaque `data:` document inside a sandboxed iframe. Browser scripts/forms are allowed inside the sandbox, but `allow-same-origin` is intentionally not granted. Business custom-domain CSP permits only `data:` frames for this purpose; the parent site's `script-src` is not relaxed with `unsafe-inline` or `unsafe-eval`. There is no server-side code execution path and no parallel database/schema for code blocks.
- **Business contact identity matches Protect:** public business forms use exact field names `FirstName`, `LastName`, `Phone`, and `Email`, plus `Message`. They persist into the existing `WebsiteLead` fields. CRM capture, analytics, and the existing Meta server dispatcher consume that same lead; no business-only Meta sender or duplicate lead record is introduced.
- **Published business form activation stays canonical:** `Legend-Website/scripts/render-business.mjs` removes the preview marker, re-enables the form, removes preview-only notice text, and loads the existing `business-inquiry.js`. Preview/editor sessions do not submit production inquiries.
- **Shared mobile navigation:** the canonical public stylesheet opens mobile navigation as a compact grid, with four columns in the broader mobile range and three columns at narrow mobile widths. Do not reintroduce scope-specific vertical-menu overrides.
- **CSS rule:** replace authoritative shared rules in place. Do not append emergency overrides, stacked selectors, or page-only fixes to reproduce these behaviors.

## Verified source map

| Capability | Existing owner to extend | Integration requirement |
|---|---|---|
| Agent Protect entry | `AgentPortal/Controllers/AccountController.cs`: `EditProtectWebsite`, `WebsiteSession`; `AgentPortal/Views/Account/ManageProfile.cshtml` | Preserve signed agent scope and profile-owned settings. |
| Founder LEGEND entry | Same controller: `EditLegendWebsite`, `WebsiteSession` | Preserve Founder authorization and `__legend_global__` ownership. |
| Business entry | `ClientApp/Controllers/ProfileController.cs`: `BusinessWebsiteSession`, `AuthorizedBusinessAsync`; profile view | Resolve `CommerceBusinessId` from membership/authorized shared access. Never substitute an agent ID. |
| Shared management | `Legend-Design/legend-website-management.js` and `.css` | Keep versions, readiness, domains, inquiries, scheduling in this one component. |
| Shared editor/runtime | `Legend-Design/legend-public-cms.js` | One editor, renderer, selected-element inspector, undo history and document model. |
| Document/publish | `Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs`, `WebsiteContentSanitizer.cs`; `Protect-Website/Controllers/WebsiteContentController.cs` | Extend versioned documents; retain revision checks and immutable published snapshots. |
| Business authorization | `WebsiteBusinessAccess.cs`, `WebsiteTicketAuthorization.cs`, `CommerceBusinessMember` | Integrate AFTER the active ownership repair. Recheck access on every read/write/export/test/publish. |
| Business rendering/routes | `Legend-Website/scripts/render-business.mjs`; `Protect-Website/Services/BusinessWebsiteMiddleware.cs` | Compiled published pages already determine custom-domain routes and sitemap. |
| LEGEND routes/sitemap | `Legend-Website/src/content.mjs`; `Legend-Website/scripts/build.mjs` | Current build emits its sitemap from page definitions. |
| Protect sitemap | Active repair adds `SHARED/Analytics/ProtectRouteCatalog.cs` and `Protect-Website/Controllers/SitemapController.cs` | Consume that repair; do not recreate its route inventory. These files are not in this branch's baseline. |
| Public card, Pixel/CAPI, booking | `Domain/Entities/AgentProfile.cs`; account profile controller/view | Bio, NPN, Pixel, protected CAPI credential, booking enabled/embed/fallback/mailbox/calendar currently live here. NPN remains insurance-specific, not required for unrelated businesses. |
| Meta connection | `SHARED/Analytics/MetaAdsConnectionDtos.cs`, `MetaAdsScopeKey.cs`; `Infrastructure/Analytics/IMetaAdsConnectionStore.cs`; `AgentPortal/Services/Analytics/MetaAdsConnectionStore.cs`, `MetaAdsOAuthService.cs` | Existing connection DTO/store uses an agent-named GUID; site keys also exist. Generalize this authority explicitly. `BusinessId` in the Meta DTO is a Meta Business Manager ID, NOT `CommerceBusinessId`. |
| Destination resolution | `Protect-Website/Services/Meta/MetaPixelResolutionService.cs` | Currently agency/agent oriented. Business resolution must not fall back to an unrelated agent or agency destination. |
| Signal definitions | `SHARED/Analytics/MetaSignalEventCatalog.cs` | Serve editor options from this catalog; do not copy a frontend event list. Respect existing browser/server eligibility. |
| Delivery | `MetaSendAuthority.cs`, `MetaConversionsApiService.cs`, `MetaSignalAnalyticsBridge.cs`, `MetaSignalOutcomeDispatcherHostedService.cs` | Preserve the other model's pending dedupe/retry/acceptance fixes. No second dispatcher. |
| Analytics | `SHARED/Analytics/ScopeContext.cs`; `Infrastructure/Analytics/AnalyticsQueryService.cs`, `MetaSignalAnalyticsService.cs`, `AnalyticsScopeQueryExtensions.cs` | Scope enum currently has Global/Agent only. Business requires an explicit business scope throughout every query, export, cache, detail, and AI summary. JSON site labels are not an authorization boundary. |
| Agent clients/leads | `AgentPortal/Controllers/ClientsController.cs`, `LeadsController.cs`; `ClientProfile`, `WorkstationLeadProfile`, `WebsiteLeadIntakeLink` | Extract reusable authorized application services/projections and shared view components. Do not copy these controllers into ClientApp. |
| Business inquiries | `Protect-Website/Controllers/WebsiteInquiriesController.cs`; `CommerceWebsiteInquiry` | Existing inquiry intake/status is scoped by permanent business ID. Connect it transactionally to the shared CRM model; do not create a second leads inbox as the CRM. |

## Target user workflow

Profile → Website workspace → Studio → select element → Analytics & Meta → choose delivery/triggers/event/approved fields → private test → publish version → monitor in Website Analytics → manage resulting leads/clients in business CRM.

Studio canvas stays left; inspector stays right. On a narrow screen each pane scrolls independently. Persistent tools should ultimately include Pages, Add, Layers, Content, Design, Position, Actions, Signals, Site Theme and publication history. Use real supported controls, not inert mock features. Add reusable section patterns through the existing document and renderer, not stored HTML or appended CSS.

Profile is the connection authority. Analytics displays its destination health and links back to the profile. The editor edits per-element bindings, not credentials. CRM operates on the same lead/client records as ingestion and verified outcomes.

## Exact next integration sequence

1. Rebase onto the completed repair batch. Inspect the overlapping ownership, analytics, dispatch, inquiry/CRM and profile changes. Do not overwrite or cherry-pick another model's unfinished working tree.
2. Add a typed owner scope to the existing destination and query authorities: Founder/site, AgentTrackingProfileId, CommerceBusinessId. Migrate existing settings to this authority with a reviewed migration; preserve the legacy account connection mapping during migration, then remove superseded read/write paths. Never establish a long-lived dual write or competing credential store.
3. Extend the profile service and shared profile controls for business public contact/bio and booking, with optional industry-specific fields. Keep secrets write-only and protected by the existing credential protection authority. Validate scheduler URLs and permitted origins. Reuse the existing booking resolver/confirmation owner.
4. Extract the shared CRM application service and components from existing AgentPortal behavior. Introduce explicit business ownership where the canonical records lack it. ClientApp receives thin authorized controllers and business-scoped navigation. Preserve agent views and all existing subscription/payment flows.
5. Extend the shared analytics scope type and persistence/query contracts for business ownership. Authorization must precede filtering. Scope all session/event/lead drilldowns, downloads, Meta connection operations, background outcomes, caches, and AI reviews. An unknown or unauthorized business returns denial, never global results.
6. Add versioned signal bindings to `WebsiteContentDocument` and its sanitizer. Bind immutable element IDs and supported component triggers to catalog events. Serve catalog and component capabilities from the authenticated management authority. Prevent a click from claiming a confirmed booking, lead, purchase, policy, or payment.
7. Add the real Analytics & Meta inspector: Off / Analytics only / Analytics + Meta; trigger; catalog event; approved matching-field selectors; verified destination label; consent status; frequency/value controls only where supported. All metadata must be typed and allowlisted.
8. Extend existing ingestion to resolve the published website and bindings from verified domain/permanent ownership. Client context is untrusted. A signature identifies a version; it does not make a client-reported conversion true. Persist verified outcomes through the existing transactional handoff/outbox mechanism.
9. Use existing normalization, hashing, consent, `MetaSendAuthority`, dedupe and dispatch paths. Never send unconsented/sensitive field values, include PII in URLs/logs, or trust a submitted destination. Business disconnects fail closed. Give browser/server copies the same event ID only when they represent the same authorized event.
10. Publish one route manifest per immutable website version and make rendering, sitemap, navigation, editor page selection and analytics consume it. Each website still has its own owned page data. Exclude drafts, preview tokens, private routes and unpublished/deleted pages. Retain custom-domain canonical origin enforcement.
11. Extract the analytics interface into shared components for AgentPortal and ClientApp. Add Event Map, destination health, delivery diagnostics/history, and a private live-test view. Distinguish browser invocation, ingestion acceptance, queued work, Meta response and retry status.
12. Test mode must be explicit at ingestion, persistence and dispatch; do not let editor clicks submit production leads or contaminate production counts. Verify consent and sensitive-field suppression in both browser and server paths. Validate current Meta test-event requirements before implementation.
13. Complete tenant tests using two businesses under one agent, two agents, a Founder, a shared manager, revoked membership, disconnected credentials, a wrong hostname, stale versions, repeated submission and retries. Assert absence of cross-tenant reads AND writes, not merely successful own-tenant access.
14. Bring the tested changes into `legend/approved-changes`, reconcile the latest head, and use the existing affected-app direct release. Do not restart another release. Verify deployed revision, migrations, both profiles, all editor scopes, saved/published content, business CRM, analytics and actual Meta acknowledgement before claiming end-to-end completion.

## Integration boundaries

The other repair checkout had uncommitted changes in business ownership/membership, profile views/controllers, shared analytics queries and dashboards, CRM handoff, Meta authority/dispatcher, sitemap and migrations. This branch changes none of those implementation files. It cannot be honestly described as the complete requested platform or as deployed.

The baseline build uses `/tmp/masterapp` by default for all checkouts. For independent validation pass `-p:MasterAppArtifactsRoot=<unique-path>` and disable shared compiler/node reuse as needed. The initial test attempt encountered shared generated Razor artifacts; subsequent validation uses this workspace's own artifact root.

## Validation for the current 2026-09-24 branch

- The current branch is isolated from `legend/approved-changes`; no deployment or merge has been performed.
- Shared editor JavaScript parses successfully in source-level validation.
- Business inquiry browser adapter parses successfully.
- LEGEND build/test modules parse successfully after normalizing module-only `import.meta` tokens for the parser check.
- Source assertions confirm the side-panel text textarea, destination/column/place controls, and legacy drag-enable button are absent from the active editor; direct inline editing, grid handles, sandboxed code blocks, and the four canonical contact field names are present.
- Modified C# source files have balanced structural delimiters in source-level validation; focused tests were updated for first/last/phone/email persistence and idempotency.
- The published-business renderer is explicitly regression-covered for removing `data-preview`, re-enabling the form, removing the preview notice, and loading the one existing inquiry runtime.
- A full Node/.NET execution is still required before integration because this connector session cannot dispatch the repository's candidate-validation workflow and the local execution environment cannot reach GitHub to materialize the repository.
- No live deployment, production mutation, migration, or Meta delivery test has been performed by this branch.

## Historical validation for the 2026-09-23 editor branch

- Shared website JS suite: 47/47 passed, including all three editor scopes, route-key persistence, hidden-section restoration, independent duplication, deletion/reload, metadata/theme without selection, numeric style contracts, credential URL rejection, and existing profile management behavior.
- Focused .NET `WebsiteContentEditorRoundTripTests`: 11/11 passed with the isolated artifact root, including route-keyed page content/metadata saved and reloaded for Founder, agent, and business.
- LEGEND static build succeeded; business publication renderer suite: 3/3 passed.
- Browser visual QA is not completed: no local Chromium executable was installed, and the browser download failed. DOM tests do not certify visual polish or real mobile interaction.
- No live deployment, production mutation, migration, or Meta delivery test was performed by this branch.
