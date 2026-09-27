# Website Analytics canonical cleanup — review record

Status: implementation and validation in progress; not a deployment or production-recovery claim. Do not merge while the remaining gates below are open.

Approved base: `df4bae6f9a175ecc04992235cfaf834a0095a7bf`.
Draft PR: https://github.com/MYLEGND/masterapp/pull/249.

## Classification and dispositions

| Implementation | Classification | Disposition and authority |
| --- | --- | --- |
| `Infrastructure/Analytics/WebsiteTrackingProxyAuthority.cs` | CANONICAL | Retained abstract/non-discoverable. Owns the public first-party protocol and delegates mapping/persistence/ownership. Lead forwarding remains a required adapter for the existing lead endpoint. |
| `Protect-Website/Controllers/TrackingProxyController.cs` | ENTRYPOINT | Sole concrete host adapter for `POST /api/tracking/ingest`; also exposes the distinct lead-submission protocol. |
| `AgentPortal/Controllers/API/AnalyticsIngestController.cs` | DEAD / COMPETING | Deleted. Removed the independent mapping, nullable-owner fallback, duplicate ingest routes, and weaker deduplication path. |
| `WebsiteTrackingProxyAuthority` `/api/analytics/ingest` alias | DEAD / COMPETING | Deleted; browser tracker and generated runtime configuration use `/api/tracking/ingest`. |
| `WebsiteAnalyticsIngestAuthority.BusinessPage`, request DTO, middleware exemption | DEAD / COMPETING | Deleted. No current production repository caller; its publication/ownership tests moved to the canonical ingest. Immutable older publications still require the predeployment check below. |
| `Infrastructure/Analytics/WebsiteAnalyticsIngestAuthority.cs` | REQUIRED ADAPTER | Retained static/non-routable. Enriches canonical browser metadata; owns no endpoint, owner resolution, deduplication, or persistence. |
| `Protect-Website/Controllers/AnalyticsController.cs` | DEAD / COMPETING | Deleted with `/analytics/meta-signal`; Meta browser observations now enter the single canonical tracker and ingest endpoint. |
| `Infrastructure/Analytics/UnifiedEventMapper.cs` | CANONICAL | Sole AnalyticsEvent/MetaSignalEvent construction mapper. |
| `Infrastructure/Analytics/UnifiedAnalyticsWriter.cs` | CANONICAL | Sole AnalyticsEvents insertion boundary. All browser protocols share `PersistBrowserEventAsync`, including duplicate/race/owner checks and persisted receipt identity. |
| `Infrastructure/Analytics/UnifiedMetaSignalWriter.cs` | CANONICAL | Sole MetaSignalEvents insertion boundary; only the canonical bridge invokes it. |
| `Infrastructure/Analytics/MetaSignalAnalyticsBridge.cs` | CANONICAL | Sole analytics-to-Meta derivation. Durable source rows are reread before mapping; asynchronous background projection consumes server outcomes without coupling their persistence to Meta. Its source catalog also supplies health reporting. |
| `CommerceSignalService`, production outcomes in `MetaSignalCrmOutcomeService` | REQUIRED ADAPTER | Retained domain producers. Removed direct Meta construction/writes; persist AnalyticsEvent; the independent background bridge projects eligible persisted rows. Stable IDs, dedupe, commerce values, CRM lineage, and dispatch provenance are preserved. |
| `MetaSignalAnalyticsService` duplicate bridge-source catalog | DEAD / COMPETING | Deleted; previously omitted appointment-completed and differed on server-lead eligibility. |
| `SHARED/Analytics/MetaSignalAnalyticsAliasCatalog.cs` | CANONICAL | Sole analytics-name-to-Meta alias catalog. Provider standard-event formatting remains a separate required transport concern. |
| `MetaSignalOutcomeDispatcherHostedService`, `MetaSendAuthority`, `MetaConversionsApiService` | CANONICAL / REQUIRED ADAPTER | Retained dispatcher, delivery authorization/deduplication, and HTTP transport respectively. One dispatch implementation and one hosted registration in Protect. |
| `WebsiteAnalyticsScopeResolver` | CANONICAL | Authenticated dashboard selection; impersonation resolves before team scope, missing scoped profiles fail closed. |
| `AnalyticsQueryService` | CANONICAL | Reporting predicates for AnalyticsEvents, leads, and Meta rows; consumers delegate to these typed predicates. |
| `AnalyticsTrackingProfileScope` | CANONICAL | Sole historical tracking-profile expansion authority. Three copied UPN algorithms removed. |
| `ProtectWebsiteOwnerResolver` | CANONICAL | Public Protect request/lead ownership, independent of credentials; explicit unresolved IDs/slugs cannot become Founder or another agent. |
| `PublicAgentTrackingResolver` | CANONICAL | Profile/alias lookup used by public and portal consumers. |
| `AgentPortal/Services/Tracking/AgentTrackingResolver.cs` | DEAD / COMPETING | Deleted; lead and avatar consumers and DI now reference shared lookup. |
| `PublicWebsiteRuntimeScopeResolver` | CANONICAL | Published LEGEND/business origin, verified domain, and immutable publication ownership. Distinct from authenticated dashboard authorization. |
| `AnalyticsIncidentQueryService.ResolveScopeAsync` | DEAD / COMPETING | Deleted misleading resolver that ignored inputs and always returned Global. Founder-only system monitoring now declares Global explicitly. |
| Portal `MetaAdsConnectionStore` | REQUIRED ADAPTER | Retained owner-checked, one-time legacy-cache import into canonical SQL. No parallel active store. DI replaces the default descriptor instead of adding a competing registration. |
| `MarketingConnectionStore` | CANONICAL | Sole durable marketing connection store. |
| `CanonicalAdvertisingEventProjection` | CANONICAL | Provider-independent persisted source eligibility, stable identities, and exact Founder/Agent/Business owner projection. Historical Meta queue rows use a thin identity adapter. |
| `MarketingBrowserConfigurationService` | CANONICAL | Tokenless public configuration for both providers from one already resolved owner. Provider failures are isolated; unresolved scope invokes neither provider. |
| Protect `TrackingViewDataFilter` | REQUIRED ADAPTER | Uses public owner authority then the shared browser configuration service. Removed OpenAI owner selection from Meta results and the unresolved-owner Founder default. |
| `CommerceStoreContextService`, `ParfaitBusinessScopeService` | CANONICAL | Moved into Infrastructure; storefront and analytics ingress consume the same store scope resolver. The move deletes the old host files without duplicating implementations. |
| `StoreCartController` | ENTRYPOINT | Canonical validated cart command; records accepted quantity and stable receipt identity through CommerceSignalService. This is domain mutation, not a second browser telemetry endpoint. |
| `ParfaitAnalyticsController`, `ParfaitAnalyticsService` | DEAD / COMPETING | Removed two browser telemetry routes, alternate normalization/mapping/persistence, and two stale registrations. |
| `MarketingMetaAdsOAuthService` | CANONICAL | Shared OAuth protocol and protected owner state. |
| Portal `MetaAdsOAuthService` | REQUIRED ADAPTER | Duplicate OAuth implementation removed; portal adapter delegates directly to the shared protocol. Callback reauthorizes owner before exchange/persistence. Previously issued legacy state expires rather than being interpreted through a competing implementation. |
| Portal redundant `IMetaAdsService` registration | DEAD / COMPETING | Deleted; shared registration owns it. |
| `SHARED/WebsitePlatform/tracking.js` | CANONICAL | Sole tracker source, linked/copied into hosts. Readiness commits only after setup; partial setup is rolled back for retry. |
| `SHARED/WebsitePlatform/legend-public-cms.js` | REQUIRED ADAPTER | Sole dynamic runtime loader for published CMS sites; waits for successful tracker execution before optional providers. |
| Protect `_Layout.cshtml` | ENTRYPOINT | Static script inclusion. First-party tracker starts before Meta/OpenAI; optional startup requires tracker readiness. |
| `AgentPortal/wwwroot/js/website-analytics.js` | CANONICAL | One execution scope and one implementation each of device, marketing-performance, and growth loaders. One include in the dashboard view. |
| `website-analytics-kpi-modal.js`, `website-analytics-ai.js`, `website-analytics-incidents.js` | REQUIRED ADAPTER | Active, separate dashboard presentation modules; no duplicate main loader implementations. |
| Parfait analytics controller/UI/service | REQUIRED ADAPTER | Read-only commerce reporting remains active. The competing browser controller/service are deleted; shared browser ingest owns telemetry and the server cart command owns accepted cart mutations. |

## Deleted files

- `Protect-Website/Controllers/AnalyticsController.cs`
- `ParfaitApp/Controllers/ParfaitAnalyticsController.cs`
- `ParfaitApp/Services/ParfaitAnalyticsService.cs` — dead browser/purchase persistence implementation and both DI registrations removed.
- `ParfaitApp/Models/ParfaitAnalyticsModels.cs` — retired telemetry DTO; cart command uses its own minimal request.
- `ParfaitApp/Services/CommerceStoreContextService.cs`, `ParfaitApp/Services/ParfaitBusinessScopeService.cs` — moved into Infrastructure/Commerce; preserved canonical implementation.
- `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
- `AgentPortal/Services/Tracking/AgentTrackingResolver.cs`
- `AgentPortal.Tests/AnalyticsIngestControllerTests.cs` — assertions migrated to `WebsiteTrackingIngestTests.cs`, not discarded.

## Runtime route inventory

| Host | Verb/path | Concrete owner | Purpose |
| --- | --- | --- | --- |
| Protect | POST `/api/tracking/ingest` | TrackingProxyController | Only general first-party browser analytics ingest |
| Protect | POST `/api/lead/submit` | TrackingProxyController | Required lead-forwarding adapter; not another analytics writer |
| AgentPortal | POST `/api/lead/submit` | LeadSubmitController | Canonical lead capture/notification endpoint; uses canonical shared event mapping/writing where applicable |

The shared Infrastructure controller bases are abstract, not runtime endpoints. Both old portal ingest routes, the Protect compatibility alias, and business-page route are removed.

## Regression coverage

- Actual MVC controller/action discovery across Infrastructure, Protect, and AgentPortal: abstract authorities excluded; exactly one general ingest route; retired route/action absent.
- Repository and compiled-IL checks: only canonical mapper constructs event entities; typed EF event insertion only in canonical writers; Meta writer called only by bridge.
- Source guards for copied profile expansion, retired files, duplicate DI registrations, and duplicate dashboard loader implementations/includes.
- Behavioral scope tests: Founder/Agent aliases, missing identities, malformed/mixed owners, explicit unknown paths/IDs, business/agent isolation, and no credential fallback.
- Browser persistence: cross-owner/session/binding replay rejection; duplicate responses retain persisted event identity.
- Bridge: persisted source required; caller mutation cannot upgrade stored browser evidence; stable commerce outcome IDs/values; distinct orders within one session; CRM lineage and eligible dispatch metadata.
- JavaScript: actual dashboard execution; successful tracker startup, rollback/retry, no duplicate listeners/events, optional integrations cannot precede first-party readiness.

## Evidence and remaining gates

Prior repair revision full suite: 3,720 passed, 46 failed, 5 skipped. Exact approved base: 3,716 passed, the **same 46 failures**, 5 skipped. Set comparison: zero introduced failures. These results predate the expanded cleanup and do not validate its final head.

Expanded shared website suite: **145 passed**. Build passed with zero warnings/errors. Focused .NET cleanup run: 128/129 passed; remaining fixture corrected to use HTTPS consistently with its referrer, rerun pending. Expanded final-head full-suite validation is pending.

Before deployment, inspect immutable published `CompiledPagesJson` and external integrations for retired routes. Current repository references cannot prove absence in persisted publications. Historical documents mentioning retired names remain audit history, not compiled/runtime implementations.

Production after-repair receipts, database reconciliation, Meta acceptance, scoped dashboard totals, and release provenance are not yet established. Automatic approval review previously rejected switching from the authorized Founder Personal dashboard to a separate named reporting scope; this rejection was not bypassed. No merge or deployment is represented as complete.

## Expanded source-lineage and provider audit checkpoint

New OpenAI conversions read AnalyticsEvents directly through CanonicalAdvertisingEventProjection, sharing the owner authority with Meta dispatch. MarketingDestinationDelivery stores AnalyticsEventId plus advertiser/data-source identity. A historical receipt adapter preserves old provider IDs; unknown historical destination pins block dispatch. Provider failures do not control first-party persistence or the other provider. This implementation still requires validation on the next frozen revision.

Commerce and CRM produce first-party events without synchronously calling the Meta bridge. CanonicalLeadEventIdentity retains existing issued lead IDs and supplies deterministic IDs for new leads. Browser persistence retains ClientEventId as EventId. New source identities bypass the old minute-window bridge heuristic; that heuristic remains only for historical independently generated records without canonical action identity.

CommerceStoreContextService and ParfaitBusinessScopeService are moved to Infrastructure/Commerce rather than copied; storefront rendering and canonical browser ingress consume the same authority. StoreCartController is a domain command, not an analytics ingest adapter: validated accepted cart state emits one canonical commerce outcome. Parfait migration validation is pending.

The latest stable .NET build compiled affected applications, but test compilation found two incorrect fixture/property references. Those references were corrected; this is not a passing final-head build. See status snapshot and runtime publication migration gate for current evidence boundaries. User's latest instruction explicitly prohibits merge, deployment, approval and ready transition.

Coordinated closure build passed with zero warnings/errors. Affected .NET regression run: 931 passed, 12 failed, zero skipped (943 total). Passing new coverage includes OpenAI projection18/18, browser configuration3/3, outcome bridge17/17, scope isolation12/12, Meta dispatcher6/6, and server cart3/3. Source-contract and test-host corrections are pending selective verification; no passing CI workflow was restarted.

## Selective verification result

All 11 corrected source-contract/test-host cases subsequently passed: 10 in `canonical-contract-recheck.trx`, then the final tracker contract in `canonical-final-contract.trx`. No production source changed during these selective corrections. Aggregate affected coverage: 942 passing identities/executions of 943, with the unchanged baseline messaging-translation case remaining failed. The 931 initially passing cases and successful a09b40e CI runs were not restarted. Final committed-revision full-suite/baseline comparison and CI remain a separate gate.

Repository evidence is in `20260927-repository-proof.md`; per-event parity is in `20260927-event-parity.md`; runtime asset/publication checks are in `20260927-runtime-migration-gate.md`. The server cart currently uses the existing ASP.NET session store and instance-local serialization; this is not a claim of distributed cart exactly-once state. Canonical event insertion has stable command identity and database deduplication.
