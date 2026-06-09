# Analytics Event Consolidation Roadmap

## Purpose

This document defines the controlled migration plan for reducing semantic duplication and telemetry noise without corrupting analytics integrity.

This roadmap exists to ensure:
- KPI continuity
- historical compatibility
- stable dashboards
- safe telemetry reduction
- deterministic migration sequencing

No event should be removed blindly.

---

# Consolidation Philosophy

Preferred architecture direction:

- fewer canonical events
- richer metadata
- lower semantic overlap
- reduced frontend event volume
- improved signal-to-noise ratio

Preferred pattern:

Instead of many highly-specific events, prefer one canonical event with metadata describing the variation.

---

# Phase 1 — Governance

Status: COMPLETE

Completed:
- analytics-event-definitions.md
- analytics-event-tiers.md
- canonical funnel governance
- public API boundary
- modular ownership boundaries

---

# Phase 2 — Canonical Funnel Stabilization

Status: COMPLETE

Canonical funnel established:

1. page_view
2. quote_click
3. form_start
4. contact_step_view
5. form_submit_attempt
6. lead_form_submit_success
7. lead_persisted

Deprecated:
- quote_start
- form_submit_success

---

# Phase 3 — Event Consolidation Candidates

## Candidate Group A — CTA Semantics

Current events:
- cta_click
- cta_clicked
- quote_cta_click
- quote_click

Future direction:
- Preferred canonical event: quote_click
- Preferred metadata: ctaType, location, offer, elementKey

Migration risk: Medium

Reason:
CTA events currently overlap semantically.

---

## Candidate Group B — Quote Step Completion Explosion

Current events:
- goal_completed
- protecting_who_completed
- age_completed
- tobacco_completed
- tobaccouse_completed
- quote_step_complete

Future direction:
- Preferred canonical event: quote_step_complete
- Preferred metadata: step, source, stepNumber, nextStep

Migration risk: Low

Reason:
Multiple events currently represent the same conceptual action: step progression.

---

## Candidate Group C — Life Submit Semantics

Current events:
- life_step2_submit_success
- life_contact_first_submit_success
- results_contact_submit
- lead_form_submit_success

Future direction:
- Preferred canonical event: lead_form_submit_success
- Preferred metadata: flowVariant, offer, submissionType

Migration risk: High

Reason:
Several events currently imply successful submission semantics. These must not accidentally inflate lead counts.

---

# Phase 4 — Noise Reduction Candidates

## Candidate Group D — Weak Engagement Events

Current events:
- page_engaged_5s
- page_engaged_10s
- page_engaged_15s

Keep:
- page_engaged_30s
- page_engaged_60s

Reason:
5–15 second engagement windows provide weak strategic signal. 30s+ represents meaningful engagement.

Migration risk: Low

---

## Candidate Group E — Scroll Depth Over-Instrumentation

Current events:
- scroll_depth_25
- scroll_depth_50
- scroll_depth_75
- scroll_depth_90
- scroll_depth_100

Potential future removal:
- scroll_depth_100

Reason:
100% scroll depth rarely changes optimization decisions.

Migration risk: Low

---

# Phase 5 — Diagnostic Isolation

Diagnostic events must remain isolated from KPI logic.

Examples:
- dead_click
- rage_click
- page_visibility_hidden
- page_visibility_return

These events support investigations, telemetry QA, and engineering diagnostics. They are not business outcomes.

---

# Mandatory Rules Before Any Event Removal

Before removing ANY event:

1. Verify dashboard dependencies.
2. Verify analytics query dependencies.
3. Verify AI review dependencies.
4. Verify attribution dependencies.
5. Verify funnel calculations.
6. Verify historical reporting compatibility.
7. Verify no active frontend listener depends on the event.
8. Verify event volume in production.
9. Verify replacement metadata exists.
10. Verify no KPI inflation or collapse occurs.

---

# Removal Process

Safe removal order:

1. Governance
2. Classification
3. Metadata replacement
4. Query migration
5. Dashboard migration
6. Parallel monitoring
7. Deprecation
8. Production verification
9. Removal
10. Post-removal audit

Never skip stages.

---

# Current Recommendation

Do not aggressively remove telemetry before:
- production validation window
- live traffic analysis
- attribution verification
- real-user conversion analysis

The current system is stable enough to publish.

Future optimization should be iterative and evidence-driven.
