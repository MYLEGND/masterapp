# Multi‑Agent Protect Website & Attribution Architecture (Plan Only)

**Status:** Design / not yet implemented  
**Scope:** Protect Website (public), AgentPortal analytics/ingest/UI, tracking/lead JS  
**Non‑negotiables:** Preserve founder (zac.owen@mylegnd.com) flows and current tracking/ingest/schema. No schema or ingest changes are applied yet—this document maps them.

---

## 1) Executive Summary
We will extend the existing Protect Website + AgentPortal analytics so every agent automatically gets a unique public link (preferred: `/a/{agent-slug}`), and all traffic/leads from that link are attributed to that agent. Agents will see only their own Website Analytics page; founder/admins retain global visibility. Attribution is enforced end‑to‑end: routing → tracking/lead payload → ingest → storage → query → UI. Provisioning creates/updates an agent tracking profile and slug automatically.

---

## 2) Recommended Architecture
- **Public URL pattern:** `/a/{agent-slug}` served by Protect Website. Slug resolves to an agent tracking profile.
- **Tracking context:** On first page load, server injects `AGENT_TRACK_ID` + `TRACKING_ENV` into layout; tracking.js/lead-modal.js attach `AgentId` (or profile ID/slug) to every analytics/lead payload.
- **Ingest:** AnalyticsIngestController & LeadSubmitController accept new optional `AgentTrackingId` and `AgentSlug` fields; enforce shared secret as today. Environment filtering remains.
- **Storage:** New table `AgentTrackingProfile` (or extend AgentProfile) storing `AgentUserId`, slug, tracking id, status, created/updated. Add columns `AgentTrackingProfileId` (GUID, nullable) and `AgentSlug` (string, indexed) to AnalyticsEvents and WebsiteLeads. Backward compatibility: null = founder/legacy.
- **Query layer:** AnalyticsQueryService scoping adds agent filter: if caller is agent, restrict to matching tracking id/slug; founder/admin can view global or per‑agent. Environment filter remains.
- **UI (AgentPortal/FounderAnalytics page):** When an agent visits, auto‑scope to their tracking profile and show their personal link with copy/open actions. Founder view adds agent selector plus global rollup.
- **Provisioning:** On agent creation or first login, ensure a tracking profile and slug exist; store in DB and expose to layouts. Backfill existing agents with scheduled job/command.
- **Protect Website routing:** Middleware or route constraint resolves `/a/{slug}` → sets body data-page-key, injects tracking config, serves shared content. If slug missing/unknown, fallback to founder/default path to preserve current behavior.

---

## 3) Why This Architecture
- Path-based slugs (`/a/{slug}`) are ad- and SEO-friendly, survive sharing, and avoid querystring stripping.  
- Server-side resolution avoids trusting client-side spoofing; we still log slug for debugging.  
- AgentTrackingProfile separates identity (OID/UPN) from presentation (slug) and allows rotation without losing history.  
- Adding nullable columns preserves legacy rows; environment filter already in place.  
- Minimal JS change: add agent identifiers to existing payloads; ingest already accepts extra fields.  
- Founder remains intact: default route without slug continues to map to founder tracking profile or legacy null.

---

## 4) File / System Areas Impacted
- **AgentPortal**  
  - Controllers: FounderAnalyticsController (agent scoping + agent link display), AnalyticsIngestController, LeadSubmitController.  
  - Services: AnalyticsQueryService, IAnalyticsQueryService.  
  - Models/DTOs: Summary/Traffic/etc. to carry AgentLink/AgentScope info.  
  - Views: Views/FounderAnalytics/Index.cshtml (agent link, selector), layout injection for tracking config.  
  - JS/CSS: wwwroot/js/founder-analytics.js, wwwroot/css/founder-analytics.css (display link, scope).  
- **Domain / Infrastructure**  
  - Entities: new AgentTrackingProfile; new columns on AnalyticsEvent, WebsiteLead.  
  - DbContext & Migrations: add table + columns + indexes.  
- **Protect-Website**  
  - Routing: map `/a/{slug}` to existing pages with agent context.  
  - Layout: inject agent tracking config (slug/id, env, secret/base URLs).  
  - JS: tracking.js, lead-modal.js add agent fields to payload.  
- **Provisioning / Identity**  
  - Agent creation/first-login hook; optional scheduled backfill.  
  - Ensure AgentUserId/OID mapping to tracking profile.

---

## 5) Proposed Entities / Columns
**New table: AgentTrackingProfile**  
- `Id` (GUID, PK)  
- `AgentUserId` (string, required) – OID / identity key  
- `AgentUpn` (string, required) – email for self-heal  
- `Slug` (string, unique, indexed, lowercase)  
- `DisplayName` (string)  
- `Status` (enum/string: active, disabled)  
- `CreatedUtc`, `UpdatedUtc`  
- Optional: `PreferredEnvironment` (defaults to prod) to future-proof

**Add to AnalyticsEvent**  
- `AgentTrackingProfileId` (GUID?, indexed) — primary attribution key  
- `AgentSlug` (string?, indexed) — optional snapshot/debug only

**Add to WebsiteLead**  
- `AgentTrackingProfileId` (GUID?, indexed) — primary attribution key  
- `AgentSlug` (string?, indexed) — optional snapshot/debug only

Backward compatibility: null values = legacy/founder. Founder’s profile stored in AgentTrackingProfile and used for default route; legacy rows can be read under founder/global views.

**Alias support (recommended): AgentTrackingAlias table**  
- `Id` (GUID, PK)  
- `AgentTrackingProfileId` (GUID, FK)  
- `Slug` (unique, lowercase)  
- `IsCanonical` (bool)  
- `CreatedUtc`  
Use to 301 redirect old slugs and preserve attribution history.

---

## 6) Public URL Design
- Pattern: `/a/{agent-slug}` where `agent-slug` is lowercase kebab (e.g., `zac-owen`, `jane-smith`).  
- Collision handling: enforce uniqueness at creation; on rename, keep old slug as alias (optional table `AgentTrackingAlias`) or 301 redirect with canonical slug param.  
- Fallback: `/` (root) continues to route to founder/default; no slug = founder scope.  
- Querystrings (utm, campaign) remain supported and passed through.

**Root-path behavior:** `protect.mylegnd.com/` remains founder/default (canonical founder URL). Founder attribution uses the root path by design. An internal founder tracking profile and slug may exist for parity/testing; an optional founder slug route (`/a/{founder-slug}`) can be served, but root remains canonical for founder.

---

## 7) Attribution Flow (text diagram)
```
Visitor hits https://protect.mylegnd.com/a/{slug}
  → Routing middleware resolves {slug} → AgentTrackingProfile(Id, Slug)
  → Layout injects TRACKING_SECRET, TRACKING_API_BASE, TRACKING_ENV, AGENT_TRACK_ID, AGENT_SLUG
  → tracking.js boots:
       - captures SessionId/VisitorId
       - includes AgentTrackingId + AgentSlug + Environment on every event
  → lead-modal.js submits lead with same agent fields
  → AnalyticsIngestController / LeadSubmitController store AgentTrackingId/Slug into AnalyticsEvents / WebsiteLeads
  → AnalyticsQueryService filters by AgentTrackingId (or slug) based on caller identity
  → AgentPortal FounderAnalytics page for an agent user fetches scoped data and shows personal URL; founder can select agent/all
  → Polling continues using scoped endpoints
```
Visitor hits https://protect.mylegnd.com/ (root, canonical founder)
  → Treat as founder/default attribution (no slug required)

Visitor hits https://protect.mylegnd.com/a/{founder-slug} (optional)
  → Resolve to founder profile; attribution still founder. Root remains canonical.
```

---

## 8) Security Model
- Slug is **public and not a security boundary**; it only selects attribution.  
- Access control remains in AgentPortal:  
  - Agent users: auto-scope queries to their AgentTrackingProfileId; cannot pass another id.  
  - Founder/Admin: can view global rollup and any per-agent scope (server-enforced).  
  - Standard agents have no cross-agent visibility.  
- Ingest still protected by shared secret; attribution fields are accepted but validated server-side (unknown slug → optional “unknown/legacy” bucket, no privilege escalation).  
- Environment filtering (prod/dev) remains; prevents cross-env leakage.  
- Agent link display comes from server-resolved profile, not client input.

---

## 9) Provisioning Model
- On agent creation (or first login): create or update AgentTrackingProfile with generated slug and tracking id.  
- Slug generation: slugify name, ensure uniqueness; if taken, append short suffix (`-2`, `-abc`).  
- Backfill job: iterate existing agents, create missing profiles, assign founder profile for zac.owen@mylegnd.com.  
- Store mapping in DB; expose to layouts via a small service (e.g., `IAgentTrackingResolver`).

Provisioning hierarchy  
- **Primary:** create profile during agent provisioning/creation pipeline.  
- **Fallback:** create on first login if missing.  
- **Backfill:** scheduled/one-time job to cover existing agents and ensure founder profile exists.

---

## 10) UI / UX Plan (AgentPortal)
- Agent view (Website Analytics page):  
  - Show personal link card: `https://protect.mylegnd.com/a/{slug}` with “Copy” and “Open” actions; optionally QR later.  
  - All metrics scoped to agent.  
- Founder/Admin view:  
  - Add agent selector (all, per agent) + environment badge; retain summary cards/modals.  
  - Optionally add rollup view later.  
- No change to public site UX initially; content shared for all agents.

---

## 11) Analytics System Impact
- **Ingest:** Accept/store AgentTrackingProfileId (primary) and optional AgentSlug snapshot on both event and lead requests; keep shared secret, dedupe.  
- Server-side validation: resolve AgentTrackingProfileId from slug when provided; if client supplies both and they disagree, prefer server resolution. Unknown/invalid slug/id → store as null (unattributed), log for review; do NOT auto-assign to founder. Founder attribution only comes from explicit founder/default path.
- **Query:** Add agent filters; default to caller’s profile unless founder/admin. Preserve deltas/trends/polling.  
- **UI:** Pass scope in AJAX calls; display agent link; founder selector.  
- **Exports:** Include agent columns for admin exports.  
- **Environment:** Keep `Analytics__EnvironmentFilter` in effect per deployment.

---

## 12) Protect Website Impact
- Routing: add `/a/{slug}/{*path?}` that maps to existing pages; resolve slug → tracking profile; set view data/body attributes (`data-agent-slug`, etc.).  
- Layout injection: set `window.AGENT_TRACK_ID`, `window.AGENT_SLUG`, `window.TRACKING_ENV`, reuse existing secret/base URL.  
- JS: tracking.js and lead-modal.js include agent fields in payload. No behavior change for non-slug (founder) path.  
- Content remains shared; later personalization possible using resolved agent profile.

**Attribution persistence rules**  
- Store AgentTrackingProfileId client-side alongside SessionId/VisitorId.  
- Duration: session + configurable TTL for return visits (recommend 30 days).  
- Refresh/direct return without slug: if TTL valid, reuse stored AgentTrackingProfileId; otherwise fall back to founder/default (or require slug).  
- Landing later on a different agent slug: override stored attribution with new profile and start a new session context to avoid cross-agent mixing.

---

## 13) Rollout Phases
1. **Schema prep (backward compatible):** Add table + nullable columns + indexes; deploy with code still ignoring new fields.  
2. **Provisioning/backfill:** Create profiles and slugs; assign founder profile.  
3. **Tracking injection:** Inject agent identifiers on public site; update JS to send them; ingest stores them.  
4. **Query/UI gating:** Scope analytics by agent; show personal link; founder selector.  
5. **Verification:** Compare founder totals pre/post; ensure agent isolation; monitor CORS/ingest.  
6. **Cleanup/redirects:** Handle legacy rows (agent null) as founder/global; optional alias support.  
7. **Enhancements:** QR/export by agent, personalized content.

---

## 14) Risks & Mitigations
- **Slug collisions / renames:** Enforce unique slug; add optional alias table; 301 redirect old → new.  
- **Agent deactivation:** Mark profile disabled; keep historical data; block link issuance.  
- **Spoofed agent params:** Server resolves slug → tracking id; ignore conflicting client values.  
- **Missing attribution:** Fallback to founder profile when slug unresolved; log metric.  
- **Old sessions:** Visitors with cached slug cookies continue; acceptable; expiry by session timeout.  
- **Performance:** Add indexes on AgentTrackingId/Slug + Environment; keep queries scoped.  
- **Cross-env leakage:** Env filter already enforced; keep prod/dev separation in config.  
- **Founder defaults:** Root/no-slug continues to map to founder; ensures existing marketing not broken.

---

## 15) Founder / Global Visibility Recommendation
- Founder/admin users get:  
  - All-agents rollup (default)  
  - Per-agent filter  
  - Access to personal link card for each agent (copy/open)  
- Agents see only their own scoped data and personal link.

---

## 16) Final Implementation Order (high level)
1) DB migrations: AgentTrackingProfile table; add agent columns to AnalyticsEvents & WebsiteLeads; indexes.  
2) Provisioning/backfill service: create profiles, generate slugs, mark founder profile.  
3) Layout/Config injection (Protect Website + AgentPortal): expose agent tracking id/slug/env.  
4) JS updates: tracking.js & lead-modal.js include agent fields.  
5) Ingest controllers: accept & store agent fields (with server validation).  
6) Query layer: add agent scoping; update DTOs to include agent link.  
7) UI: AgentPortal analytics page shows personal link; agent-scope; founder selector.  
8) E2E verification (local dev + prod with strict env filters).  
9) Optional: alias/redirect support, QR/export per agent.

---

## 17) Critical decisions to approve before build
- **Root-path ownership:** keep `protect.mylegnd.com/` as founder/default (phase 1) or reassign later?  
- **Attribution duration:** TTL for stored agent attribution (proposal: 30 days; new slug overrides and resets session).  
- **Alias/redirect support:** implement `AgentTrackingAlias` + 301 redirects for slug changes?  
- **Founder/global rollup UX:** founder default view = all-agents rollup vs per-agent first?
