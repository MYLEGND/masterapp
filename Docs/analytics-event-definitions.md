# Analytics Event Definitions

## Canonical Funnel Events

### quote_click
User intentionally clicks a quote/start CTA.

### form_start
User begins interacting with a quote form.

### form_progress
User advances through a meaningful form step.

### form_submit_attempt
User attempts to submit a form.

### lead_form_submit_success
Confirmed successful lead submission. This is the canonical confirmed lead event.

### lead_form_submit_failed
Lead submission failed.

### form_abandon
User started the form but left before confirmed success.

## Diagnostic Events

### page_view
Page loaded.

### page_exit
Page lifecycle exit signal.

### page_visibility_hidden
Diagnostic visibility state. Does not equal true exit by itself.

### page_visibility_return
Diagnostic return state. Does not equal conversion.

### dead_click
User clicked an inactive or non-functional element.

## Legacy Events

### form_submit
Legacy compatibility only. Do not use as confirmed lead.

### form_submit_success
Removed. Do not reintroduce.

### quote_start
Removed. Use quote_click or form_start.

## Rules

- Confirmed leads must come from lead_form_submit_success or actual Lead records.
- Submit attempts must never count as confirmed leads.
- Diagnostic events must not become business KPIs unless explicitly mapped.
- Every new event must be added here before implementation.
