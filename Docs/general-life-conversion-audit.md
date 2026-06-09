# General Life Conversion Audit

## Scope

- Primary target: `Protect-Website/Views/Quote/Life.cshtml`
- Controller: `Protect-Website/Controllers/LifeQuoteController.cs`
- Estimate/reward logic: `Protect-Website/wwwroot/js/life-estimate-engine.js`
- Browser tracking: `Protect-Website/wwwroot/js/tracking.js`
- Shared event catalog: `SHARED/Analytics/AnalyticsEventCatalog.cs`

## Current Page Structure

- General Life uses the shared `Life.cshtml` funnel shell that also powers Term, Whole Life, Final Expense, Mortgage Protection, and IUL.
- General Life now supports a controlled conversion-focused landing variant: `low_friction_options_v1`.
- The improved General Life flow is:
  - hero with low-pressure options framing
  - immediate visible CTA
  - “what you’ll get first” value strip
  - 5 quick-question discovery flow
  - estimate / recommendation reveal
  - softened contact handoff
  - thank-you redirect with preserved quote context

## Current CTA Flow

- Public landing view tracks `quote_landing_view`.
- Above-the-fold hero CTA and sticky mobile CTA both use `data-cta="life_funnel_start"`.
- CTA visibility now tracks `primary_cta_seen`.
- CTA clicks continue to track `quote_cta_click`.
- CTA action scrolls the user directly into the first question instead of relying on a weaker bridge-only transition.

## Current Form Stages

1. Hero / landing context
2. First question view
3. First question answer
4. Remaining discovery questions
5. Processing bridge
6. Estimate / recommendation reveal
7. Contact step
8. Submit attempt
9. Submit success or failure
10. Thank-you

## Current Mobile Above-the-Fold Layout

- The new General Life variant is now copy-first instead of banner-first.
- The first screen answers:
  - what this is: a personalized life coverage options review
  - why care: protect the people who depend on you
  - what they get: a likely starting point before sharing contact info
  - what to click: a single obvious CTA
- A sticky mobile CTA now appears when the active funnel panel is out of view.
- Tap targets remain large and the first action is visible without forcing the user through a heavy wall of insurance copy.

## User Action Points

- Hero CTA
- Sticky mobile CTA
- First question answer
- Remaining quick answers
- Continue into estimate
- Continue into contact step
- Submit contact info for personalized review

## UX Friction Found

- The old contact-first landing was visually heavier than it was action-oriented.
- Cold traffic could read the page and still not feel a clear first click.
- The original hero relied too much on banner imagery and not enough on immediate action/value.
- The contact step could feel like a sales handoff instead of a continuation of the estimate review.
- Last name and email were previously more likely to feel like unnecessary friction for the General Life paid-traffic use case.

## UX Fixes Landed

- Added `low_friction_options_v1` as a controlled General Life landing variant.
- Reframed hero copy around options exploration instead of application language.
- Added a prominent hero CTA plus sticky mobile CTA.
- Added a visible “what you’ll get first” value strip before contact capture.
- Kept the estimate / recommendation reward loop in front of the contact ask.
- Softened contact-step framing to:
  - “Where should I follow up with your personalized review?”
  - “No pressure. You decide what happens next.”
- For the low-friction General Life variant:
  - `FirstName` and `Phone` remain required
  - `LastName` is optional
  - `Email` is optional

## Tracking Gaps Found

- The funnel had Life-specific progress events, but it did not yet emit the cleaner generic conversion events needed for easier reporting and diagnosis.
- The first question could be shown in the DOM before it was truly visible to the visitor on mobile.
- Contact-step and submit outcome reporting needed clearer generic event names alongside the existing Life-specific events.

## Tracking Fixes Landed

- Added / aligned these browser events:
  - `primary_cta_seen`
  - `first_question_view`
  - `first_question_answered`
  - `contact_step_view`
  - `lead_form_submit_attempt`
  - `lead_form_submit_success`
  - `lead_form_submit_failure`
- Preserved existing Life-specific events:
  - `life_contact_first_view`
  - `life_contact_first_start`
  - `life_contact_first_complete`
  - `life_step1_*`
  - `estimate_results_viewed`
  - `estimate_contact_continue`
  - `estimate_inline_contact_view`
- First-question view is now tied to actual viewport visibility instead of only micro-step rendering.
- Contact-step view is emitted once per session view state.
- Submit success and failure now have explicit generic Life funnel events in addition to the existing canonical submit signals.

## Submit Risks Reviewed

- Submit endpoint preserved: `LifeQuoteController`
- Meta browser + CAPI fallback preserved
- Thank-you redirect path preserved
- Required consent preserved
- Existing server validation preserved for:
  - coverage amount
  - age bounds
  - required contact consent
- General Life friction reduced without weakening the required contact authorization path

## Variant Strategy

- Control path remains intact.
- New controlled paid-traffic test variant:
  - `low_friction_options_v1`
- This keeps comparison clean in Website Analytics without breaking the existing General Life route.

## Manual QA Checklist

- Mobile first screen is readable without pinch/zoom.
- Primary CTA is visible above the fold.
- Sticky mobile CTA appears when the form panel is below the viewport.
- `quote_cta_click` fires on hero CTA and sticky CTA.
- `primary_cta_seen` fires once.
- First question becomes visible quickly after CTA click.
- `first_question_view` fires once when the first question is actually visible.
- `first_question_answered` fires once after the first answer.
- Estimate / recommendation summary appears before contact submission.
- `estimate_results_viewed` fires once.
- `estimate_contact_continue` fires on contact-step handoff.
- `contact_step_view` fires once.
- Required contact validation behaves correctly on mobile.
- `lead_form_submit_attempt` fires on real submit attempt.
- `lead_form_submit_success` fires on successful AJAX submission.
- `lead_form_submit_failure` fires on failed AJAX submission.
- Thank-you page loads with quote context intact.
- Meta Lead fallback remains intact.
- Website Analytics shows the session flowing through landing, CTA, discovery, estimate, contact, and submit stages.

## Readiness

- The General Life funnel is now structured for a clean paid-traffic test.
- Remaining work is manual browser QA and live Meta validation, not a known architectural blocker in the funnel itself.
