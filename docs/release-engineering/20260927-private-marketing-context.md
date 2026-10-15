# Canonical private marketing context

## Implemented

The AI data builder, payload contract and privacy projection now live in Infrastructure/SHARED. The Agent Portal AI review, copy snapshot, Marketing Manager planning response, and authenticated agent/business context APIs use that projection. It reads existing analytics queries and unified channel performance; no new event store, credential store or attribution table was introduced.

The context includes website traffic, page/CTA conversion, intent/quote funnels, dwell/exits, source performance, abandonment, device/browser/OS/viewport/language aggregates, journey summaries, pipeline health, Meta and ChatGPT delivery and canonical CRM outcome totals, plus authorized published offering metadata. Founder promotion inventory now distinguishes LEGEND and Protect published pages.

Customer records, contact details, session/visitor IDs, raw event metadata and raw provider responses are not exported. Untrusted analytics labels use stable resource aliases. Published offering metadata is a separate allowlisted input; email/phone-shaped public values are removed and public paths lose query/fragment tokens. Scope is resolved by the existing canonical owner authority; global/team, mixed and mismatched advertiser scopes fail closed. No shared advertiser fallback is introduced.

AI review no longer diagnoses a landing-page failure from zero leads alone. Both channel evidence and limitations inform recommendations. Oversized review requests fail explicitly instead of truncating JSON. Marketing Manager no longer runs concurrent operations against one scoped DbContext or rebuilds its aggregate evidence independently.

Existing canonical event projections remain the only delivery path. Tests verify that confirmed Meta-acquired, ChatGPT-acquired and direct leads can project to both providers with the same event identity; provider configuration and eligibility still apply. Receipt acceptance does not assign campaign credit, and overlapping provider conversions must not be summed as unique customers. No ad campaign or budget was activated by this code change.

## Access and limits

Authorized users can read `/WebsiteAnalytics/marketing-manager/context` (agent/Founder) or `/business/{businessId}/analytics/marketing-manager/context` (business). Business exports also use the existing snapshot shape through `/business/{businessId}/analytics/ai-review-snapshot`. Existing session authorization and business capability checks remain required; the Ads Manager connector does not automatically obtain access to these private endpoints.

Aggregate queries retain their dashboard population and bounds. Some detail lists are limited to 100 rows; summary totals cover the selected window. Provider windows/availability and traffic-quality differences are disclosed. This is decision support, not a guarantee of improved returns. Provider mutations continue through exact-action approval and execution.

## Release scope

Portal, Client, Protect and Parfait contain the modified shared runtime and require deployment. The static LEGEND website is preserved. Existing consent/reporting repairs and release integration safeguards are included. Store routing and final live Founder journeys still require post-release verification; they are not certified by local tests.
