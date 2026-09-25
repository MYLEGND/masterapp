# Founder launch audit repair batch — 2026-09-23

This is a partial, evidence-based repair record. Source review, deterministic code tests, deployment success, and live workflow proof are separate gates. No customer activation, payment, received-email, or Meta delivery test is authorized by the current code-tests-only selection. No such test is represented as passed.

## Release scope

Direct release through `legend/approved-changes`: AgentPortal, ClientApp, ProtectWebsite, ParfaitApp. Shared domain/EF and business authorization changes affect all four .NET applications. Static LEGEND Website and native applications are not changed by this release request. Preserve the branch's existing history; do not reset to an older production revision.

## Defect ledger

| Area | Root cause / change owner | Change and verification boundary |
|---|---|---|
| Client workspace | Previously released routing correction | Existing routing tests remain in release gate. Original production exception correlation and every impersonation failure path remain live-audit items. |
| Billing notices | Existing queue had competing-worker send race | `ClientBillingNotificationDeliveryService` now durably claims a due notice before sending, records each attempt/outcome, and retains retry scheduling. SQLite competing-worker regression. Provider acceptance and inbox receipt remain distinct. |
| Business identity | Entity identity and multi-owner shares were absent from client creation/projections | Extend existing `CommerceBusiness`/`CommerceBusinessMember` authority, with exact 100% total, linked personal accounts, same-agent authorization, active-owner management, revocation, ownership history, and optimistic concurrency. Update creation, CRM/subscriber labels, ClientApp ownership management and website permissions. No parallel owner store. |
| Lead persistence | Lead could commit before CRM handoff and canonical events; duplicate retries could skip a failed stage | `WebsiteLeadSubmission` scopes submission identity by token, agent, route and email; unique persisted LeadId; transaction encloses lead, canonical CRM intake link and required events. Quote controllers reuse the existing capture service. Risk Assessment now joins the same pipeline. SQLite rollback/retry regression and Risk Assessment controller regression. |
| Notification replay | Risk/central lead failure had no recoverable path; duplicates could return misleading success | Persist notification attempt/acceptance timestamps on the existing lead; claim lease prevents simultaneous replay; retry uses saved submission; distinguish saved, notification unconfirmed/failed, and accepted. This is explicit same-token retry, not an automatic background email outbox. Provider acceptance still cannot prove inbox delivery. |
| Analytics ingress | Earlier audit reproduced `401 invalid_secret` between Protect and Portal | Shared configuration resolver and release-time secret parity verification; no secret values in release output. Before/after live ingest reconciliation is still required after deployment. Historical missing events are not fabricated. |
| Analytics scope | Timeline, concentration and Meta-quality joins did not consistently use the same scope/quality authority | Canonical attributed-event and scoped-Meta queries; session-first matching; selected range/timezone in timeline; distinct aggregate counts. Scope isolation regressions included. |
| Meta transport/health | Server-created bridge rows could appear as failed browser sends; transient CAPI failures lacked retry; success criteria too weak | Browser provenance states, stable IDs, existing dispatcher retries/backoff, fail-closed authority and accepted-response checks. Shared marketing health projection and AI display surface Meta failures. No claim of live pixel/CAPI acknowledgement. |
| Form milestones | Duplicate submit/abandon transitions and incomplete attribution binding | Existing tracker corrected; nine deterministic JavaScript cases cover starts, retries, exit, BFCache and attribution. AJAX replay retains stable conversion ID and booking context. |
| Routes/sitemap | Duplicate route inventories and empty-string merge discarded discovered keys | Shared `ProtectRouteCatalog`, sitemap controller and landing discovery correction. Paid-route registry regression. Live HTTP/XML/canonical parity remains a deployment verification item. |
| Subscriber lists | Expired invitations depended on stale stored status; mixed records had ambiguous labels | Effective expiry and record-kind labels; entity names projected from canonical business membership. No assertion that active-count/MRR definitions or billing-provider renewal reconciliation passed. |
| UX/content | Risk review lacked summary; placeholder partners and empty training lacked truthful presentation | Editable Risk summary/consent validation, placeholder cleanup, explicit training empty state, spelling correction; no replacement external destinations invented. Partner destination verification remains partial. |
| Legend Connect | Repeated equivalent capacity issues inflated warning presentation | Group open issues by category/code/language/pair, retain history/count/first seen, actionable reservation-vs-monthly-limit guidance. Capacity limits and remediation authority are unchanged. Live provider policy diagnosis remains unverified. |
| Exports/accessibility | CSV escaping/formula risk, empty-export suppression, closed AI drawer exposure | Central CSV serializer, header-only empty exports, formula protection, inert/focus behavior; three CSV tests. |

## Schema and backfill

`20260923050000_LaunchAuditIntegrity` adds nullable ownership percentage/history and lead notification timestamps, plus a unique `WebsiteLeads.LeadId` index. Existing ownership percentages are not guessed or backfilled. Duplicate legacy lead IDs stop the migration for review; no automatic deletion/merging. Existing business membership remains the permission authority. There is no historical analytics/page-view reconstruction. Apply only candidate migrations before application restart, with the existing direct-release unexpected-pending-migration guard.

## Coverage and remaining evidence

The earlier browser pass loaded the listed Founder top-level routes and Protect Home/RiskAssessment/Quote/Life. Initial render is not workflow completion. The current pass performs code tests only. Nested Founder controls, complete create/invite/activate/login/plan-change lifecycle, controlled payments, actual email receipt, every coverage-family browser journey, mobile consent variants, Meta tooling acknowledgements, cross-scope live reconciliation, and billing-provider comparisons remain unverified. Business-context messaging presentation also needs a separate live review; personal identity is not renamed to an entity.

A passing release means the selected code gate and deployment checks passed, not that the entire launch audit is certified. Check the workflow run for exact test totals, migration results, selected app deployment outcomes and live source identities.

## Local validation result

Full affected-app test-project build: passed. Direct-release filter: 765 passed, 0 failed, 0 skipped. JavaScript: nine form-tracking cases and three CSV cases passed. `scripts/db.sh validate-artifacts`: DATABASE MODEL CLEAN; migration artifact set valid. Deployment and live provenance are still separate gates recorded by the release workflow.
