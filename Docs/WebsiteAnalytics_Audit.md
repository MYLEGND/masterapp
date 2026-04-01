# Website Analytics Audit

## System Overview
Current analytics flow:
Protect‑Website frontend emits events via `/wwwroot/js/tracking.js` and lead modal `/wwwroot/js/lead-modal.js`. Events post to AgentPortal APIs (`/api/analytics/ingest`, `/api/lead/submit`) with `X-Shared-Secret`. AgentPortal persists to SQL Server tables `AnalyticsEvents` and `WebsiteLeads`. Founder analytics page (`/FounderAnalytics`) renders summary counts and tables filtered to non‑internal and environments prod/prod-like.

## File Map
- Protect-Website
  - `Views/Shared/_Layout.cshtml` injects `TRACKING_SECRET`, `TRACKING_API_BASE`, page key.
  - `wwwroot/js/tracking.js` page/CTA/form tracking, VisitorId/SessionId, posts events.
  - `wwwroot/js/lead-modal.js` lead capture modal, posts leads, fires lead events.
- AgentPortal
  - Controllers
    - `Controllers/API/AnalyticsIngestController.cs` ingest events.
    - `Controllers/API/LeadSubmitController.cs` ingest leads + email notify.
    - `Controllers/FounderAnalyticsController.cs` queries dashboard.
  - Models/ViewModels
    - `Domain.Entities.AnalyticsEvent`, `Domain.Entities.WebsiteLead`
    - `Models/FounderAnalyticsViewModel.cs` (summary datasets).
  - Data
    - `Infrastructure/Data/MasterAppDbContext.cs` DbSet<AnalyticsEvent>, DbSet<WebsiteLead>.
    - Migrations `20260327121000_CreateWebsiteLeadManual.cs`, `CreateAnalyticsEventsManual.cs`.
  - Views
    - `Views/FounderAnalytics/Index.cshtml` dashboard UI, tables/cards.
  - Styling/JS: inline CSS in view; no dedicated JS for live refresh.

## Data Flow (text diagram)
Browser (tracking.js / lead-modal.js)
→ POST `/api/analytics/ingest` or `/api/lead/submit` (with X-Shared-Secret, JSON payload)
→ AgentPortal controllers validate secret + allowed event types, dedupe on ClientEventId, save `AnalyticsEvents` or `WebsiteLeads` (Environment inferred from ASPNETCORE_ENVIRONMENT or payload/null; IsInternal set from request or founder)
→ Dashboard queries `_db.AnalyticsEvents` / `_db.WebsiteLeads` with filters (!IsInternal && env in prod/production/dev/development/null)
→ View renders counts/tables.

## Metrics (current logic)
- Page Views: count events with EventType = `page_view`.
- Unique Visitors: distinct VisitorId where VisitorId not null/empty.
- Sessions: distinct SessionId where SessionId not null/empty.
- Verified Leads: count of WebsiteLeads after filters.
- Visits by Page: group page_view by PageKey.
- CTA Clicks: group `cta_click` by PageKey + ElementKey.
- Quote Starts: `quote_click` grouped by ElementKey/QuoteType.
- Quote Form Starts/Submits: `form_start` / `form_submit` where FormKey contains `quote_`.
- Risk Starts/Submits: risk_assessment_click or form_start/form_submit with FormKey `risk_assessment_form`.
- Book Call Clicks: cta_click where ElementKey hero_book_call/footer_book_call.
- Top Conversions: lead_form_submit_success or form_submit with SubmitOutcome null/success.
- Recent Submissions: latest lead_form_submit_success or form_submit.
- Recent Leads: latest WebsiteLeads (default 15).
- LeadsForModal: latest 200 WebsiteLeads.

## Payload / Validation
- AnalyticsIngest: required ClientEventId (Guid), EventType in allowed set; optional PageKey, ElementKey, FormKey, QuoteType, Url, Path, Referrer, SessionId, VisitorId, UTM, Environment, Host, SubmitOutcome, MetadataJson, IsInternal. Dedupe by ClientEventId.
- LeadSubmit: required FirstName, Email, InterestType, TermsAccepted, secret; optional Phone, PreferredContactMethod, Notes, SourcePageKey/CtaKey, UTM, SessionId, VisitorId, Environment, Host; CreatedUtc server-side; Status = New; IsInternal if founder; emails `connect@mylegnd.com; zac.owen@mylegnd.com`.

## Database
- Tables: `AnalyticsEvents`, `WebsiteLeads`.
- Key columns:
  - AnalyticsEvents: Id (bigint identity), EventId (Guid), ClientEventId (Guid?), EventType, PageKey, SectionKey, ElementKey, ButtonLabel, FormKey, QuoteType, Url, Path, Referrer, SessionId, VisitorId, UTM fields, IsInternal (bit), Environment, Host, EventUtc, ReceivedUtc, SubmitOutcome, MetadataJson.
  - WebsiteLeads: Id (bigint identity), LeadId (Guid), First/Last/Email/Phone, PreferredContactMethod, InterestType, Notes, SourcePageKey/CtaKey, UTM fields, SessionId, VisitorId, MarketingEmailConsent, CallTextConsent, TermsAccepted, IsInternal, Environment, Host, CreatedUtc, Status, MetadataJson.
- Indexes:
  - AnalyticsEvents: ElementKey, EventType, FormKey, PageKey, ReceivedUtc, SessionId, VisitorId.
  - WebsiteLeads: CreatedUtc, Email, InterestType, SourceCtaKey, SourcePageKey.
- UTC: EventUtc/ReceivedUtc/CreatedUtc stored server-side in UTC.

## Filters / Environment
- Controllers filter to !IsInternal and Environment in {null, prod, production, development, dev}.
- IsInternal set if request payload says so or current user is founder (ingest).
- Environment set from payload or ASPNETCORE_ENVIRONMENT (default "Production").

## Limitations
- No date-range/time-grouping; all-time aggregates only.
- No live refresh/polling.
- No drilldown modals; full tables on main page.
- No server-confirmed quote submits beyond form_submit events.
- No charting/trends; no top CTA/page summary on KPIs.
- CORS must be configured per environment; sensitive to missing shared secret.
- Environment filter previously hid Development until adjusted.

## Recommended Redesign Path (high level)
- Keep ingestion unchanged; add summary vs detail query services with date range/grouping.
- Add time filter (today/7/30/month/year/custom) applied server-side.
- Add drilldown modals per module with sorting/filtering.
- Add lightweight polling for summary + open modal datasets.
- Add cached/read-optimized queries (e.g., grouping by day/week/month).
- Keep environment/internal filters consistent; allow toggle for env/internal if permitted.

## Risks for Refactor
- Changing filters could inflate/deflate counts; preserve current logic.
- Date grouping without proper UTC handling could misalign; keep UTC and label clearly.
- Client polling might increase load; throttle and cache.
- CORS/secret misconfig between sites could silently drop events; document env keys.

## Safe to Preserve vs Needs Replacement
- Preserve: tracking.js, lead-modal.js payload schema; ingest controllers, dedupe, DB schema/indexes, secret validation.
- Replace/extend: dashboard UI (summary + modals), add time filters, add service layer for analytics queries, add polling, move inline CSS to structured styles.
