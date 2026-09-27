# Canonical event/provider parity audit — 2026-09-27

**Historical snapshot, superseded by [normalized behavior parity](20260927-normalized-behavior-parity.md).**

Source-derived working-tree inventory. **This is a mapping audit, not proof of provider acceptance or final SHA validation.** Provider decoupling and commerce/CRM lineage work were active while this snapshot was prepared. Recheck matrix after final changes.

## Authority and identity conventions

- **O — owner:** permanent MarketingOwnerScope Founder/Agent/Business, resolved server-side; typed AgentTrackingProfileId or CommerceBusinessId plus applicable publication/binding lineage. A disconnected owner gets no other owner’s account, Pixel or credentials.
- **B — browser identity:** ClientEventId plus canonical owner/site/event/session/visitor/page identity; UnifiedAnalyticsWriter browser persistence rejects cross-owner/replayed conflicting identity. Duplicate receipts use the persisted EventId. Provider projections must reuse this event ID rather than create an unrelated conversion.
- **S — confirmed outcome identity:** stable canonical AnalyticsEvent/event/client ID, lead/CRM/booking/order/purchase outcome identity and permanent owner. Meta bridge and OpenAI projection must retain that same identity; retries use provider delivery receipts, not new source events.
- Browser observation is not confirmation authority. Form success/booking UI events alone must never assert a persisted Lead, completed appointment, issued policy, paid policy or Purchase.
- Provider eligibility additionally requires the exact owner’s configured connection and consent/quality/dispatch policy; “yes” here means catalog eligibility, not successful sending.
- Meta browser flags are from MetaSignalEventCatalog; server flags are from that catalog plus bridge mapping. Advertising destinations are from MarketingConversionDestinationCatalog. The OpenAI browser implementation currently maps only page_view. Meta browser projections subscribe to accepted canonical envelopes; the table describes eligibility, not external acceptance.

## Eligibility and resolved gaps

1. page_view and ProductViewed now project to Meta ViewContent through the shared alias catalog. No independent page ViewContent source is generated.
2. Generic form starts/contact/submit attempts map through canonical metric definitions; accepted source envelopes drive browser projections with the same ID.
3. The canonical source catalog owns unique behavioral signals and confirmed conversion names. The Meta catalog describes provider eligibility, not transport or event creation.
4. OpenAI browser maps only page_view to page_viewed. Other browser measurements have no supported mapping here; canonical server conversions use the destination catalog independently.
5. Browser appointment_booked remains observational and is provider-ineligible without server confirmation. A name alias does not establish authority.
6. Custom OpenAI outcomes retain the standard/custom and optimization eligibility of MarketingConversionDestinationCatalog; no standard-optimization claim is implied.
7. Published old script snapshots require the deployment migration gate below.

## Required meaningful events

The complete matrix below includes all AnalyticsEventCatalog definitions, all MetaSignalEventCatalog entries, explicit bridge outcome names, and ProductViewed/CheckoutStarted commerce names. It covers page views; every engagement checkpoint; scroll; CTA; form start/progress; attempts; Lead/QualifiedLead; AppointmentBooked/Completed; ApplicationSubmitted; PolicyIssued/Paid; ViewContent; ProductViewed; AddToCart; CheckoutStarted/InitiateCheckout; Purchase.

| Canonical/source event name | Dashboard metric membership | Meta alias/provider name | Meta browser eligibility | Meta CAPI eligibility | OpenAI measurement name | OpenAI browser eligibility | OpenAI server eligibility | Confirmation authority | Dedupe | Owner |
|---|---|---|---|---|---|---|---|---|---|---|
| `page_view` | page_view, landing_view | ViewContent | named signal eligible; alias does not itself emit Pixel | no | page_viewed | yes | no | B: observed browser action; not a verified conversion | B | O |
| `page_engaged_5s` | engaged_5s | SessionEngaged5s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_engaged_10s` | engaged_10s | SessionEngaged5s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_engaged_15s` | engaged_15s | SessionEngaged15s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_engaged_30s` | engaged_30s | SessionEngaged15s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_engaged_60s` | engaged_60s | SessionEngaged15s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `scroll_depth_25` | scroll_depth_25 | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `scroll_depth_50` | scroll_depth_50 | MeaningfulScroll | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `scroll_depth_75` | scroll_depth_75 | MeaningfulScroll | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `scroll_depth_90` | scroll_depth_90 | MeaningfulScroll | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `scroll_depth_100` | scroll_depth_100 | MeaningfulScroll | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `MeaningfulScroll` | MeaningfulScroll | MeaningfulScroll | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `cta_click` | cta_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_click` | quote_click, cta_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_form_start` | lead_form_start, form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `LeadFormStart` | LeadFormStart | LeadFormStart | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_field_focus` | field_focus | ContactInputStarted | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_field_complete` | field_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `ContactInputStarted` | ContactInputStarted | ContactInputStarted | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `PhoneFieldCompleted` | PhoneFieldCompleted | PhoneFieldCompleted | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `RequiredContactFieldsCompleted` | RequiredContactFieldsCompleted | RequiredContactFieldsCompleted | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_submit_attempt` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_form_submit_attempt` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `SubmitAttempt` | SubmitAttempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `website_lead_submitted` | submit_success, confirmed_lead, lead_persisted | Lead | no | conditional S | lead_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `lead_persisted` | lead_persisted, confirmed_lead | Lead | no | conditional S | lead_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `Lead` | Lead | Lead | no | conditional S | lead_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `qualified_lead` | NOT IN AnalyticsEventCatalog; reporting requires explicit query/projection | QualifiedLead | no | conditional S | QualifiedLead | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `QualifiedLead` | QualifiedLead | QualifiedLead | no | conditional S | QualifiedLead | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `appointment_booked` | appointment_booked | AppointmentBooked | no | conditional S | appointment_scheduled | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `AppointmentBooked` | AppointmentBooked | AppointmentBooked | no | conditional S | appointment_scheduled | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `appointment_completed` | appointment_completed | AppointmentCompleted | no | conditional S | AppointmentCompleted | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `AppointmentCompleted` | AppointmentCompleted | AppointmentCompleted | no | conditional S | AppointmentCompleted | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `application_submitted` | NOT IN AnalyticsEventCatalog; reporting requires explicit query/projection | ApplicationSubmitted | no | conditional S | ApplicationSubmitted | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `ApplicationSubmitted` | ApplicationSubmitted | ApplicationSubmitted | no | conditional S | ApplicationSubmitted | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `policy_issued` | NOT IN AnalyticsEventCatalog; reporting requires explicit query/projection | PolicyIssued | no | conditional S | PolicyIssued | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `PolicyIssued` | PolicyIssued | PolicyIssued | no | conditional S | PolicyIssued | no | conditional S; custom/non-optimization | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `policy_paid` | NOT IN AnalyticsEventCatalog; reporting requires explicit query/projection | Purchase | no | conditional S | order_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `PolicyPaid` | PolicyPaid | Purchase | no | conditional S | order_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `ViewContent` | ViewContent | ViewContent | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `ProductViewed` | none | ViewContent | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `AddToCart` | AddToCart | AddToCart | no | conditional S | items_added | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `CheckoutStarted` | NOT IN AnalyticsEventCatalog; reporting requires explicit query/projection | InitiateCheckout | no | conditional S | checkout_started | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `InitiateCheckout` | InitiateCheckout | InitiateCheckout | no | conditional S | checkout_started | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `Purchase` | Purchase | Purchase | no | conditional S | order_created | no | conditional S | S: canonical persisted lead/CRM/booking/order outcome; catalog signal alone insufficient | S | O |
| `AbandonedHighIntentLead` | AbandonedHighIntentLead | AbandonedHighIntentLead | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `Backtrack` | Backtrack | Backtrack | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `ContactStepReached` | ContactStepReached | ContactStepReached | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `DeadClick` | DeadClick | DeadClick | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `DiscoveryComplete` | DiscoveryComplete | DiscoveryComplete | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `FieldError` | FieldError | FieldError | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `FunnelStepComplete` | FunnelStepComplete | FunnelStepComplete | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `HighIntentLeadSignal` | HighIntentLeadSignal | HighIntentLeadSignal | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `LeadReadySignal` | LeadReadySignal | LeadReadySignal | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `RageClick` | RageClick | RageClick | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `RapidBounce` | RapidBounce | RapidBounce | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `RecommendationViewed` | RecommendationViewed | RecommendationViewed | yes | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `SessionEngaged15s` | SessionEngaged15s | SessionEngaged15s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `SessionEngaged5s` | SessionEngaged5s | SessionEngaged5s | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `age_completed` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_abandoned` | appointment_abandoned | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_booking_fallback_clicked` | appointment_booking_fallback_clicked | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_cancelled` | appointment_cancelled | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_embed_viewed` | appointment_embed_viewed | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_no_show` | appointment_no_show | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_rescheduled` | appointment_rescheduled | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `appointment_slot_selected` | appointment_slot_selected | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `capi_event_attempt` | capi_event_attempt | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `capi_event_failure` | capi_event_failure | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `capi_event_success` | capi_event_success | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `carrier_trust_strip_view` | trust_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `client_tracking_error` | tracking_error, tracking_health | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `contact_step_view` | contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `contact_step_viewed` | contact_step_view, quote_contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `cta_clicked` | cta_click, quote_cta_click, primary_cta_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `dead_click` | dead_click | DeadClick | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `disability_quote_contact_step_view` | contact_step_view, quote_contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `disability_quote_step1_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `estimate_contact_continue` | contact_step_view, quote_contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `estimate_inline_contact_view` | estimate_inline_contact_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `estimate_results_error` | estimate_results_error | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `estimate_results_viewed` | recommendation_viewed, estimate_results_viewed | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `file_download` | file_download | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `first_question_answered` | first_question_answered, quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `first_question_view` | first_question_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_abandon` | form_abandon | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_field_error` | field_error, validation_friction | FieldError | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_submit` | submit_attempt, form_submit | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `form_submit_success` | submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `funnel_started` | funnel_start | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `goal_completed` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_form_submit_failure` | submit_failure | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_form_submit_success` | submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_modal_close` | lead_modal_close | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `lead_modal_open` | lead_modal_open | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_contact_first_complete` | life_contact_first_complete, submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_contact_first_start` | life_contact_first_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_contact_first_submit_attempt` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_contact_first_submit_success` | submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_contact_first_view` | life_contact_first_view, landing_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_finalexpense_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_finalexpense_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_general_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_general_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_iul_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_iul_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_mp_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_mp_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_processing_bridge_complete` | processing_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_processing_bridge_view` | processing_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_age_continue` | discovery_complete, quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_age_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_coverage_select` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_coverage_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_goal_select` | first_question_answered, quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_goal_view` | first_question_view, step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_intro_view` | form_shell_view, first_question_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_protecting_select` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_protecting_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_tobacco_continue` | discovery_complete, quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_tobacco_select` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step1_tobacco_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step2_back` | backtrack | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step2_submit_attempt` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step2_submit_success` | submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_step2_view` | contact_step_view, quote_contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_term_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_term_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_value_bridge_continue` | processing_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_value_bridge_view` | processing_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_whole_form_start` | form_start, funnel_start | LeadFormStart | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `life_whole_submit` | submit_attempt | SubmitAttempt | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `meta_browser_event_attempt` | meta_browser_event_attempt | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `meta_browser_event_success` | meta_browser_event_success | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `mini_results_view` | recommendation_viewed | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `outbound_click` | outbound_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_exit` | page_exit | RapidBounce | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_visibility_hidden` | page_visibility_hidden | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `page_visibility_return` | page_visibility_return | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `primary_cta_seen` | primary_cta_seen | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `processing_bridge_viewed` | processing_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `protecting_who_completed` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_contact_step_view` | contact_step_view, quote_contact_step_view | ContactStepReached | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_cta_click` | cta_click, quote_cta_click, primary_cta_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_entry_engaged` | quote_entry_engaged, funnel_start | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_landing_view` | landing_view, quote_landing_view | ViewContent | named signal eligible; alias does not itself emit Pixel | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_step_complete` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `quote_step_view` | step_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `rage_click` | rage_click | RageClick | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `recommendation_generated` | recommendation_viewed | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `recommendation_viewed` | recommendation_viewed, estimate_results_viewed | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `results_contact_submit` | submit_success, confirmed_lead | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `risk_assessment_click` | risk_assessment_click | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `section_view` | section_view | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `session_end` | session_end | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `step1_age_entered` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `submit_failure` | submit_failure | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `thank_you_view` | thank_you_view, funnel_completion | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `tobacco_completed` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `tobaccouse_completed` | quote_step_complete | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `workstation_capture_attempt` | workstation_capture_attempt | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `workstation_capture_failure` | workstation_capture_failure | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |
| `workstation_capture_success` | workstation_capture_success | — | no | no | — | no | no | B: observed browser action; not a verified conversion | B | O |

## Source trace

- `SHARED/Analytics/AnalyticsEventCatalog.cs` — accepted first-party names, browser/server flags and dashboard metric membership.
- `SHARED/Analytics/MetaSignalEventCatalog.cs` — named signal browser/server flags and server authority classification.
- `SHARED/Analytics/MetaSignalAnalyticsAliasCatalog.cs` — engagement/scroll/friction/commerce aliases.
- `SHARED/Analytics/MarketingConversionDestinationCatalog.cs` — provider names, standard/custom and optimization flags.
- `SHARED/Analytics/OpenAiMeasurementContracts.cs` — provider standard-name constants.
- `SHARED/WebsitePlatform/openai-measurement.js` — actual browser map.
- `Infrastructure/Analytics/MetaSignalAnalyticsBridge.cs` — lead/outcome/landing aliases and source enumeration.
- `Infrastructure/Analytics/OpenAiMeasurementDelivery.cs` — server mapping and delivery (under active decoupling repair).
- `Infrastructure/Analytics/WebsiteTrackingProxyAuthority.cs` — sole browser ingest, including verified commerce scope; `Infrastructure/Commerce/CommerceSignalService.cs` — canonical server commerce outcomes.

RapidBounce alias is conditional: page_exit requires bounce candidate or dwell below10s, dwell below10s, engagement below5s, and scroll below35%. Engagement aliases collapse5s/10s to SessionEngaged5s and15s/30s/60s to SessionEngaged15s; those are separate source checkpoints, not permission to invent duplicate source events.

No row proves actual destination delivery. Final evidence must attach canonical ID → scoped destination → delivery attempt/result for each eligible scope, independently for both providers.
## Canonical transport revision (working tree)

The runtime now exports its sole source allowlist from `AnalyticsEventCatalog`. The catalog explicitly includes the unique engagement/funnel signals and ProductViewed, plus the shared authoritative conversion definitions. Meta provider names are resolved only through `MetaSignalAnalyticsAliasCatalog.ResolveSignalName`; the browser projection map is exported with the canonical runtime config. Page views map to ViewContent; form starts/contact/submit metrics map by canonical definition. OpenAI browser page views remain page_viewed. Unsupported provider mappings remain absent rather than manufacturing events.

`tracking.js` publishes accepted canonical envelopes to keyed provider subscribers. Late-loaded providers replay the accepted bounded page history. Meta and OpenAI use the source ClientEventId. Meta's unique behavioral signals call `LegendAnalytics.track` with that same stable ID; they have no independent fetch/beacon transport. The retired `/analytics/meta-signal` controller is deleted. Its shared authority now only enriches canonical context; it performs no ownership resolution, persistence, or dedupe. `/api/tracking/ingest` validates nested identity and browser authority before the existing mapper/writer chain.

The shipped-JS regression executes tracker plus both providers: one page source, matching IDs in both browser providers, no independently generated ViewContent/LeadFormStart sources, stable-ID unique signal ingestion, and duplicate subscription protection. Browser provider invocation is a diagnostic fact, not proof of external provider acceptance. Confirmed conversion authority remains server-only regardless of alias names.

### Deployment migration gate

Business publication HTML normally references fixed asset paths whose bytes come from current `WebsiteCompiler/dist`; it does not ordinarily embed an old runtime. Rebuild/deploy current assets, invalidate stale caches, and inventory active plus rollback-eligible snapshots for exceptional inline/alternate retired code. Selectively recompile exceptional publications into new versions. See `20260927-runtime-migration-gate.md` for exact source contracts and predeployment commands. No deployment or republish occurred.

Managed form/action bindings now enrich the same source envelope before transport, preserving every matched binding's identity, element, trigger, event name, delivery mode, and once-per-session policy. Meta projects those binding choices with the canonical event ID; analytics-only mappings suppress browser Pixel for that action. Element impressions and other independently observed configured signals retain their own stable canonical identity.

### Action presentation normalization

Managed navigation/CTA clicks persist the canonical browser event plus explicit ActionKey; visible text is presentation metadata only. Default `cta_click` has no Meta or OpenAI destination mapping. No advertising conversion is fabricated for a raw custom link or CTA click. Form focus/submit and cart/checkout presets delegate their existing native lifecycle or server command; their subsequent verified outcomes use the existing outcome mappings.

New template element IDs use structural page/tag/ordinal identity. A read-only lookup translates historical label-derived IDs to the corresponding structural node and retains its saved binding identity. Changing template wording does not produce a new binding. Structural template reordering still requires publication migration review; this is not a claim that ordinal IDs survive arbitrary template restructuring.
