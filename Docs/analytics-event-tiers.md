# Analytics Event Tiers

## Purpose

This document governs how analytics events are classified, used, and protected from semantic drift.

The goal is simple:

- Tier 1 events drive official business KPIs.
- Tier 2 events support UX and conversion optimization.
- Tier 3 events support diagnostics and engineering investigations.

Not every tracked event deserves dashboard authority.

---

# Tier 1 — KPI Events

## Definition

Tier 1 events are sacred business metrics.

They may power:

- KPI cards
- funnel conversion rates
- lead attribution
- confirmed lead reporting
- executive summaries
- AI business review summaries

Tier 1 events must be stable, intentional, and semantically clear.

## Tier 1 Events

### Traffic / Landing

- `page_view`
- `quote_landing_view`
- `thank_you_view`

### Intent / Funnel Entry

- `quote_click`
- `quote_entry_engaged`
- `form_start`
- `lead_form_start`
- `funnel_started`

### Funnel Progression

- `contact_step_view`
- `quote_contact_step_view`
- `form_submit_attempt`
- `lead_form_submit_attempt`
- `form_abandon`

### Confirmed Lead / Business Outcome

- `lead_form_submit_success`
- `website_lead_submitted`
- `lead_persisted`

### Appointment Outcome

- `appointment_slot_selected`
- `appointment_booked`
- `appointment_completed`
- `appointment_no_show`

### Internal Pipeline

- `workstation_capture_attempt`
- `workstation_capture_success`
- `workstation_capture_failure`

## Tier 1 Rules

- Confirmed leads must come from `lead_form_submit_success`, `website_lead_submitted`, `lead_persisted`, or actual lead records.
- Submit attempts must never count as confirmed leads.
- Tier 1 events require review before changing.
- Tier 1 events should not be passive browser telemetry.
- New Tier 1 events must be added to this document before implementation.

---

# Tier 2 — UX Telemetry Events

## Definition

Tier 2 events help improve user experience, conversion flow, and funnel clarity.

They may support:

- friction analysis
- UX diagnostics
- step drop-off analysis
- page optimization
- offer testing
- form optimization

They do not directly define business success.

## Tier 2 Events

### Engagement

- `page_engaged_5s`
- `page_engaged_10s`
- `page_engaged_15s`
- `page_engaged_30s`
- `page_engaged_60s`
- `primary_cta_seen`
- `carrier_trust_strip_view`
- `section_view`
- `file_download`
- `outbound_click`

### Scroll

- `scroll_depth_25`
- `scroll_depth_50`
- `scroll_depth_75`
- `scroll_depth_90`
- `scroll_depth_100`

### Form Interaction

- `form_field_focus`
- `form_field_complete`
- `contact_field_focus`
- `contact_field_complete`
- `contact_progress_snapshot`
- `form_field_error`

### Quote Journey

- `quote_step_complete`
- `quote_step_view`
- `first_question_view`
- `first_question_answered`
- `goal_completed`
- `protecting_who_completed`
- `age_completed`
- `tobacco_completed`
- `tobaccouse_completed`
- `recommendation_viewed`
- `recommendation_generated`
- `estimate_results_viewed`
- `estimate_inline_contact_view`
- `estimate_contact_continue`
- `estimate_results_error`

### Life Funnel Experience

- `life_general_form_start`
- `life_term_form_start`
- `life_whole_form_start`
- `life_finalexpense_form_start`
- `life_mp_form_start`
- `life_iul_form_start`
- `life_general_submit`
- `life_term_submit`
- `life_whole_submit`
- `life_finalexpense_submit`
- `life_mp_submit`
- `life_iul_submit`
- `life_step1_intro_view`
- `life_step1_goal_view`
- `life_step1_goal_select`
- `life_step1_protecting_view`
- `life_step1_protecting_select`
- `life_step1_coverage_view`
- `life_step1_coverage_select`
- `life_step1_age_view`
- `life_step1_age_continue`
- `life_step1_tobacco_view`
- `life_step1_tobacco_select`
- `life_step1_tobacco_continue`
- `life_processing_bridge_view`
- `life_processing_bridge_complete`
- `life_value_bridge_view`
- `life_value_bridge_continue`
- `life_step2_view`
- `life_step2_back`
- `life_step2_submit_attempt`
- `life_step2_submit_success`
- `results_contact_submit`
- `life_contact_first_view`
- `life_contact_first_start`
- `life_contact_first_submit_attempt`
- `life_contact_first_submit_success`
- `life_contact_first_complete`

### Modal / Lead Capture UX

- `lead_modal_open`
- `lead_modal_close`

### Appointment UX

- `appointment_embed_viewed`
- `appointment_abandoned`
- `appointment_booking_fallback_clicked`

## Tier 2 Rules

- Tier 2 events may inform optimization, but they are not final business truth.
- Tier 2 events may be sampled, reduced, renamed, or consolidated.
- Prefer one canonical event plus metadata over many near-duplicate event names.
- Tier 2 events must not silently become Tier 1 KPIs.

---

# Tier 3 — Diagnostics

## Definition

Tier 3 events exist for engineering, debugging, attribution validation, and telemetry health.

They should never drive official business KPIs.

## Tier 3 Events

### Page Lifecycle / Browser State

- `page_exit`
- `page_visibility_hidden`
- `page_visibility_return`
- `session_end`

### Tracking Health

- `client_tracking_error`
- `meta_browser_event_attempt`
- `meta_browser_event_success`
- `capi_event_attempt`
- `capi_event_success`
- `capi_event_failure`

### Interaction Diagnostics

- `dead_click`
- `rage_click`

## Tier 3 Rules

- Tier 3 events must not count as conversions.
- Tier 3 events must not define funnel success.
- Tier 3 events may be throttled, sampled, or disabled without harming KPI integrity.
- Tier 3 events belong in engineering or diagnostic views, not executive KPI reporting.

---

# Legacy / Compatibility Events

These events may exist for backward compatibility, but they are not preferred going forward.

- `form_submit`
- `cta_clicked`
- `quote_cta_click`
- `submit_failure`
- `lead_form_submit_failure`

## Legacy Rules

- Legacy events may be read for historical compatibility.
- Legacy events should not be used as the canonical source of truth.
- Legacy events should be removed only after queries, dashboards, and historical compatibility are verified.

---

# Removed Events

These must not be reintroduced.

- `form_submit_success`
- `quote_start`

---

# Canonical Funnel Model

The preferred canonical funnel is:

1. `page_view`
2. `quote_click`
3. `form_start`
4. `contact_step_view`
5. `form_submit_attempt`
6. `lead_form_submit_success`
7. `lead_persisted`

Everything else is supporting telemetry, diagnostic context, or derived reporting.

---

# Governance Questions Before Adding Any Event

Before adding a new event, answer:

1. Is this Tier 1, Tier 2, or Tier 3?
2. Is this event canonical or supporting telemetry?
3. Can this be metadata on an existing event?
4. Could this create duplicate KPI meaning?
5. Does this improve signal-to-noise ratio?
6. Will this event still make sense 12 months from now?
7. Which dashboard or query will consume it?
8. What happens if this event fires twice?
9. What happens if this event never fires?

If the answer is unclear, do not add the event yet.
