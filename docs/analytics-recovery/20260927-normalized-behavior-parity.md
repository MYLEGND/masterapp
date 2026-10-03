# Canonical behavior parity — current normalization

Generated from the compiled existing AnalyticsEventCatalog and downstream provider catalogs. This supersedes the older event-parity mapping snapshot. It proves declared policy, not live delivery. No persisted event names or historical event rows are renamed.

## Shared authority and lineage

Every row uses UnifiedEventMapper → UnifiedAnalyticsWriter → AnalyticsEvents. Owner is one canonical MarketingOwnerScope (Founder, exact Agent profile, or exact CommerceBusinessId). Browser identity is the accepted ClientEventId/EventId; server identity is the trusted producer outcome identity. Publication/version, page, element, immutable ActionKey and binding remain separate from editable text. CRM includes lead/booking lineage; commerce includes order/payment, value/currency and item quantities. Eligible provider projections retain that identity and owner; no connection creates attribution.

## Seventeen core behaviors

| Behavior key | Preserved event | Display label | Automatic producer/trigger | Manual triggers | Authority | Meta mapping | OpenAI mapping |
|---|---|---|---|---|---|---|---|
| page_view | page_view | Page viewed | page_load | viewed | browser observation | ViewContent | page_viewed |
| cta_click | cta_click | Action clicked | managed_action_click | click | browser observation | — | — |
| form_start | form_start | Form started | first_form_interaction | click, form_started | browser observation | LeadFormStart | — |
| contact_input_started | form_field_focus | Contact input started | contact_field_focus | field_started | browser observation | ContactInputStarted | — |
| meaningful_scroll | scroll_depth_50 | Meaningful scroll | scroll_threshold | scroll_threshold | browser observation | MeaningfulScroll | — |
| submit_attempt | form_submit_attempt | Submit attempted | form_submit | submit_attempt | browser observation | SubmitAttempt | — |
| lead_created | website_lead_submitted | Inquiry received | inquiry_persisted | locked; automatic | verified server | Lead | lead_created |
| qualified_lead | QualifiedLead | Inquiry qualified | crm_qualification | locked; automatic | verified server | QualifiedLead | QualifiedLead |
| appointment_booked | appointment_booked | Appointment booked | booking_persisted | locked; automatic | verified server | AppointmentBooked | appointment_scheduled |
| appointment_completed | appointment_completed | Appointment completed | booking_completed | locked; automatic | verified server | AppointmentCompleted | AppointmentCompleted |
| application_submitted | ApplicationSubmitted | Request submitted | crm_submission | locked; automatic | verified server | ApplicationSubmitted | ApplicationSubmitted |
| outcome_completed | PolicyIssued | Business outcome completed | crm_outcome_completed | locked; automatic | verified server | PolicyIssued | PolicyIssued |
| payment_completed | PolicyPaid | Payment completed | payment_confirmed | locked; automatic | verified server | Purchase | order_created |
| product_viewed | ProductViewed | Product viewed | product_page_load | viewed | browser observation | ViewContent | — |
| add_to_cart | AddToCart | Item added to cart | cart_command_accepted | locked; automatic | verified server | AddToCart | items_added |
| checkout_started | InitiateCheckout | Checkout started | checkout_created | locked; automatic | verified server | InitiateCheckout | checkout_started |
| purchase_completed | Purchase | Purchase completed | order_payment_confirmed | locked; automatic | verified server | Purchase | order_created |

Presets delegate runtime commands: Contact/Call/Email/Quote/Schedule navigation, Form Start focus, Submit the actual form, Product the actual product, Add To Cart the server cart command, Checkout the server checkout command. Custom Link is the explicit detach action. Browser cart/checkout clicks cannot manufacture accepted cart/checkout outcomes. PolicyIssued/PolicyPaid remain historical persisted names; their neutral behaviors are outcome_completed/payment_completed and display labels do not select identity.

## Complete source catalog

| Preserved name | Behavior | Browser | Server | Dashboard metrics | Meta alias | Meta browser | Meta server | OpenAI server | Manual binding triggers |
|---|---|---|---|---|---|---|---|---|---|
| page_view | page_view | yes | no | page_view, landing_view | ViewContent | yes | no | — | viewed |
| quote_landing_view | quote_landing_view | yes | no | landing_view, quote_landing_view | ViewContent | yes | no | — | none |
| thank_you_view | thank_you_view | yes | no | thank_you_view, funnel_completion | — | no | no | — | none |
| page_exit | page_exit | yes | no | page_exit | RapidBounce | no | no | — | none |
| page_visibility_hidden | page_visibility_hidden | yes | no | page_visibility_hidden | — | no | no | — | none |
| page_visibility_return | page_visibility_return | yes | no | page_visibility_return | — | no | no | — | none |
| page_engaged_5s | page_engaged_5s | yes | no | engaged_5s | SessionEngaged5s | no | no | — | none |
| page_engaged_10s | page_engaged_10s | yes | no | engaged_10s | SessionEngaged5s | no | no | — | none |
| page_engaged_15s | page_engaged_15s | yes | no | engaged_15s | SessionEngaged15s | no | no | — | none |
| page_engaged_30s | page_engaged_30s | yes | no | engaged_30s | SessionEngaged15s | no | no | — | none |
| page_engaged_60s | page_engaged_60s | yes | no | engaged_60s | SessionEngaged15s | no | no | — | none |
| scroll_depth_25 | scroll_depth_25 | yes | no | scroll_depth_25 | — | no | no | — | none |
| scroll_depth_50 | meaningful_scroll | yes | no | scroll_depth_50 | MeaningfulScroll | no | no | — | scroll_threshold |
| scroll_depth_75 | meaningful_scroll | yes | no | scroll_depth_75 | MeaningfulScroll | no | no | — | scroll_threshold |
| scroll_depth_90 | meaningful_scroll | yes | no | scroll_depth_90 | MeaningfulScroll | no | no | — | scroll_threshold |
| scroll_depth_100 | meaningful_scroll | yes | no | scroll_depth_100 | MeaningfulScroll | no | no | — | scroll_threshold |
| cta_click | cta_click | yes | no | cta_click | — | no | no | — | click |
| quote_cta_click | cta_click | yes | no | cta_click, quote_cta_click, primary_cta_click | — | no | no | — | click |
| cta_clicked | cta_click | yes | no | cta_click, quote_cta_click, primary_cta_click | — | no | no | — | click |
| primary_cta_seen | primary_cta_seen | yes | no | primary_cta_seen | — | no | no | — | none |
| quote_entry_engaged | quote_entry_engaged | yes | no | quote_entry_engaged, funnel_start | — | no | no | — | none |
| quote_click | cta_click | yes | no | quote_click, cta_click | — | no | no | — | click |
| quote_step_complete | quote_step_complete | yes | no | quote_step_complete | — | no | no | — | none |
| quote_contact_step_view | quote_contact_step_view | yes | no | contact_step_view, quote_contact_step_view | ContactStepReached | yes | no | — | none |
| risk_assessment_click | cta_click | yes | no | risk_assessment_click | — | no | no | — | click |
| outbound_click | outbound_click | yes | no | outbound_click | — | no | no | — | none |
| carrier_trust_strip_view | carrier_trust_strip_view | yes | no | trust_view | — | no | no | — | none |
| file_download | file_download | yes | no | file_download | — | no | no | — | none |
| section_view | section_view | yes | no | section_view | — | no | no | — | none |
| session_end | session_end | yes | no | session_end | — | no | no | — | none |
| form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| lead_form_start | form_start | yes | no | lead_form_start, form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| funnel_started | funnel_started | yes | no | funnel_start | — | no | no | — | none |
| lead_modal_open | lead_modal_open | yes | no | lead_modal_open | — | no | no | — | none |
| lead_modal_close | lead_modal_close | yes | no | lead_modal_close | — | no | no | — | none |
| form_field_focus | contact_input_started | yes | no | field_focus | ContactInputStarted | no | no | — | field_started |
| form_field_complete | form_field_complete | yes | no | field_complete | — | no | no | — | field_completed |
| form_field_error | form_field_error | yes | no | field_error, validation_friction | FieldError | no | no | — | validation_failed |
| form_submit_attempt | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| lead_form_submit_attempt | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| form_submit | submit_attempt | yes | no | submit_attempt, form_submit | SubmitAttempt | no | no | — | submit_attempt |
| submit_failure | submit_failure | yes | no | submit_failure | — | no | no | — | none |
| form_abandon | form_abandon | yes | no | form_abandon | — | no | no | — | none |
| form_submit_success | form_submit_success | yes | no | submit_success | — | no | no | — | none |
| lead_form_submit_success | lead_form_submit_success | yes | no | submit_success | — | no | no | — | none |
| lead_form_submit_failure | lead_form_submit_failure | yes | no | submit_failure | — | no | no | — | none |
| website_lead_submitted | lead_created | no | yes | submit_success, confirmed_lead, lead_persisted | Lead | no | yes | lead_created | none |
| lead_persisted | lead_created | no | yes | lead_persisted, confirmed_lead | Lead | no | yes | lead_created | none |
| workstation_capture_attempt | workstation_capture_attempt | no | yes | workstation_capture_attempt | — | no | no | — | none |
| workstation_capture_success | workstation_capture_success | no | yes | workstation_capture_success | — | no | no | — | none |
| workstation_capture_failure | workstation_capture_failure | no | yes | workstation_capture_failure | — | no | no | — | none |
| appointment_embed_viewed | appointment_embed_viewed | yes | no | appointment_embed_viewed | — | no | no | — | none |
| appointment_slot_selected | appointment_slot_selected | yes | no | appointment_slot_selected | — | no | no | — | none |
| appointment_booked | appointment_booked | no | yes | appointment_booked | AppointmentBooked | no | yes | appointment_scheduled | none |
| appointment_confirmation_viewed | appointment_confirmation_viewed | yes | no | appointment_confirmation_viewed | — | no | no | — | none |
| appointment_abandoned | appointment_abandoned | yes | no | appointment_abandoned | — | no | no | — | none |
| appointment_booking_fallback_clicked | appointment_booking_fallback_clicked | yes | no | appointment_booking_fallback_clicked | — | no | no | — | none |
| appointment_completed | appointment_completed | no | yes | appointment_completed | AppointmentCompleted | no | yes | AppointmentCompleted | none |
| appointment_no_show | appointment_no_show | no | yes | appointment_no_show | — | no | no | — | none |
| appointment_cancelled | appointment_cancelled | no | yes | appointment_cancelled | — | no | no | — | none |
| appointment_rescheduled | appointment_rescheduled | no | yes | appointment_rescheduled | — | no | no | — | none |
| meta_browser_event_attempt | meta_browser_event_attempt | yes | yes | meta_browser_event_attempt | — | no | no | — | none |
| meta_browser_event_success | meta_browser_event_success | yes | yes | meta_browser_event_success | — | no | no | — | none |
| capi_event_attempt | capi_event_attempt | no | yes | capi_event_attempt | — | no | no | — | none |
| capi_event_success | capi_event_success | no | yes | capi_event_success | — | no | no | — | none |
| capi_event_failure | capi_event_failure | no | yes | capi_event_failure | — | no | no | — | none |
| client_tracking_error | client_tracking_error | yes | yes | tracking_error, tracking_health | — | no | no | — | none |
| disability_quote_step1_view | disability_quote_step1_view | yes | no | step_view | — | no | no | — | none |
| disability_quote_contact_step_view | disability_quote_contact_step_view | yes | no | contact_step_view, quote_contact_step_view | ContactStepReached | yes | no | — | none |
| life_general_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_term_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_whole_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_finalexpense_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_mp_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_iul_form_start | form_start | yes | no | form_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_general_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_term_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_whole_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_finalexpense_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_mp_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_iul_submit | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_step1_intro_view | life_step1_intro_view | yes | no | form_shell_view, first_question_view | — | no | no | — | none |
| first_question_view | first_question_view | yes | no | first_question_view | — | no | no | — | none |
| quote_step_view | quote_step_view | yes | no | step_view | — | no | no | — | none |
| life_step1_goal_view | life_step1_goal_view | yes | no | first_question_view, step_view | — | no | no | — | none |
| first_question_answered | first_question_answered | yes | no | first_question_answered, quote_step_complete | — | no | no | — | none |
| life_step1_goal_select | life_step1_goal_select | yes | no | first_question_answered, quote_step_complete | — | no | no | — | none |
| goal_completed | goal_completed | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_protecting_view | life_step1_protecting_view | yes | no | step_view | — | no | no | — | none |
| life_step1_protecting_select | life_step1_protecting_select | yes | no | quote_step_complete | — | no | no | — | none |
| protecting_who_completed | protecting_who_completed | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_coverage_view | life_step1_coverage_view | yes | no | step_view | — | no | no | — | none |
| life_step1_coverage_select | life_step1_coverage_select | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_age_view | life_step1_age_view | yes | no | step_view | — | no | no | — | none |
| step1_age_entered | step1_age_entered | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_age_continue | life_step1_age_continue | yes | no | discovery_complete, quote_step_complete | — | no | no | — | none |
| age_completed | age_completed | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_tobacco_view | life_step1_tobacco_view | yes | no | step_view | — | no | no | — | none |
| life_step1_tobacco_select | life_step1_tobacco_select | yes | no | quote_step_complete | — | no | no | — | none |
| life_step1_tobacco_continue | life_step1_tobacco_continue | yes | no | discovery_complete, quote_step_complete | — | no | no | — | none |
| tobacco_completed | tobacco_completed | yes | no | quote_step_complete | — | no | no | — | none |
| tobaccouse_completed | tobaccouse_completed | yes | no | quote_step_complete | — | no | no | — | none |
| life_processing_bridge_view | life_processing_bridge_view | yes | no | processing_view | — | no | no | — | none |
| processing_bridge_viewed | processing_bridge_viewed | yes | no | processing_view | — | no | no | — | none |
| life_processing_bridge_complete | life_processing_bridge_complete | yes | no | processing_complete | — | no | no | — | none |
| life_value_bridge_view | life_value_bridge_view | yes | no | processing_view | — | no | no | — | none |
| life_value_bridge_continue | life_value_bridge_continue | yes | no | processing_complete | — | no | no | — | none |
| mini_results_view | mini_results_view | yes | no | recommendation_viewed | — | no | no | — | none |
| recommendation_generated | recommendation_generated | yes | no | recommendation_viewed | — | no | no | — | none |
| estimate_results_viewed | estimate_results_viewed | yes | no | recommendation_viewed, estimate_results_viewed | — | no | no | — | none |
| recommendation_viewed | recommendation_viewed | yes | no | recommendation_viewed, estimate_results_viewed | — | no | no | — | none |
| estimate_inline_contact_view | estimate_inline_contact_view | yes | no | estimate_inline_contact_view | — | no | no | — | none |
| contact_step_view | contact_step_view | yes | no | contact_step_view | ContactStepReached | yes | no | — | none |
| contact_step_viewed | contact_step_viewed | yes | no | contact_step_view, quote_contact_step_view | ContactStepReached | yes | no | — | none |
| estimate_contact_continue | estimate_contact_continue | yes | no | contact_step_view, quote_contact_step_view | ContactStepReached | yes | no | — | none |
| estimate_results_error | estimate_results_error | yes | no | estimate_results_error | — | no | no | — | none |
| life_step2_view | life_step2_view | yes | no | contact_step_view, quote_contact_step_view | ContactStepReached | yes | no | — | none |
| life_step2_back | life_step2_back | yes | no | backtrack | — | no | no | — | none |
| life_step2_submit_attempt | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_step2_submit_success | life_step2_submit_success | yes | no | submit_success | — | no | no | — | none |
| results_contact_submit | results_contact_submit | yes | no | submit_success | — | no | no | — | none |
| life_contact_first_view | life_contact_first_view | yes | no | life_contact_first_view, landing_view | — | no | no | — | none |
| life_contact_first_start | form_start | yes | no | life_contact_first_start, funnel_start | LeadFormStart | yes | no | — | click, form_started |
| life_contact_first_submit_attempt | submit_attempt | yes | no | submit_attempt | SubmitAttempt | no | no | — | submit_attempt |
| life_contact_first_submit_success | life_contact_first_submit_success | yes | no | submit_success | — | no | no | — | none |
| life_contact_first_complete | life_contact_first_complete | yes | no | life_contact_first_complete, submit_success | — | no | no | — | none |
| rage_click | rage_click | yes | no | rage_click | RageClick | no | no | — | none |
| dead_click | dead_click | yes | no | dead_click | DeadClick | no | no | — | none |
| Lead | lead_created | no | yes | Lead | Lead | no | yes | lead_created | none |
| QualifiedLead | qualified_lead | no | yes | QualifiedLead | QualifiedLead | no | yes | QualifiedLead | none |
| AppointmentBooked | appointment_booked | no | yes | AppointmentBooked | AppointmentBooked | no | yes | appointment_scheduled | none |
| AppointmentCompleted | appointment_completed | no | yes | AppointmentCompleted | AppointmentCompleted | no | yes | AppointmentCompleted | none |
| ApplicationSubmitted | application_submitted | no | yes | ApplicationSubmitted | ApplicationSubmitted | no | yes | ApplicationSubmitted | none |
| PolicyIssued | outcome_completed | no | yes | PolicyIssued | PolicyIssued | no | yes | PolicyIssued | none |
| PolicyPaid | payment_completed | no | yes | PolicyPaid | PolicyPaid | no | yes | order_created | none |
| AddToCart | add_to_cart | no | yes | AddToCart | AddToCart | no | yes | items_added | none |
| InitiateCheckout | checkout_started | no | yes | InitiateCheckout | InitiateCheckout | no | yes | checkout_started | none |
| Purchase | purchase_completed | no | yes | Purchase | Purchase | no | yes | order_created | none |
| ViewContent | page_view | yes | no | ViewContent | ViewContent | yes | no | — | viewed |
| RapidBounce | rapid_bounce | yes | no | RapidBounce | RapidBounce | no | no | — | none |
| SessionEngaged5s | session_engaged5s | yes | no | SessionEngaged5s | SessionEngaged5s | no | no | — | none |
| SessionEngaged15s | session_engaged15s | yes | no | SessionEngaged15s | SessionEngaged15s | no | no | — | none |
| MeaningfulScroll | meaningful_scroll | yes | no | MeaningfulScroll | MeaningfulScroll | no | no | — | scroll_threshold |
| LeadFormStart | form_start | yes | no | LeadFormStart | LeadFormStart | yes | no | — | click, form_started |
| DiscoveryComplete | discovery_complete | yes | no | DiscoveryComplete | DiscoveryComplete | yes | no | — | none |
| FunnelStepComplete | funnel_step_complete | yes | no | FunnelStepComplete | FunnelStepComplete | no | no | — | none |
| RecommendationViewed | recommendation_viewed | yes | no | RecommendationViewed | RecommendationViewed | yes | no | — | none |
| ContactStepReached | contact_step_reached | yes | no | ContactStepReached | ContactStepReached | yes | no | — | none |
| ContactInputStarted | contact_input_started | yes | no | ContactInputStarted | ContactInputStarted | no | no | — | field_started |
| PhoneFieldCompleted | phone_field_completed | yes | no | PhoneFieldCompleted | PhoneFieldCompleted | no | no | — | field_completed |
| RequiredContactFieldsCompleted | required_contact_fields_completed | yes | no | RequiredContactFieldsCompleted | RequiredContactFieldsCompleted | no | no | — | none |
| FieldError | field_error | yes | no | FieldError | FieldError | no | no | — | validation_failed |
| SubmitAttempt | submit_attempt | yes | no | SubmitAttempt | SubmitAttempt | no | no | — | submit_attempt |
| HighIntentLeadSignal | high_intent_lead_signal | yes | no | HighIntentLeadSignal | HighIntentLeadSignal | yes | no | — | none |
| LeadReadySignal | lead_ready_signal | yes | no | LeadReadySignal | LeadReadySignal | yes | no | — | none |
| Backtrack | backtrack | yes | no | Backtrack | Backtrack | no | no | — | none |
| DeadClick | dead_click | yes | no | DeadClick | DeadClick | no | no | — | none |
| RageClick | rage_click | yes | no | RageClick | RageClick | no | no | — | none |
| AbandonedHighIntentLead | abandoned_high_intent_lead | yes | no | AbandonedHighIntentLead | AbandonedHighIntentLead | yes | no | — | none |
| ProductViewed | product_viewed | yes | no | product_view | ViewContent | yes | no | — | viewed |

Catalog count: 160 unique definitions. OpenAI browser projection supports page_view → page_viewed only. Unsupported mappings are shown as absent, not fabricated.

## Compatibility and evidence limits

- Historical names resolve through the same catalog; no second semantic catalog was introduced.
- Historical manual outcome bindings are inactive on read. New manual outcome bindings are rejected. Trusted backend inquiry production retains historical binding lineage without granting browser confirmation authority.
- Historical OpenAI Meta-source receipts are read-only resend fences and status evidence. They are not adopted, rewritten, or dispatched as new source events.
- Instance-local cart Session idempotency is not a distributed exactly-once guarantee.
- Event Map status distinguishes recorded evidence from mapping/configuration. Live deployment and provider reconciliation remain a separate authorized gate.
