# Multi-Agent Analytics Scale-Hardening Plan

Audience: engineering team preparing for 50+ agents and richer attribution without rewrites.

## What is already scale-ready
- **Attribution keys**: `AgentTrackingProfileId` is the primary key on events/leads; slug is snapshot only. Environment filtering already normalized to prod/dev with strict filter option.
- **Routing & resolution**: `/` → founder; `/a/{slug}` → canonical profile; aliases 301 to canonical; unknown slugs 404 (no silent fallback). Server injects resolved profile to frontend and ingest re-resolves server-side.
- **Ingress validation**: tracking.js / lead-modal.js send profile/slug; controllers trust server resolution first, log unknowns, do not auto-founder.
- **Scoped reads**: `ScopeContext` drives all analytics queries; standard agent scope enforced in controller; founder can request global or agent scope; unattributed rows only appear in global.
- **DTO separation**: summary, traffic, funnel, conversions, leads, agent performance are discrete DTOs, easing new projections.
- **Slug service**: AgentTrackingService handles slug uniqueness, aliases, founder root vs slug URL, personal URL generation, backfill, and first-login/provisioning idempotency.

## Gaps / not yet scale-ready
- **Indexing**: Events/Leads lack explicit indexes on `AgentTrackingProfileId`, `Environment`, `EventType`, `CreatedUtc/EventUtc`, `UtmSource/UtmCampaign`, `PageKey`, `ElementKey`. At 50+ agents this will bite queries and exports.
- **Attribution dimensions**: `UtmMedium`, `UtmTerm`, `UtmContent` not exposed in DTOs/queries; `QuoteType` only in funnel; `SourceCta` is overused as both CTA and source key. No normalized dimension tables; all strings.
- **Query structure**: AnalyticsQueryService pulls large in-memory lists then groups. No pagination/top-N parameters; agent performance is full table scan. No async streaming for exports.
- **Founder comparisons**: Agent performance lacks sorting options, paging, time grouping, or environment filter override; low-sample flag is coarse (<20) and not exposed in UI hints beyond a note.
- **Exports/reporting**: CSV exports run client-side from cached rows (100 conversions/200 leads). No server-side export endpoints with paging or filters; DTO contracts could change and break exports.
- **UI state**: founder-analytics.js uses a flat state map; adding more filters (source, campaign, device, env) will require ad-hoc params and duplicated wiring. No central scope object; polling list is hard-coded. Modules are manual; adding new founder-only modules needs map edits.
- **Data validation**: Unknown/invalid agent IDs become null, but no audit/event log table to track attribution failures at scale.

## Harden now (low effort, high leverage)
1) **Add DB indexes** (SQL + migrations):
   - AnalyticsEvents: `(AgentTrackingProfileId, EventUtc)`, `(Environment, EventUtc)`, `(EventType, EventUtc)`, `(UtmSource)`, `(UtmCampaign)`, `(PageKey)`, `(ElementKey)`, `(SessionId)`.
   - WebsiteLeads: `(AgentTrackingProfileId, CreatedUtc)`, `(Environment, CreatedUtc)`, `(SourcePageKey)`, `(SourceCtaKey)`, `(UtmSource)`, `(UtmCampaign)`.
   - AgentTrackingAliases: `(Slug) UNIQUE WHERE IsCanonical=1` (if not present) plus `(AgentTrackingProfileId)`.

2) **Query-layer parameters**:
   - Introduce a small `AnalyticsQueryOptions` carrying pagination (skip/take), sorting, and optional filters (source, campaign, pageKey, quoteType, env). Keep `ScopeContext` unchanged; have controllers compose options.
   - Refactor heavy queries to use server-side grouping (IQueryable) with top-N default (e.g., TOP 10 sources/campaigns, pages) to avoid full materialization.

3) **Agent performance API**:
   - Add sorting (by leads default, optional conversions/session rate) and paging to avoid scanning/returning all agents.
   - Keep founder-only guard.

4) **DTO/contract stability**:
   - Add optional fields now for future reporting: `UtmMedium`, `UtmContent`, `UtmTerm`, `Device`, `GeoCountry` (placeholders). Leave null until data collected.
   - Normalize CTA vs Source: add explicit `SourceCtaKey` vs `PageCtaKey` fields in events/leads to avoid overloading `ElementKey`.

5) **UI state hardening**:
   - Centralize scope into a single `state.scope` object (range, agentProfileId, env, optional filters) and drive all requests from it. Keep polling list derived from registered modals/modules instead of a switch statement.
   - Add lightweight module registry so adding founder-only modules (e.g., campaign view) is declarative.

6) **Logging for failed attribution**:
   - Add a lightweight table or structured log entry for unknown/invalid agent IDs/slugs with counts and last-seen timestamps. Helps detect typos/attacks at scale.

## Can safely wait
- Server-side export endpoints and scheduled reports (once indexes and query options exist).
- True dimension tables for UTM and pages/CTAs (can be added when traffic volume or analytics needs rise).
- UI polish for low-sample labeling and confidence intervals (after core metrics stabilize).
- Short-link/QR infrastructure (keep URL generation abstracted; see below).

## URL / link architecture readiness
- Current: AgentTrackingService produces primary (root for founder, /a/{slug} for agents) and optional founder slug URL; aliases exist.
- Harden: wrap URL generation behind `AgentUrlInfo` with future-friendly fields: `PrimaryUrl`, `AlternateSlugUrl`, `CampaignBaseUrl`, `ShortUrl` (null for now). Keep base URL configurable per environment. Reserve path segment for campaigns (e.g., `/c/{campaign}/{slug}`) to avoid breaking `/a/{slug}`.

## Founder comparison model readiness
- Add query options for top-N and paging; default to top 20 agents by leads, include total count for pagination. Add optional `orderBy` (leads, conversions, sessionConv, intentConv). Keep low-sample flag; add `sampleSize` fields so UI can show counts.

## Export/reporting readiness
- Stabilize DTOs by versioning (e.g., `SummaryKpiDtoV1`) or add semantic version field to responses used by exports. Keep CSV generation server-side once pagination and filters exist.

## UI expansion readiness
- Adopt a small config object for modules `{id, modalId, founderOnly, loader}` and derive polling/handlers from it. This prevents repeated switch/case edits when adding modules like campaigns or device breakdowns.
- Ensure agent selector drives a shared scope object; future filters (source, campaign) can reuse the same pattern.

## Do now vs later
- **Do now**: add DB indexes; add query option object & top-N limits; add sorting/paging to agent performance endpoint; centralize UI scope state; add attribution-failure logging; extend URL service interface to include placeholders for campaign/short links.
- **Later**: dimension tables for UTM/CTA, server-side exports, confidence labels, campaign/QR link generation, scheduled rollups.

## Architectural debt risks at 50+ agents
- Slow dashboards/exports due to missing indexes and in-memory LINQ grouping.
- Agent performance comparisons becoming unusable without paging/sorting.
- Ad-hoc UI state making future filters brittle and error-prone.
- Overloaded `ElementKey`/`SourceCtaKey` leading to ambiguous reporting once campaigns/CTAs grow.
- Lack of attribution-failure telemetry hiding slug/URL issues across many agents.

