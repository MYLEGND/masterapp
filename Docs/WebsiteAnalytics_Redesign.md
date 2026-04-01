# Website Analytics Redesign Plan (Command Center)

## Goals
- Keep existing tracking/ingest intact.
- Transform main page into a concise executive summary with clickable modules.
- Drilldowns in centered modals with filtering/sorting/time controls.
- Add time intelligence (Today, 7d, 30d, Month, Year, Custom).
- Add lightweight live refresh (polling) without heavy load.
- Maintain prod/non-internal filters; clearly label Environment/time windows.

## Scope Summary
- No changes to tracking.js / lead-modal.js / ingest payloads.
- UI/UX overhaul of `FounderAnalytics` view + supporting JS/CSS.
- Add dedicated query layer/services for summary vs drilldowns.
- Add API endpoints for drilldown data and polling (or reuse action methods via AJAX).

## Proposed Architecture
- Controllers:
  - `FounderAnalyticsController` keeps initial page load, adds endpoints:
    - `GetSummary(range)` returns KPI row + quick trends.
    - `GetTraffic(range, groupBy)` for Traffic Overview modal.
    - `GetPagePerf(range)` for Page Performance.
    - `GetCtaPerf(range)` for CTA performance.
    - `GetQuoteFunnel(range)` for quote starts/forms/submits.
    - `GetConversions(range)` for conversion center.
    - `GetLeads(range, search, page)` for Leads Snapshot modal.
- Services:
  - `IAnalyticsQueryService` with methods for each drilldown, accepts date range + env/internal filters.
  - Implementation `AnalyticsQueryService` encapsulates EF queries, grouping (day/week/month/year), pagination for tables.
- ViewModels/DTOs:
  - SummaryKpiDto (pageViews, uniqueVisitors, sessions, leads, conversionRate, topPage, topCta).
  - TrendPointDto (label, value).
  - Table row DTOs for each module.
- Time Ranges:
  - Default: Last 30d.
  - Preset: Today, 7d, 30d, Month (calendar), Year (calendar), Custom (start/end).
  - Grouping: day for <=30d; week for <=90d; month for >90d; year for multi-year.
- Live Refresh:
  - Client polling every 30–60s for summary; if a modal open, refresh that modal only.
  - Debounce/abort concurrent requests.

## UI/UX
- Main page = summary cards:
  - KPIs: Page Views, Unique Visitors, Sessions, Verified Leads, Conversion Rate, Top Page, Top CTA.
  - Each card clickable → modal drilldown.
- Modals:
  - Traffic Overview: line charts (views/sessions/visitors), top pages, entry pages, recent activity feed.
  - Page Performance: table with visits, conversion rate, CTA engagement per page.
  - CTA Performance: table of CTAs with clicks, conv rate, trends.
  - Quote Funnel: starts, form starts, submits; by quote type; drop-off percentages (if inferable).
  - Conversion Center: lead_form_submit_success and successful form_submit; source attribution.
  - Leads Snapshot: searchable/filterable table (name/email/phone/interest/source/timestamp); ready for future export.
- Styling:
  - Premium dark cards, gold accents; modals centered with focus trap; strong hover states.
  - Keep gold headings; body text navy on light sections, light on dark cards.

## Data & Filters
- Environment: keep existing filter (!IsInternal && env in null/prod/production/dev/development); expose optional env toggle only if authorized.
- Date filtering: apply server-side in queries; always label the active window.
- Conversion Rate: leads / sessions (or leads / page_views if sessions=0 fallback).

## Backlog / Future
- Endpoints implemented:
  - `/founder-analytics/summary`
  - `/founder-analytics/traffic`
  - `/founder-analytics/page-performance`
  - `/founder-analytics/cta-performance`
  - `/founder-analytics/quote-funnel`
  - `/founder-analytics/conversions`
  - `/founder-analytics/leads`

- Service layer:
  - `IAnalyticsQueryService` / `AnalyticsQueryService` centralize time-filtered queries, grouping, and filtering (!IsInternal + env in prod/prod/dev/development/null).

- DTOs:
  - SummaryKpiDto, TrendPointDto, KeyCountDto/Key2CountDto
  - TrafficOverviewDto, PagePerformanceDto, CtaPerformanceDto, QuoteFunnelDto, ConversionCenterDto, LeadSnapshotDto, TimeRangeRequest (with presets & grouping)

- Frontend:
  - `wwwroot/js/founder-analytics.js` for time controls, polling (45s configurable), modal fetch, rendering tables.
  - `wwwroot/css/founder-analytics.css` premium dark styling for KPIs/modules/modals.

- View:
  - `Views/FounderAnalytics/Index.cshtml` now summary-first command center with clickable modules and modals.

- Polling:
  - Default 45s interval; refreshes summary; if a modal is open, refreshes that modal only.

- Time behavior:
  - Presets: today, 7d, 30d, month (calendar), year (calendar), custom (date pickers).
  - Grouping: day/ week/ month/ year chosen based on span inside TimeRangeRequest; labels include range.

- Limitations remaining:
  - Trends use lightweight table views; no heavy charting.
  - Quote submit “server-confirmed” still based on form_submit events; no backend confirmation yet.
  - Leads/Conversions CSV export added (lightweight client-side); other modules remain table-only.
- Conversion metrics:
  - Intent Conversion = Verified Leads / best available intent starts (prefers Quote Form Starts, else Quote Starts, else Form Starts); warns on low sample (<20); unavailable if no intent denominator in range.
  - Session Conversion Rate = Verified Leads / Sessions (secondary; warns on low sample <20).
- Server-confirmed quote submits if available later.
- Broader CSV/export for other drilldowns when needed.
- SignalR push if scaling beyond polling.
- Role-based toggles for env/internal visibility.

## Environment separation (prod vs dev)
- New appsetting (AgentPortal): `Analytics__EnvironmentFilter`
  - `prod` → only includes env normalized to prod/production; excludes null/legacy and dev.
  - `dev`  → only includes env normalized to dev/development.
  - If unset → legacy behavior allows prod/dev/null for backward compatibility.
- Normalization: values starting with `prod` → prod family; starting with `dev` → dev family; null/empty = legacy/unclassified.
- Tracking expectation:
  - Live/prod deployments set tracking environment to `prod`.
  - Local/dev deployments set tracking environment to `dev`.
- UI clarity: Founder Analytics header shows environment badge (Production / Development / Mixed/Legacy) so operators know which data they’re viewing.
- Optional cleanup (manual/DBA):
  - Identify non-prod rows in prod DB:
    ```
    SELECT Environment, COUNT(*) AS Rows
    FROM AnalyticsEvents
    GROUP BY Environment;
    ```
  - Remove dev/legacy rows from prod (only if approved):
    ```
    DELETE FROM AnalyticsEvents WHERE Environment IS NULL OR Environment LIKE 'dev%';
    DELETE FROM WebsiteLeads   WHERE Environment IS NULL OR Environment LIKE 'dev%';
    ```
