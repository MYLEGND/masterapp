# Multi‑Agent Protect Website Build Plan
*Aligned to approved architecture and locked decisions.*

## Phase 1 — Schema
- **Migrations (Infrastructure)**
  - Add table `AgentTrackingProfile` (Id, AgentUserId, AgentUpn, Slug, DisplayName, Status, CreatedUtc, UpdatedUtc, PreferredEnvironment?).
  - Add table `AgentTrackingAlias` (Id, AgentTrackingProfileId FK, Slug unique, IsCanonical, CreatedUtc).
  - Add columns to `AnalyticsEvents`: `AgentTrackingProfileId` (GUID?, indexed), `AgentSlug` (string?, indexed).
  - Add columns to `WebsiteLeads`: `AgentTrackingProfileId` (GUID?, indexed), `AgentSlug` (string?, indexed).
  - Indexes: (`AgentTrackingProfileId`, `Environment`), (`AgentSlug`), (`AgentTrackingProfileId`, `EventUtc` / `CreatedUtc`).
- **Validation**
  - Apply migrations locally; verify zero data loss.
  - Ensure prod migration plan reviewed before apply.

## Phase 2 — Provisioning / Backfill
- **Services**
  - Create `IAgentTrackingService` (create/find by AgentUserId/UPN, generate slug, manage aliases).
  - Hook into agent provisioning pipeline (where AgentProfile is created) to upsert tracking profile (Primary).
  - Fallback on first login: ensure profile exists.
  - Backfill job/command: iterate existing agents, create profiles, set founder profile for zac.owen@mylegnd.com (root canonical; slug optional), generate unique slugs, create canonical alias. Founder slug is secondary; root stays canonical.
- **Validation**
  - Idempotent reruns; report collisions; ensure founder profile exists.

## Phase 3 — Protect Website Routing/Context
- **Routing**
  - Add `/a/{slug}/{*path?}` route mapping to existing pages; resolve slug → AgentTrackingProfileId via service.
  - Root `/` remains founder/default (primary/canonical); founder slug route may exist for parity/testing, but root stays canonical.
  - Implement 301 redirects via `AgentTrackingAlias` when slug not canonical.
- **Context Injection**
  - In Protect-Website layout: inject `AGENT_TRACK_ID`, `AGENT_SLUG`, `TRACKING_ENV`, `TRACKING_SECRET`, `TRACKING_API_BASE`.
  - Body/data attributes for debug as needed.
- **Validation**
  - Slug resolves to profile; alias redirects; unknown slug logs and is treated as unattributed (not founder); root path remains founder/default.

## Phase 4 — Tracking / Lead Payload
- **JS updates (Protect-Website)**
  - `tracking.js`: include `AgentTrackingProfileId` (primary) and `AgentSlug` (snapshot) on every event; set environment from injected config; apply 30‑day attribution TTL; new slug overrides + resets session.
  - `lead-modal.js`: include same agent fields on submit; reuse TTL rules.
- **Validation**
  - Ensure payload schema unchanged otherwise; shared secret still required; events/leads captured with agent id populated.

## Phase 5 — Ingest / Storage
- **Controllers (AgentPortal/API)**
  - `AnalyticsIngestController`: accept agent fields; resolve slug → profile server-side; prefer server value; unknown/invalid => store as null (unattributed) and log; do NOT auto-assign to founder. Store profile id + slug snapshot when valid.
  - `LeadSubmitController`: same resolution logic; unknown/invalid => null attribution + log.
- **Validation**
  - Dedupe still works; environment filter intact; unit/integration tests for mixed/unknown slugs.

## Phase 6 — Query Scoping
- **Services**
  - `AnalyticsQueryService`: add agent scoping parameter; default to caller’s AgentTrackingProfileId (when agent); founder/admin can request rollup or specific agent; keep env filter.
  - DTOs: include AgentLink for UI.
- **Validation**
  - Consistency between summary and drilldowns; env + agent filters applied identically.

## Phase 7 — Agent Portal UI
- **Views/JS**
  - FounderAnalytics page: show personal link card for agent users; for founder/admin add agent selector (all / specific).
  - AJAX calls include agent scope (implicit for agent user; selected for founder).
  - Copy/open link actions; maintain polling.
- **Validation**
  - Agent sees only own data; founder sees rollup and per-agent; no cross-agent leakage.

## Phase 8 — Founder / Global Rollup
- **Behavior**
  - Founder default view = all-agent rollup; selector to filter to an agent.
  - Ensure exports respect scope.
- **Validation**
  - Rollup totals = sum of per-agent + legacy founder rows; check against legacy baselines.

## Phase 9 — Rollout & Validation Checklist
- Deploy schema first (safe, nullable).  
- Run backfill; verify profiles/slugs; founder profile present.  
- Backfill execution path: POST founder-only `/admin/tracking/backfill?dryRun=true|false` (AgentPortal); logs created/skipped.  
- Deploy routing + layout + JS + ingest + query + UI in coordinated release.  
- Post-deploy checks:
  - Ingest writes AgentTrackingProfileId for slug hits; founder path null/legacy as expected.
  - Agent UI isolation confirmed (login as agent).  
  - Founder rollup and per-agent filters correct.  
  - Alias redirect works.  
  - Attribution TTL honored; new slug overrides session.  
  - Env filter still separates prod/dev.  
  - Performance: index usage verified.  
- Monitoring: logs for unknown slugs, attribution drops, ingest errors.
