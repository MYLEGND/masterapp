# Phase 8 – Growth Toolkit Plan (Draft)

Objective
Turn Website Analytics into an agent growth toolkit with campaign link builder, QR generation, and founder campaign visibility—without breaking attribution, scoping, or current UX.

Scope (Phase 8)
- Agent-side: build/copy/open tracked links (utm_source/utm_medium/utm_campaign) off their personal base URL; generate QR for a chosen link.
- Founder/admin: view campaign/source impact in analytics (global + per-agent), see agent campaign usage at a glance.
- No database schema changes unless lightweight logging is required; prefer ephemeral generation first.

1) Link Builder Design
- Base URL: use existing AgentUrlInfo.PrimaryUrl (founder root, agent /a/{slug}); allow optional alternate slug for founder.
- Supported params now: utm_source (required), utm_medium (optional), utm_campaign (optional). Keep param keys lowercase, avoid adding more until usage is proven.
- Construction rules:
  - Start from primary URL; preserve trailing slash off root.
  - Append query with provided params; do not overwrite existing params unless same key.
  - Validate input: strip whitespace, limit length (e.g., 128 chars), allow alnum + - _ . ; reject spaces.
  - Present final link as copyable and openable; do not auto-shortlink yet.
- Later: support presets/templates, additional params (utm_content/utm_term), and short-link service.

2) QR Code Design
- Generate on demand, client-side, using a small JS QR library (e.g., qrcodejs) to avoid server image generation and storage.
- Scope: render a canvas/svg in the modal with the final built URL; provide “Download PNG” and “Copy link” actions.
- Server-side QR generation not needed now; keeps infra simple. If later required (branding, error correction control), add a server endpoint.

3) UI Design
- Location: new “Growth” panel in Website Analytics header/right rail (agent view). For founder, show both their own link tools plus an agent selector and campaign insights module.
- Agent view contents:
  - Personal base URL (already shown) + actions: Copy, Open.
  - Link builder form: utm_source (required), utm_medium (optional), utm_campaign (optional), build button → shows resulting URL + Copy + Open + “Generate QR”.
  - QR modal: displays QR of the built URL, download button.
- Founder view:
  - Same builder for chosen agent (defaults to founder root or selected agent slug).
  - Additional “Campaign Insights” module: top sources/campaigns by agent (leverages existing top source/campaign metrics).

4) Data / Logging
- Phase 8: no persistence of generated links; treat as ephemeral UI tools.
- Logging: client-side console/log optional; server logging not required until short-link service exists.
- Avoid storing built links server-side to keep scope low and avoid PII concerns.

5) Founder Campaign Analytics (Phase 8)
- Surface existing top source/top campaign metrics in founder UI:
  - Global view: top sources/campaigns across agents.
  - Agent-specific view: reuse scoped traffic summary; add a small “top campaigns” table in founder view.
- No new backend aggregation required beyond current top-source/campaign fields; reuse AnalyticsQueryService outputs.
- Later phases: campaign rollups, per-link performance, attribution confidence, templates.

6) Implementation Order (Phase 8)
1) UI scaffolding:
   - Add Growth panel + modal shells (agent & founder variants).
   - Wire existing personal link display into the panel.
2) Link builder logic (frontend only):
   - Build URL from base + params; validate inputs; show resulting URL and actions.
3) QR code client-side generation:
   - Add lightweight QR library; modal to render/download.
4) Founder campaign insights:
   - Display top sources/campaigns (global/agent scoped) using existing endpoints.
5) Polish & guards:
   - Handle empty params, invalid input messaging, copy/open affordances.

7) Do-Now vs Later
- Do now: client-side builder, QR generation, UI panels, reuse existing analytics top source/campaign data for founder insight.
- Later: short-link service, server-side QR, link templates/presets, campaign persistence/logging, utm_content/utm_term, branded QR styling, export of campaign links.

8) Risks / Tradeoffs
- Client-side only means no audit trail of generated links (acceptable for Phase 8 MVP).
- QR library adds small JS payload; keep to a minimal dependency.
- Input validation must prevent malformed URLs; keep strict pattern to avoid broken attribution.
- Founder insights rely on existing data quality; low-sample data should be labeled (already have low-sample hints in analytics).
